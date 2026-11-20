using System.Globalization;
using System.Text;
using NetPulse.Core.Analysis;
using NetPulse.Core.Evidence;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Presentation;

/// <summary>판정별 하위 구간 통계 한 줄 (N3).</summary>
public sealed record VerdictLine(Verdict Verdict, string Name, int Count, string Text);

/// <summary>대상 통계 한 줄 (N9). 값이 없으면 "값 없음".</summary>
public sealed record TargetStatLine(string TargetId, string Name, TargetRole Role, string Method, string Loss, string Average, string Max,
    string Consecutive, string LastSuccess, string LastLoss);

/// <summary>끊김 목록 한 줄. 하위 구간이 둘 이상이면 <see cref="Mixed"/>이고 후보는 줄에 쓰지 않는다 (펼침에서 하위 구간마다).</summary>
public sealed record EpisodeLine(int Index, long FirstRound, long LastRound, DateTimeOffset Start, string Duration, bool Mixed, string Verdicts, string? Candidate);

/// <summary>하위 구간 한 줄 (펼친 상세).</summary>
public sealed record PartLine(string Verdict, long FirstRound, long LastRound, string Duration, string Candidate);

/// <summary>지연 추세: 대상 하나의 5분 창 중앙값. 끊긴 곳마다 선을 나눈다. 기준선이 없으면 선 없음.</summary>
public sealed record TrendLine(string TargetId, string Name, IReadOnlyList<IReadOnlyList<(long FromMs, double MedianMs)>> Segments, bool NoBaseline);

/// <summary>기록 상세 (M7 설계 3.2).</summary>
public sealed record RecordDetailView(
    Grade Grade, GradeBadge Badge, string GradeName, string Sentence, IReadOnlyList<string> Notes, string Coverage,
    string End, string? Save, string Targets, string Setup, IReadOnlyList<string> Damage,
    IReadOnlyList<VerdictLine> Verdicts, string? CountNote, IReadOnlyList<TargetStatLine> TargetLines,
    IReadOnlyList<TrendLine> Trend, IReadOnlyList<EpisodeLine> Episodes, string? NoEpisodes);

/// <summary>기록 화면의 문구와 모델 (M7 설계 3.2·3.3). 순수 함수.</summary>
public static class RecordView
{
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");
    public const long TrendWindowMs = 300_000;
    public const string TrendNote = "지연 상승 판정은 측정 전체 대비 순간 상승만 잡습니다. 측정 동안 서서히 나빠진 것은 이 추세로 보세요.";

    // ---------------- 이름 ----------------

    public static string VerdictName(Verdict v) => v switch
    {
        Verdict.Normal => "정상",
        Verdict.LatencyHigh => "지연 상승",
        Verdict.RouteHold => "판단 불가 (경로가 예상과 다름)",
        Verdict.LinkDown => "로컬 링크 단절 관측",
        Verdict.ExternalOutage => "외부 연결 단절 추정",
        Verdict.Insufficient => "판단 불가 (근거 부족)",
        Verdict.TargetFailure => "대상 응답 실패",
        _ => v.ToString(),
    };

    public static string CandidateText(Candidate c) => c.Kind switch
    {
        CandidateKind.PcLink => $"후보: 이 PC의 {PathMapModel.KindText(c.LinkKind)} 링크 (어댑터 다운 관측. 원인이 링크라는 단정은 아님)",
        CandidateKind.AfterRouter => "후보: 공유기 이후 (인터넷 쪽)",
        CandidateKind.PcToRouterOrRouter => "후보: PC~공유기 구간 또는 공유기",
        _ => "후보 없음 — " + PathMapModel.ReasonText(c.Reason),
    };

    /// <summary>끝 사유 (수명과 함께). 저장 상태와 따로 말한다.</summary>
    public static string EndText(RecordLifecycle life, EndReason? end) => life switch
    {
        RecordLifecycle.Running => "측정 중",
        RecordLifecycle.NoSummary => "끝 기록 없음 (비정상 종료일 수도, 요약을 쓰지 못한 것일 수도 있습니다)",
        _ => end switch
        {
            EndReason.Completed => "계획한 시간을 마침",
            EndReason.Stopped => "사용자가 멈춤",
            EndReason.Error => "오류로 끝남",
            EndReason.Interrupted => "중간에 중단됨",   // 네트워크 "끊김"과 섞이지 않게 (M11 문구 점검)
            _ => "끝 사유 알 수 없음",
        },
    };

    /// <summary>저장 상태. 문제가 없으면 null.</summary>
    public static string? SaveText(RecordSummary? s)
    {
        if (s is null) return null;
        var parts = new List<string>();
        long unsaved = s.Pending + s.Dropped + s.Unprocessed;
        if (unsaved > 0) parts.Add($"저장하지 못한 회차 {unsaved}개");
        if (s.EventsDropped is > 0) parts.Add($"저장하지 못한 근거 {s.EventsDropped}개");
        if (s.SealFailed) parts.Add("마무리 기록을 쓰지 못함");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"대상 3개 · 로컬 1 · 외부 2" (과거 구성 이름은 기록에 없어 쓰지 않는다).</summary>
    public static string TargetsSummary(IReadOnlyList<Target> targets)
    {
        var on = targets.Where(t => t.Enabled).ToArray();
        return $"대상 {on.Length}개 · 로컬 {on.Count(t => t.Role == TargetRole.Local)} · 외부 {on.Count(t => t.Role == TargetRole.External)}";
    }

    public static string ExpectedText(ExpectedConnection? e) => e switch
    {
        ExpectedConnection.Wired => "예상 연결 유선",
        ExpectedConnection.Wireless => "예상 연결 Wi‑Fi",
        ExpectedConnection.Any => "예상 연결 상관없음",
        _ => "예상 연결 기록 없음",
    };

    public static string MethodText(ProbeMethod m) => m.Kind == ProbeMethodKind.Icmp ? "Ping" : $"TCP {m.Port}";

    /// <summary>손상 종류별 알림 (M7 설계 3.2 [1-12]). 회차·근거·메모·조각·파일을 따로 말한다.</summary>
    public static IReadOnlyList<string> DamageNotices(MeasurementRecord r, int ignoredMarkLines)
    {
        var list = new List<string>();
        int rounds = r.Damage.Total - r.Damage.UnreadableFiles;
        if (rounds > 0) list.Add($"이 측정의 회차 {rounds}줄을 읽지 못해 그 회차는 관측하지 못한 것으로 셉니다.");
        if (r.Damage.DamagedEvents > 0) list.Add($"경로·링크 근거 {r.Damage.DamagedEvents}줄을 읽지 못해 끊김 상세의 근거가 빠질 수 있습니다.");
        int memos = r.DamagedNotes + ignoredMarkLines;
        if (memos > 0) list.Add($"증상 표시·메모 {memos}줄을 읽지 못했습니다.");
        if (r.MissingSegments.Count > 0) list.Add($"기록 조각 {r.MissingSegments.Count}개를 찾지 못했습니다 ({string.Join(", ", r.MissingSegments)}번).");
        if (r.Damage.UnreadableFiles > 0) list.Add($"열지 못한 파일이 {r.Damage.UnreadableFiles}개 있습니다.");
        if (r.MissingNotice is { } n) list.Add(n);
        return list;
    }

    // ---------------- 길이 ----------------

    /// <summary>끊김 길이: 최소~최대 (최소 길이 기준). 최대를 모르면 "이상". 한 회차면 "1회차".</summary>
    public static string Duration(long minMs, long? maxMs, int rounds)
    {
        if (rounds <= 1) return maxMs is long m1 ? $"1회차 · 최대 약 {TimelineModel.Seconds(m1)}" : "1회차";
        if (maxMs is not long max) return $"{TimelineModel.Seconds(minMs)} 이상";
        // 반올림한 두 값이 같으면 범위로 쓰지 않는다 ("약 1시간 6분~1시간 6분", M8 Edge 확인)
        string lo = TimelineModel.Seconds(minMs), hi = TimelineModel.Seconds(max);
        return lo == hi ? $"약 {lo}" : $"약 {lo}~{hi}";
    }

    public static string Duration(Episode e) => Duration(e.MinDurationMs, e.MaxDurationMs, e.Parts.Sum(p => p.Count));

    public static string Duration(SubRun p) => Duration(p.MinDurationMs, p.MaxDurationMs, p.Count);

    // ---------------- 상세 ----------------

    public static RecordDetailView Detail(MeasurementRecord record, AnalysisResult a, RecordLifecycle life)
    {
        var header = record.Header ?? throw new ArgumentException("머리가 없는 기록입니다.", nameof(record));
        var targets = header.Targets.Where(t => t.Enabled).ToArray();
        var marks = SymptomMarks.FromEvents(record.Events, record.NoteLines);
        var c = a.Coverage;
        string coverage = c.ExpectedRounds <= 0 ? "판단할 수 있는 관측이 없습니다."
            : $"판단할 수 있는 관측 {c.JudgeableRounds}회차 / 관측해야 했던 {c.ExpectedRounds}회차 ({LiveSummary.Percent((double)c.JudgeableRounds / c.ExpectedRounds)})";

        var verdicts = a.Stats.Where(s => s.Verdict != Verdict.Normal && s.Count > 0)
            .Select(s => new VerdictLine(s.Verdict, VerdictName(s.Verdict), s.Count,
                $"{s.Count}번 · 가장 긴 것 {TimelineModel.Seconds(s.LongestMinMs)} 이상 · 평균 {TimelineModel.Seconds((long)Math.Round(s.AverageMinMs))} 이상"
                + (s.OpenEnded > 0 ? $" · 끝을 모르는 것 {s.OpenEnded}번" : "")))
            .ToArray();
        int parts = verdicts.Sum(v => v.Count);
        string? countNote = parts != a.Episodes.Count && a.Episodes.Count > 0
            ? $"끊김 {a.Episodes.Count}번 가운데 판정이 바뀐 구간이 있어, 판정별로 나눠 세면 {parts}개입니다." : null;

        var stats = record.Stats ?? StatsCalculator.Compute(record.Rounds, targets.Select(t => t.Id).ToArray(), record.Gaps);
        var lines = targets.Select(t => Line(t, stats.Targets.FirstOrDefault(s => s.TargetId == t.Id), header.UtcOffsetMinutes)).ToArray();

        var trend = targets.Select(t => Trend(t, a.Latency.FirstOrDefault(l => l.TargetId == t.Id), record.Gaps)).ToArray();

        var episodes = a.Episodes.Select((e, i) => EpisodeRow(i, e, record)).ToArray();

        return new RecordDetailView(a.Grade.Grade, LiveSummary.Badge(a.Grade.Grade), Sentences.GradeName(a.Grade.Grade), a.Grade.Sentence,
            a.Grade.Notes.Select(NoteText).ToArray(), coverage,
            EndText(life, record.Summary?.EndReason ?? record.EndReason), SaveText(record.Summary), TargetsSummary(header.Targets),
            $"측정 주기 {header.IntervalMs / 1000.0:0.#}초 · 응답 제한 {header.TimeoutMs / 1000.0:0.#}초 · {ExpectedText(header.Expected)}",
            DamageNotices(record, marks.Ignored), verdicts, countNote, lines, trend, episodes,
            episodes.Length == 0 ? "이 측정에서는 끊김이 관측되지 않았습니다." : null);
    }

    /// <summary>등급에 붙는 알림 코드 (M4 B5) → 쉬운 말. 모르는 코드는 숨기지 않고 코드 그대로.</summary>
    public static string NoteText(string code) => code switch
    {
        "no-link-evidence" => "링크 근거를 모으지 못해 이 PC 쪽 링크 끊김은 판단에 넣지 않았습니다.",
        "expected-unrecorded" => "예상 연결 유형이 기록되지 않아 경로 비교를 하지 않았습니다.",
        "events-dropped-unrecorded" => "근거 기록이 빠졌는지 알 수 없는 옛 형식입니다.",
        "events-dropped" => "저장하지 못한 근거가 있어 끊김 상세가 빠질 수 있습니다.",
        "no-baseline-target" => "한 번도 응답하지 않은 대상은 판단에서 뺐습니다 (Ping을 막는 장비일 수 있음).",
        _ => "알림: " + code,
    };

    private static TargetStatLine Line(Target t, TargetStats? s, int offsetMinutes)
    {
        string When(DateTimeOffset? w) => w is { } x ? x.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).ToString("tt h:mm:ss", Ko) : "값 없음";
        if (s is null) return new TargetStatLine(t.Id, t.Name, t.Role, MethodText(t.Method), "값 없음", "값 없음", "값 없음", "값 없음", "값 없음", "값 없음");
        int valid = s.Success + s.Lost;
        string loss = s.LossRate is double r ? $"{valid}번 중 {s.Lost}번 응답 없음 ({LiveSummary.Percent(r)})" : "값 없음";
        if (s.Error > 0) loss += $" · 검사 오류 {s.Error}번 (분모에서 뺌)";
        return new TargetStatLine(t.Id, t.Name, t.Role, MethodText(t.Method), loss,
            s.AvgRttMs is double avg ? PathMapModel.Ms(avg) : "값 없음",
            s.MaxRttMs is double max ? PathMapModel.Ms(max) : "값 없음",
            valid == 0 ? "값 없음" : $"{s.MaxConsecutiveLoss}번",
            When(s.LastSuccess), When(s.LastLoss));
    }

    /// <summary>5분 창 중앙값을 선으로. 창이 빠졌거나(성공 없음) 공백과 겹치면 선을 끊는다.</summary>
    public static TrendLine Trend(Target t, LatencyBaseline? b, IReadOnlyList<GapSpan> gaps)
    {
        if (b is null || b.MedianMs is null || b.WindowMedians.Count == 0) return new TrendLine(t.Id, t.Name, [], true);
        var segments = new List<IReadOnlyList<(long, double)>>();
        var current = new List<(long, double)>();
        (long FromMs, double MedianMs)? prev = null;
        foreach (var w in b.WindowMedians.OrderBy(w => w.FromMs))
        {
            bool broken = prev is { } p && (w.FromMs - p.FromMs > TrendWindowMs || gaps.Any(g => g.FromMs < w.FromMs && g.ToMs > p.FromMs));   // 두 창 사이(앞 창 안 포함)에 공백이 있으면 잇지 않는다
            if (broken && current.Count > 0) { segments.Add(current); current = []; }
            current.Add((w.FromMs, w.MedianMs));
            prev = w;
        }
        if (current.Count > 0) segments.Add(current);
        return new TrendLine(t.Id, t.Name, segments, false);
    }

    private static EpisodeLine EpisodeRow(int index, Episode e, MeasurementRecord r)
    {
        var first = r.Rounds.FirstOrDefault(x => x.Round == e.FirstRound) ?? r.Rounds.First(x => x.Round >= e.FirstRound);
        bool mixed = e.Parts.Count > 1;
        string verdicts = mixed
            ? $"여러 판정 ({e.Parts.Count}): " + string.Join(" → ", e.Parts.Select(p => VerdictName(p.Verdict)))
            : VerdictName(e.Parts[0].Verdict);
        return new EpisodeLine(index, e.FirstRound, e.LastRound, first.Wall, Duration(e), mixed, verdicts, mixed ? null : CandidateText(e.Parts[0].Candidate));
    }

    public static IReadOnlyList<PartLine> Parts(Episode e) =>
        e.Parts.Select(p => new PartLine(VerdictName(p.Verdict), p.FirstRound, p.LastRound, Duration(p), CandidateText(p.Candidate))).ToArray();

    // ---------------- 복사 ----------------

    /// <summary>
    /// 구간 상세 복사 텍스트 (M7 설계 3.3 [1-6]). 대상은 역할과 순번("로컬 대상 1", "외부 대상 2")으로만:
    /// 사용자가 붙인 이름·주소·어댑터 이름·인터페이스 번호·메모는 넣지 않는다.
    /// </summary>
    public static string Copy(RecordHeader header, EpisodeDetailResult d)
    {
        var names = NeutralNames(header.Targets);
        var e = d.Episode;
        var offset = TimeSpan.FromMinutes(header.UtcOffsetMinutes);
        string Clock(DateTimeOffset w) => w.ToOffset(offset).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("NetPulse 끊김 상세").Append('\n');
        sb.Append("측정 시작: ").Append(header.StartedAt.ToOffset(offset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        var start = d.Rows.FirstOrDefault(r => r.InEpisode);
        sb.Append("구간: ").Append(start is null ? "" : Clock(start.Wall)).Append(", ").Append(Duration(e)).Append('\n');
        foreach (var p in e.Parts)
            sb.Append("- ").Append(VerdictName(p.Verdict)).Append(", ").Append(Duration(p)).Append(", ").Append(CandidateKindText(p.Candidate)).Append('\n');
        sb.Append("회차별 응답 (").Append(string.Join(", ", names.Values)).Append(")\n");
        foreach (var r in d.Rows)
        {
            if (r.Round is null) { sb.Append("  공백 (관측 없음)\n"); continue; }
            sb.Append(r.InEpisode ? "* " : "  ").Append(Clock(r.Wall)).Append(' ');
            sb.Append(r.Gap ? "관측 없음" : string.Join(" | ", r.Cells.Select(c => CellText(c))));
            sb.Append('\n');
            if (d.OmitAfter == r.Round && d.OmittedRounds > 0) sb.Append("  … 가운데 ").Append(d.OmittedRounds).Append("회차 생략\n");
        }
        sb.Append("시작 시점 경로: ").Append(string.Join(", ", d.Routes.Select(x => $"{names.GetValueOrDefault(x.TargetId, "대상")} {RouteText(x)}"))).Append('\n');
        sb.Append("링크 끊김 관측: ").Append(d.Links.Count == 0 ? "없음" : $"{d.Links.Count + d.MoreLinks}건").Append('\n');
        if (d.EvidenceDamaged) sb.Append("근거 일부를 읽지 못했습니다.\n");
        return sb.ToString();
    }

    /// <summary>대상 ID → "로컬 대상 1", "외부 대상 2" (역할 안의 순서).</summary>
    public static IReadOnlyDictionary<string, string> NeutralNames(IReadOnlyList<Target> targets)
    {
        var map = new Dictionary<string, string>();
        int local = 0, external = 0;
        foreach (var t in targets.Where(t => t.Enabled))
            map[t.Id] = t.Role == TargetRole.Local ? $"로컬 대상 {++local}" : $"외부 대상 {++external}";
        return map;
    }

    public static string CellText(DetailCell c) => c.Kind switch
    {
        CellKind.Success => c.RttMs is double ms ? PathMapModel.Ms(ms) : "응답",
        CellKind.Lost => "응답 없음",
        CellKind.Error => "검사 오류",
        _ => "없음",
    };

    public static string RouteText(RouteKindAt r) => !r.Ok ? "경로 없음" : r.Kind switch { "wired" => "유선", "wireless" => "Wi‑Fi", _ => "기타" };

    /// <summary>복사용 후보 종류 (사유 문장은 길어 종류만).</summary>
    private static string CandidateKindText(Candidate c) => c.Kind switch
    {
        CandidateKind.PcLink => "후보 이 PC 링크",
        CandidateKind.AfterRouter => "후보 공유기 이후",
        CandidateKind.PcToRouterOrRouter => "후보 PC~공유기 또는 공유기",
        _ => "후보 없음",
    };
}
