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

    // 경계는 \b 대신 ASCII 글자 기준 (한국어는 \w라서 "링크https://"·"192.0.2.23에"에서 \b가 성립하지 않았다, G4 M8 1라운드 a1)
    [GeneratedRegex(@"(?<![A-Za-z0-9+.\-])[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s/?#""'<>]+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    // 큰따옴표로 둘러싼 경로는 닫는 따옴표까지 (Windows 이름에는 큰따옴표가 들 수 없다). 작은따옴표는 이름에 들 수 있어("O'Brien") 경계로 쓰지 않는다
    [GeneratedRegex(@"""(?:\\\\|[A-Za-z]:[\\/])[^""\r\n]*""")]
    private static partial Regex QuotedPathPattern();

    // 따옴표 없는 경로는 이름에 공백·괄호·대괄호·쉼표·세미콜론·작은따옴표가 모두 들 수 있어 어디서 끝나는지 알 수 없다.
    // 그래서 줄 끝(또는 이름에 들 수 없는 큰따옴표·꺾쇠)까지 통째로 가린다 (G4 M8 1~4라운드 a1·a2: 구분 기호로 끝을 정하면 늘 이름 일부가 남았다).
    // 경로 뒤의 같은 줄 글이 함께 가려진다 — 덜 가리는 것보다 낫다
    [GeneratedRegex(@"\\\\[^\r\n""<>]*")]
    private static partial Regex UncPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\r\n""<>]*")]
    private static partial Regex DrivePattern();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:\-])[0-9A-Fa-f]{2}([-:])[0-9A-Fa-f]{2}(?:\1[0-9A-Fa-f]{2}){4}(?![0-9A-Fa-f])|(?<![0-9A-Fa-f.])[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}(?![0-9A-Fa-f])")]
    private static partial Regex MacPattern();

    // IPv4가 든 IPv6 (::ffff:198.51.100.77): IPv6 패턴이 IPv4 앞에서 끊기지 않게 먼저 통째로
    [GeneratedRegex(@"(?<![0-9A-Za-z:])(?:[0-9A-Fa-f]{0,4}:){2,6}\d{1,3}(?:\.\d{1,3}){3}(?![0-9])")]
    private static partial Regex Ipv6With4Pattern();

    // IPv6: 콜론 셋 이상이거나 "::" (시각 13:20:05는 콜론 둘이라 걸리지 않음). 끝 경계의 점은 뒤에 글자가 이어질 때만 막는다
    // (문장 끝 "fe80::1."은 가리고, IPv4가 든 꼴은 앞 패턴이 먼저 처리, G4 M8 2라운드 a1)
    [GeneratedRegex(@"(?<![0-9A-Za-z:])(?:[0-9A-Fa-f]{0,4}:){3,7}[0-9A-Fa-f]{0,4}(?:%[0-9A-Za-z]+)?(?![0-9A-Za-z:]|\.[0-9A-Za-z])|(?<![0-9A-Za-z:])[0-9A-Fa-f]{0,4}::(?:[0-9A-Fa-f]{1,4}:?){0,6}(?:%[0-9A-Za-z]+)?(?![0-9A-Za-z:]|\.[0-9A-Za-z])")]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"(?<![0-9.])\d{1,3}(?:\.\d{1,3}){3}(?![0-9]|\.[0-9])")]
    private static partial Regex Ipv4Pattern();

    // 전화번호 꼴: +나 0으로 시작해 숫자 9~15개가 공백·점·줄표로만 이어진 것 (날짜 "2026-11-13 13시"는 2로 시작해 걸리지 않음).
    // 앞 글자는 숫자만 막는다 ("contact:010-…", "…/02-…"도 가림, G4 M8 3라운드 a1). 시각·날짜는 이 패턴 전에 따로 감싸 둔다 (아래 MaskPhones)
    [GeneratedRegex(@"(?<![\d])(?:\+\d|0)(?:[ .\-]?\d){8,14}(?![\d])")]
    private static partial Regex PhonePattern();

    // 지킬 시각(13:20, 13:20:05)과 날짜(2026-11-13, 2026.11.13, 2026/11/13): 값이 시각·날짜로 맞고, 앞뒤가 숫자·구분 기호로 이어지지 않는
    // 홀로 선 토큰만 (G4 M8 2라운드 a2). 더 긴 숫자 줄의 일부("010-2026-11-13", "0800-12-34-56")는 지키지 않는다 (G4 M8 4라운드 a1)
    [GeneratedRegex(@"(?<![\d:.\-/+])(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?(?![\d]|[:.\-/]\d)|(?<![\d.\-/+])\d{4}([-./])(?:0?[1-9]|1[0-2])\1(?:0?[1-9]|[12]\d|3[01])(?![\d]|[:.\-/]\d)")]
    private static partial Regex DateTimePattern();

    /// <summary>
    /// 전화번호 가림. 지킬 시각·날짜의 글자를 같은 길이의 숫자 아닌 글자로 바꾼 사본에서 전화번호 위치를 찾고, 그 위치만 원문에서 가린다
    /// (원문을 바꿔 끼우지 않아 메모에 어떤 글자가 있어도 같다, G4 M8 4라운드 a3).
    /// </summary>
    private static string MaskPhones(string s)
    {
        var copy = s.ToCharArray();
        foreach (Match m in DateTimePattern().Matches(s))
            for (int k = m.Index; k < m.Index + m.Length; k++) copy[k] = 'x';
        var sb = new StringBuilder(s.Length);
        int last = 0;
        foreach (Match m in PhonePattern().Matches(new string(copy)))
        {
            sb.Append(s, last, m.Index - last).Append(ReportTexts.Masked);
            last = m.Index + m.Length;
        }
        sb.Append(s, last, s.Length - last);
        return sb.ToString();
    }

    /// <summary>메모 패턴 가림 (가리기를 켠 문서에서 메모를 넣을 때). 기록의 식별 값도 가린다 (긴 것부터, 3글자 이하는 단어 경계).</summary>
    public string MaskMemo(string memo)
    {
        string s = memo;
        s = UrlPattern().Replace(s, m => m.Value[..(m.Value.IndexOf("://", StringComparison.Ordinal) + 3)] + ReportTexts.Masked);
        s = EmailPattern().Replace(s, ReportTexts.Masked);
        s = QuotedPathPattern().Replace(s, m => m.Value[0] + ReportTexts.Masked + m.Value[0]);
        s = UncPattern().Replace(s, ReportTexts.Masked);
        s = DrivePattern().Replace(s, ReportTexts.Masked);
        s = MacPattern().Replace(s, ReportTexts.Masked);
        s = Ipv6With4Pattern().Replace(s, ReportTexts.Masked);
        s = Ipv6Pattern().Replace(s, m => m.Value.Count(c => c == ':') >= 2 ? ReportTexts.Masked : m.Value);
        s = Ipv4Pattern().Replace(s, ReportTexts.Masked);
        s = MaskPhones(s);
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
