using System.Globalization;
using NetPulse.Core.Analysis;
using NetPulse.Core.Evidence;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;

namespace NetPulse.Core.Report;

/// <summary>
/// 리포트 문구 표 (M8 설계 3.2). 관측 문장만 쓰고 책임을 단정하지 않는다. 판정 이름은 기획서 6.2 그대로 (<see cref="RecordView.VerdictName"/>).
/// </summary>
public static class ReportTexts
{
    /// <summary>단정하는 말 (G2 금지어). 생성 문구 어디에도 없어야 한다.</summary>
    public static readonly string[] Forbidden = ["통신사 장애", "장애", "회선 불량", "불량", "고장", "원인은", "때문에", "탓", "책임이 있", "잘못"];

    public const string Title = "NetPulse 측정 리포트";

    public const string Intro =
        "이 문서는 이 PC에서 관측한 응답·지연·링크 상태의 기록입니다. 관측한 사실만 적고, 끊김이 어느 장비나 회선에서 생겼는지 단정하지 않습니다.";

    public const string PrintHint = "브라우저의 인쇄에서 PDF로 저장할 수 있습니다 (A4 세로).";

    public const string Scope =
        "이 리포트가 말하는 것: 각 대상이 응답했는지, 응답이 얼마나 걸렸는지, 이 PC의 링크가 끊긴 것이 관측됐는지입니다. "
        + "말하지 않는 것: 끊김이 어느 장비·회선·사업자 쪽에서 생겼는지입니다. 구간 후보는 PC → 공유기 → 인터넷 개념도 위의 후보이며 실제 경로를 증명하지 않습니다.";

    public const string MaskedNote = "식별 정보를 가린 리포트입니다. 대상은 역할과 순번으로, 사업자는 서로 다른지만, 측정 ID는 시각 부분만 적었습니다. 날짜와 시각은 그대로입니다.";

    public const string UnmaskedNote = "가리지 않은 리포트입니다. 대상 이름·주소·사업자 이름이 그대로 들어 있습니다. 다른 사람에게 보낼 때는 가린 리포트를 만드세요.";

    public const string MemoNote = "증상 메모가 들어 있습니다. 주소·이메일·전화번호·경로·URL의 호스트와 대상 이름·주소를 [가림]으로 바꿨지만, 사람 이름·와이파이 이름·컴퓨터 이름 같은 글은 가리지 못합니다.";

    public const string Masked = "[가림]";

    public static string Method(int locals, int externals, int intervalMs, int timeoutMs) =>
        $"측정 방법: {Sec(intervalMs)}초마다 로컬 대상 {locals}개와 외부 대상 {externals}개에 응답을 요청하고, {Sec(timeoutMs)}초 안에 답이 없으면 응답 없음으로 셉니다.";

    public static string Sec(int ms) => (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);

    // ---------------- 절 제목 ----------------

    public static string SectionTitle(int number, string id) => $"{number}. " + id switch
    {
        "summary" => "요약",
        "timeline" => "타임라인",
        "episodes" => "끊김 목록",
        "evidence" => "관측 근거",
        "details" => "끊김 상세",
        "marks" => "증상 표시와 메모",
        "diagnoses" => "측정 중 진단",
        _ => "측정 조건",
    };

    // ---------------- 끊김 ----------------

    /// <summary>
    /// 끊김 길이 (M8 설계 3.1 [1-9]). 기록 끝에서 열린 끊김: 끝난 기록은 "측정 끝까지 이어짐 (n초 이상)",
    /// 끝 기록 없음은 "마지막 관측까지 이어짐, 그 뒤는 알 수 없음 (n초 이상)". "이어지는 중"은 쓰지 않는다 (측정 중 화면의 말).
    /// </summary>
    public static string Duration(long minMs, long? maxMs, int rounds, bool openAtEnd, RecordLifecycle life)
    {
        if (openAtEnd && maxMs is null)
            return life == RecordLifecycle.NoSummary
                ? $"마지막 관측까지 이어짐, 그 뒤는 알 수 없음 ({TimelineModel.Seconds(minMs)} 이상)"
                : $"측정 끝까지 이어짐 ({TimelineModel.Seconds(minMs)} 이상)";
        return RecordView.Duration(minMs, maxMs, rounds);
    }

    public static string EmptyEpisodes(Coverage c, Grade g) =>
        c.JudgeableRounds == 0 ? "끊김을 관측하지 못했습니다 (판단할 수 있는 관측이 없음)."
        : g is Grade.Insufficient ? "끊김이 관측되지 않았습니다. 다만 관측이 충분하지 않아 놓친 끊김이 있을 수 있습니다."
        : "이 측정에서는 끊김이 관측되지 않았습니다.";

    public static string EpisodeCount(int count, Coverage c, Grade g) =>
        count > 0 ? $"{count}번" : c.JudgeableRounds == 0 ? "관측하지 못함" : g is Grade.Insufficient ? "관측되지 않음 (관측 부족)" : "관측되지 않음";

    public static string ListCapTitle(int shown, int total) => $"길이가 긴 끊김 {shown}개 (전체 {total}개)";

    public static string ListCapNote(int total, int shown) =>
        $"끊김 {total}개 가운데 길이가 긴 {shown}개를 시간순으로 적었습니다 (최소 길이가 긴 순, 같으면 먼저 시작한 것; 끝을 모르는 끊김은 최소 길이로). "
        + $"나머지 {total - shown}개는 위 판정별 통계에 들어 있습니다.";

    public static string MoreParts(int n) => $"나머지 하위 구간 {n}개";

    public static string Candidate(Candidate c) => RecordView.CandidateText(c);

    // ---------------- 링크 근거 (R2) ----------------

    /// <summary>R2 네 상태 (기획서 6.2): 수집 미지원 / 접근 거부 / 관측했으나 이벤트 없음 / 끊김 관측.</summary>
    public static string LinkState(LinkDisplay d) => d switch
    {
        LinkDisplay.Down => "끊김 관측",
        LinkDisplay.Denied => "접근 거부",
        LinkDisplay.ObservedNone => "관측했으나 이벤트 없음",
        _ => "수집 미지원",
    };

    public const string NoLinkEvidence = "링크 근거를 모으지 못했습니다 (근거 기록이 없음). 이 PC 쪽 링크가 끊겼는지는 이 리포트로 말할 수 없습니다.";

    public static string LinkSource(LinkSource s) => s == Evidence.LinkSource.Direct ? "직접 관측" : "시스템 기록";

    /// <summary>보조 사유 (Partial·Coverage·Secondary). 사유 코드는 고정 문구로만 (원문을 넣지 않음).</summary>
    public static string LinkNotes(LinkSummary s)
    {
        var parts = new List<string>();
        if (s.Coverage is double cov) parts.Add("관측 범위 " + LiveSummary.Percent(cov));
        foreach (var x in s.Secondary)
        {
            string text = x switch
            {
                "partial" => "일부만 관측",
                "overflow" => "알림이 넘쳐 일부 빠짐",
                "denied" => "수집 능력: 접근 거부",
                "unsupported" => "수집 능력: 미지원",
                "interrupted" => "끝 요약 없이 기록이 끊김",
                _ => Code(x),
            };
            if (!parts.Contains(text)) parts.Add(text);
        }
        return parts.Count == 0 ? "없음" : string.Join(" · ", parts);
    }

    /// <summary>
    /// 사유 코드 → 고정 문구. 진단 문구 표를 쓰되 괄호 안의 원문 조각(오류 번호·코드)은 버린다 (원문에 경로·이름이 섞일 수 있음, 설계 3.1 [1-1]).
    /// </summary>
    public static string Code(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "사유 없음";
        var text = Diagnostics.DiagnosisTexts.Reason(code);
        int paren = text.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) text = text[..paren];
        return text.Contains(code, StringComparison.Ordinal) ? "기타 사유" : text;
    }

    public static string Expected(Targets.ExpectedConnection? e) => e switch
    {
        Targets.ExpectedConnection.Wired => "유선",
        Targets.ExpectedConnection.Wireless => "Wi‑Fi",
        Targets.ExpectedConnection.Any => "상관없음",
        _ => "기록 없음",
    };

    public static string RouteKind(string? kind) => kind switch { "wired" => "유선", "wireless" => "Wi‑Fi", null => "알 수 없음", _ => "기타" };

    public static string AdapterKind(string? kind) => kind switch { "wired" => "유선 어댑터", "wireless" => "무선 어댑터", _ => "기타 어댑터" };

    // ---------------- 시각 ----------------

    public static string Offset(int minutes)
    {
        var t = TimeSpan.FromMinutes(minutes);
        return "UTC" + (t < TimeSpan.Zero ? "-" : "+") + t.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    public static string DateTime(DateTimeOffset wall, int offsetMinutes) =>
        wall.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (" + Offset(offsetMinutes) + ")";

    /// <summary>표의 시각: 시작한 날과 같으면 "HH:mm:ss", 다르면 날짜를 붙인다.</summary>
    public static string Clock(DateTimeOffset wall, DateTimeOffset start, int offsetMinutes)
    {
        var o = TimeSpan.FromMinutes(offsetMinutes);
        var local = wall.ToOffset(o);
        return local.Date == start.ToOffset(o).Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("M'/'d HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FileName(DateTimeOffset start, int offsetMinutes) =>
        "NetPulse-리포트-" + start.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + ".html";
}
