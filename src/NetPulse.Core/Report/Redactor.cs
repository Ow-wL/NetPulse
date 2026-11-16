using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPulse.Core.Analysis;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Report;

/// <summary>원본 식별 값 하나와 그 종류 (안전망이 걸린 필드 종류만 앱 기록에 남긴다).</summary>
public sealed record Identity(string Kind, string Value);

/// <summary>
/// 식별 정보 가리기 (M8 설계 3.3 [1-1, 1-2], 구현 중 결정 1·2).
/// <list type="bullet">
/// <item>필드별 규칙: 대상 이름 → 역할 + 순번, 주소 → 넣지 않음, 사업자 → "사업자 A·B", 어댑터 → 연결 유형 + 순번, 측정 ID → 시각 부분.</item>
/// <item>메모(넣을 때만): 주소·MAC·이메일·전화번호 꼴·UNC·드라이브 경로·URL 호스트와 기록의 대상 이름·주소·사업자·대상 ID를 "[가림]"으로.</item>
/// <item>안전망 <see cref="Check(ReportDocument)"/>: 내보내기 전에 원본 값이 남았는지 본다.</item>
/// </list>
/// </summary>
public sealed partial class Redactor
{
    private readonly IReadOnlyDictionary<string, string> targetNames;
    private readonly IReadOnlyDictionary<string, string> providers;
    private readonly IReadOnlyDictionary<string, string> adapters;

    public bool Masked { get; }
    public string MeasurementId { get; }
    public IReadOnlyList<Identity> Identities { get; }

    public Redactor(RecordHeader header, LinkEvidenceAdapters adapters, bool masked)
    {
        Masked = masked;
        MeasurementId = header.Id;
        targetNames = RecordViewNames(header.Targets);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in header.Targets.Where(t => t.Enabled).Select(t => t.Provider).Where(p => !string.IsNullOrWhiteSpace(p)))
            if (!map.ContainsKey(p!.Trim())) map[p.Trim()] = "사업자 " + (char)('A' + Math.Min(map.Count, 25));
        providers = map;
        var counts = new Dictionary<string, int>();
        var am = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in adapters.Items.OrderBy(a => a.Alias, StringComparer.Ordinal))
        {
            string kind = ReportTexts.AdapterKind(a.Kind);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            am[a.Alias] = $"{kind} {counts[kind]}";
        }
        this.adapters = am;

        var ids = new List<Identity>();
        foreach (var t in header.Targets)
        {
            ids.Add(new Identity("대상 주소", t.Address.ToString()));
            ids.Add(new Identity("대상 이름", t.Name));
            ids.Add(new Identity("대상 ID", t.Id));
            if (!string.IsNullOrWhiteSpace(t.Provider)) ids.Add(new Identity("사업자 이름", t.Provider));
        }
        foreach (var a in adapters.Items) ids.Add(new Identity("어댑터 별칭", a.Alias));
        if (Tail(header.Id) is { } tail) ids.Add(new Identity("측정 ID", tail));
        Identities = ids.Where(i => Norm(i.Value).Length > 0).ToArray();
    }

    private static IReadOnlyDictionary<string, string> RecordViewNames(IReadOnlyList<Target> targets) => Presentation.RecordView.NeutralNames(targets);

    /// <summary>측정 ID의 임의 꼬리 (밑줄 뒤). 없으면 null.</summary>
    public static string? Tail(string id)
    {
        int i = id.LastIndexOf('_');
        return i >= 0 && i < id.Length - 1 ? id[(i + 1)..] : null;
    }

    // ---------------- 필드 ----------------

    public ReportRun TargetName(Target t) => Masked
        ? ReportRun.Gen(targetNames.GetValueOrDefault(t.Id, t.Role == TargetRole.Local ? "로컬 대상" : "외부 대상"))
        : ReportRun.Rec(t.Name);

    public ReportRun? Address(Target t) => Masked ? null : ReportRun.Rec(t.Address.ToString());

    public ReportRun Provider(Target t)
    {
        if (string.IsNullOrWhiteSpace(t.Provider)) return ReportRun.Gen("기록 없음");
        return Masked ? ReportRun.Gen(providers[t.Provider.Trim()]) : ReportRun.Rec(t.Provider);
    }

    public ReportRun Adapter(string alias, int? interfaceIndex)
    {
        if (alias == "*") return ReportRun.Gen("경로 전체");
        if (Masked) return ReportRun.Gen(adapters.GetValueOrDefault(alias, "어댑터"));
        return ReportRun.Rec(interfaceIndex is int i ? $"{alias} (인터페이스 {i})" : alias);
    }

    public ReportRun Id() => Masked
        ? ReportRun.Gen(MeasurementId.Contains('_') ? MeasurementId[..MeasurementId.LastIndexOf('_')] : MeasurementId.Length > 16 ? MeasurementId[..16] : MeasurementId)
        : ReportRun.Rec(MeasurementId);

    // ---------------- 메모 ----------------

    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s/?#""'<>]+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\\\\[^\s\\]+(\\[^\s]*)?")]
    private static partial Regex UncPattern();

    [GeneratedRegex(@"\b[A-Za-z]:[\\/][^\s]*")]
    private static partial Regex DrivePattern();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}([-:])[0-9A-Fa-f]{2}(?:\1[0-9A-Fa-f]{2}){4}\b|\b[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\b")]
    private static partial Regex MacPattern();

    // IPv6: 콜론 셋 이상이거나 "::" (시각 13:20:05는 콜론 둘이라 걸리지 않음)
    [GeneratedRegex(@"(?<![0-9A-Za-z:])(?:[0-9A-Fa-f]{0,4}:){3,7}[0-9A-Fa-f]{0,4}(?:%\w+)?(?![0-9A-Za-z:])|(?<![0-9A-Za-z:])[0-9A-Fa-f]{0,4}::(?:[0-9A-Fa-f]{1,4}:?){0,6}(?:%\w+)?(?![0-9A-Za-z:])")]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b")]
    private static partial Regex Ipv4Pattern();

    // 전화번호 꼴: +나 0으로 시작해 숫자 9~15개가 공백·점·줄표로만 이어진 것 (날짜 "2026-11-13 13시"는 2로 시작해 걸리지 않음)
    [GeneratedRegex(@"(?<![\d])(?:\+\d|0)(?:[ .\-]?\d){8,14}(?![\d])")]
    private static partial Regex PhonePattern();

    /// <summary>메모 패턴 가림 (가리기를 켠 문서에서 메모를 넣을 때). 기록의 식별 값도 가린다 (긴 것부터, 3글자 이하는 단어 경계).</summary>
    public string MaskMemo(string memo)
    {
        string s = memo;
        s = UrlPattern().Replace(s, m => m.Value[..(m.Value.IndexOf("://", StringComparison.Ordinal) + 3)] + ReportTexts.Masked);
        s = EmailPattern().Replace(s, ReportTexts.Masked);
        s = UncPattern().Replace(s, ReportTexts.Masked);
        s = DrivePattern().Replace(s, ReportTexts.Masked);
        s = MacPattern().Replace(s, ReportTexts.Masked);
        s = Ipv6Pattern().Replace(s, m => m.Value.Count(c => c == ':') >= 2 ? ReportTexts.Masked : m.Value);
        s = Ipv4Pattern().Replace(s, ReportTexts.Masked);
        s = PhonePattern().Replace(s, ReportTexts.Masked);
        foreach (var id in Identities.Where(i => i.Kind != "측정 ID").OrderByDescending(i => i.Value.Length))
            s = ReplaceIdentity(s, id.Value);
        return s;
    }

    private static string ReplaceIdentity(string text, string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return text;
        // 비교는 전각·대소문자를 정리한 글에서, 바꾸기는 원문 위치에서 (정규화가 길이를 바꾸지 않는 문자만 다룬다)
        var folded = Fold(text);
        var needle = Fold(v);
        if (folded.Length != text.Length || needle.Length == 0) return text.Replace(v, ReportTexts.Masked, StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        int i = 0, last = 0;
        while ((i = folded.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            if (needle.Length <= 3 && !Bounded(folded, i, needle.Length)) { i++; continue; }
            sb.Append(text, last, i - last).Append(ReportTexts.Masked);
            i += needle.Length;
            last = i;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    // ---------------- 안전망 ----------------

    /// <summary>비교용 정리: 전각 → 반각(NFKC), 소문자. 길이를 바꾸지 않는 경우가 대부분이다.</summary>
    public static string Fold(string s) => s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    public static string Norm(string s) => Fold(s.Trim());

    private static bool Bounded(string text, int at, int length)
    {
        bool before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
        bool after = at + length >= text.Length || !char.IsLetterOrDigit(text[at + length]);
        return before && after;
    }

    /// <summary>원본 값이 글 안에 있는지 (정리 뒤 비교, 3글자 이하는 단어 경계 일치만).</summary>
    public static bool Contains(string text, string value)
    {
        var hay = Fold(text);
        var needle = Norm(value);
        if (needle.Length == 0) return false;
        int i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            if (needle.Length > 3 || Bounded(hay, i, needle.Length)) return true;
            i++;
        }
        return false;
    }

    /// <summary>
    /// 안전망 (설계 3.3, 구현 중 결정 2). 가리기를 켠 문서에서:
    /// 기록·메모에서 온 조각, 문서 제목·파일 이름, 그림 제목·설명은 모든 원본 값으로,
    /// 생성 문구 조각은 대상 주소와 측정 ID 꼬리로만 (고정 문구의 "공유기"와 이름 "공유기"를 구별) 검사한다.
    /// 걸린 값의 종류를 돌려준다 (없으면 빈 목록). 가리지 않은 문서는 검사하지 않는다.
    /// </summary>
    public IReadOnlyList<string> Check(ReportDocument d)
    {
        if (!d.Options.Masked) return [];
        var strong = Identities.Where(i => i.Kind is "대상 주소" or "측정 ID").ToArray();
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        void Scan(string text, IEnumerable<Identity> list)
        {
            foreach (var id in list) if (Contains(text, id.Value)) hits.Add(id.Kind);
        }
        Scan(d.Title, Identities);
        Scan(d.FileName, Identities);
        foreach (var r in d.Texts()) Scan(r.Text, r.Origin == TextOrigin.Generated ? strong : Identities);
        foreach (var s in d.Sections)
            foreach (var b in s.Blocks)
                if (b is ReportFigure f) { Scan(f.Figure.Title, Identities); Scan(f.Figure.Description, Identities); }
        return hits.ToArray();
    }

    /// <summary>HTML의 모든 속성 값 검사 (속성은 고정 이름·스키마 값만 쓰므로 주소와 측정 ID 꼬리로).</summary>
    public IReadOnlyList<string> CheckAttributes(string html)
    {
        var strong = Identities.Where(i => i.Kind is "대상 주소" or "측정 ID").ToArray();
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, value) in ReportHtml.Attributes(html))
            foreach (var id in strong)
                if (Contains(value, id.Value)) hits.Add(id.Kind);
        return hits.ToArray();
    }
}

/// <summary>기록의 어댑터 목록 (별칭·인터페이스 번호·종류). 분석 결과의 링크 근거에서.</summary>
public sealed record LinkEvidenceAdapters(IReadOnlyList<Evidence.AdapterAt> Items)
{
    public static LinkEvidenceAdapters From(AnalysisResult a) => new(a.Links.Adapters.Values.ToArray());
}
