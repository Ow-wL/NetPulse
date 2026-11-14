using System.Text.Json.Nodes;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Analysis;

/// <summary>구간 후보 (M4 설계 B4). 개념도 PC → 공유기 → 인터넷 위의 후보이며 실제 패킷 경로가 아니다.</summary>
public enum CandidateKind
{
    None,
    /// <summary>이 PC의 링크 (어댑터 다운 관측됨)</summary>
    PcLink,
    /// <summary>공유기 이후 (인터넷 쪽)</summary>
    AfterRouter,
    /// <summary>PC~공유기 구간 또는 공유기</summary>
    PcToRouterOrRouter,
}

/// <summary>후보와 사유. 후보가 없으면 Reason에 왜 좁힐 수 없는지.</summary>
public sealed record Candidate(CandidateKind Kind, string? Reason, string? LinkKind = null);

/// <summary>같은 판정이 이어진 하위 구간 (M4 설계 B4, 1-8). 길이는 M3 LossRun과 같은 경과 시간 범위.</summary>
public sealed record SubRun(Verdict Verdict, long FirstRound, long LastRound, int Count, long MinDurationMs, long? MaxDurationMs, Candidate Candidate);

/// <summary>끊김 구간. 하위 구간이 둘 이상이면 혼합(대표 판정 없음).</summary>
public sealed record Episode(long FirstRound, long LastRound, IReadOnlyList<SubRun> Parts, long MinDurationMs, long? MaxDurationMs)
{
    public bool Mixed => Parts.Select(p => p.Verdict).Distinct().Count() > 1;
    public Verdict? Summary => Mixed ? null : Parts[0].Verdict;
}

/// <summary>판정별 하위 구간 통계 (N3). 최소 길이 기준, 열린 끝은 따로.</summary>
public sealed record VerdictStats(Verdict Verdict, int Count, long LongestMinMs, double AverageMinMs, int OpenEnded);

/// <summary>대상별 지연 기준선 (M4 설계 B6). 측정 전체 대비 이상치 탐지용. 성공 20개 미만이면 없음.</summary>
public sealed record LatencyBaseline(string TargetId, double? MedianMs, double? ThresholdMs, IReadOnlyList<(long FromMs, double MedianMs)> WindowMedians);

public enum Grade
{
    /// <summary>관측 부족</summary>
    Insufficient,
    /// <summary>판단 불가</summary>
    Undecidable,
    /// <summary>매우 나쁨</summary>
    VeryBad,
    /// <summary>나쁨</summary>
    Bad,
    /// <summary>보통</summary>
    Fair,
    /// <summary>좋음</summary>
    Good,
}

/// <summary>등급과 한 문장 결론. Reason은 결론 문장을 고른 사유 코드, Notes는 결론에 붙는 알림 코드.</summary>
public sealed record GradeResult(Grade Grade, string Reason, string Sentence, IReadOnlyList<string> Notes);

/// <summary>관측 충족 (M4 설계 B5, 2-5).</summary>
public sealed record Coverage(long ExpectedRounds, long JudgeableRounds, long NonGapRounds, double? ExternalLossRate, long LatencyHighRounds, long TargetFailureRounds, long UndecidableRounds, long LossRounds);

/// <summary>분석 결과. 저장하지 않고 읽을 때마다 계산한다 (M4 설계 B1).</summary>
public sealed record AnalysisResult(
    int AnalyzerVersion,
    bool Provisional,
    IReadOnlyList<RoundJudgment> Rounds,
    IReadOnlyList<Episode> Episodes,
    IReadOnlyList<VerdictStats> Stats,
    IReadOnlyList<LatencyBaseline> Latency,
    Coverage Coverage,
    GradeResult Grade,
    LinkEvidence Links,
    IReadOnlyList<string> NoBaselineTargets,
    bool StartedDisconnected);

/// <summary>기록 하나 → 분석 결과 (M4 설계 B). 순수 함수.</summary>
public static class MeasurementAnalysis
{
    public const int AnalyzerVersion = 1;
    public const int MinJudgeableRounds = 60;
    public const double MinCoverageRatio = 0.8;
    public const int MinLatencySamples = 20;
    public static readonly TimeSpan LatencyWindow = TimeSpan.FromMinutes(5);

    public static AnalysisResult Analyze(MeasurementRecord record, bool provisional = false)
    {
        if (record.Header is null) throw new ArgumentException("머리가 없는 기록은 분석할 수 없습니다.", nameof(record));
        return Analyze(record.Header, record.Rounds, record.Gaps, record.Events, record.Summary, provisional);
    }

    /// <param name="elapsedMs">측정 중(잠정) 분석이면 지금까지의 경과 ms. 끝난 기록은 요약의 끝 시각을 쓴다</param>
    /// <param name="live">
    /// 측정 중이면 직접 관측의 지금 상태 (M5 설계 3.2). 끝 요약을 아직 쓰지 않았으면 같은 형식의 잠정 요약 줄을 이벤트 뒤에 붙여
    /// 진행 중 다운을 마지막 표본까지 복원한다 (현재 시각까지 늘리지 않는다).
    /// </param>
    public static AnalysisResult Analyze(RecordHeader header, IReadOnlyList<RoundData> rounds, IReadOnlyList<GapSpan> gaps,
        IReadOnlyList<JsonObject> events, RecordSummary? summary, bool provisional = false, long? elapsedMs = null,
        NetPulse.Core.Measurement.LinkLive? live = null)
    {
        if (live is not null)
        {
            long at = elapsedMs ?? rounds.Select(r => r.EndMs ?? r.AtMs).DefaultIfEmpty(0).Max();
            var extra = live.ToSummaryEvents(header.Id, at, header.StartedAt + TimeSpan.FromMilliseconds(at));
            if (extra.Count > 0) events = events.Concat(extra).ToArray();
        }
        var ordered = rounds.OrderBy(r => r.Round).ToArray();
        var links = LinkEvidenceBuilder.FromEvents(events);
        var ctx = JudgeContext.From(header, ordered, events, links);
        var latency = Baselines(header, ordered);
        var thresholds = latency.Where(l => l.ThresholdMs is not null).ToDictionary(l => l.TargetId, l => l.ThresholdMs!.Value);
        bool High(RoundData r) => r.Results.Any(x => x.Outcome == ProbeOutcome.Success && x.RttMs is double ms
            && thresholds.TryGetValue(x.TargetId, out var th) && ms >= th);

        var noBaseline = header.Targets.Where(t => !ctx.Baseline.Contains(t.Id)).Select(t => t.Id).ToArray();
        bool startedDisconnected = header.Targets.Count > 0 && ctx.Baseline.Count == 0 && ordered.Any(r => !r.Gap);

        var judgments = ordered.Select(r => RoundJudge.Judge(r, ctx, High)).ToArray();
        var breakAfter = new HashSet<long>(gaps.Where(g => g.LastRoundBefore is not null).Select(g => g.LastRoundBefore!.Value));
        var episodes = Episodes(ordered, judgments, breakAfter, ctx);
        var stats = Enum.GetValues<Verdict>().Where(v => v is not (Verdict.Normal or Verdict.LatencyHigh))
            .Select(v =>
            {
                var parts = episodes.SelectMany(e => e.Parts).Where(p => p.Verdict == v).ToArray();
                return new VerdictStats(v, parts.Length, parts.Length == 0 ? 0 : parts.Max(p => p.MinDurationMs),
                    parts.Length == 0 ? 0 : parts.Average(p => (double)p.MinDurationMs), parts.Count(p => p.MaxDurationMs is null));
            }).ToArray();

        var coverage = CoverageOf(header, ordered, gaps, summary, judgments, ctx, elapsedMs);
        var grade = GradeOf(coverage, stats, episodes, ctx, links, header, summary, startedDisconnected);
        return new AnalysisResult(AnalyzerVersion, provisional, judgments, episodes, stats, latency, coverage, grade, links, noBaseline, startedDisconnected);
    }

    // ---------------- 지연 기준선 (B6) ----------------

    private static LatencyBaseline[] Baselines(RecordHeader header, RoundData[] rounds) => header.Targets.Select(t =>
    {
        var ok = rounds.Where(r => !r.Gap).SelectMany(r => r.Results.Where(x => x.TargetId == t.Id && x.Outcome == ProbeOutcome.Success && x.RttMs is not null)
            .Select(x => (r.AtMs, Ms: x.RttMs!.Value))).ToArray();
        double? median = ok.Length >= MinLatencySamples ? Median(ok.Select(x => x.Ms)) : null;
        double? threshold = median is double m ? m + Math.Max(2 * m, 30) : null;
        long window = (long)LatencyWindow.TotalMilliseconds;
        var windows = ok.GroupBy(x => x.AtMs / window).OrderBy(g => g.Key).Select(g => (g.Key * window, Median(g.Select(x => x.Ms)))).ToArray();
        return new LatencyBaseline(t.Id, median, threshold, windows);
    }).ToArray();

    private static double Median(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    // ---------------- 끊김 구간 (B4) ----------------

    private static bool IsLoss(RoundJudgment j) => j.Verdict is Verdict.RouteHold or Verdict.LinkDown or Verdict.ExternalOutage or Verdict.Insufficient or Verdict.TargetFailure;

    private static List<Episode> Episodes(RoundData[] rounds, RoundJudgment[] judgments, HashSet<long> breakAfter, JudgeContext ctx)
    {
        var episodes = new List<Episode>();
        int i = 0;
        while (i < rounds.Length)
        {
            if (!IsLoss(judgments[i])) { i++; continue; }
            int start = i;
            while (i + 1 < rounds.Length && IsLoss(judgments[i + 1]) && Contiguous(rounds[i], rounds[i + 1], breakAfter)) i++;
            int end = i;
            var parts = new List<SubRun>();
            int p = start;
            while (p <= end)
            {
                int q = p;
                while (q + 1 <= end && judgments[q + 1].Verdict == judgments[p].Verdict) q++;
                var (min, max) = Duration(rounds, p, q, breakAfter);
                var verdict = judgments[p].Verdict!.Value;
                parts.Add(new SubRun(verdict, rounds[p].Round, rounds[q].Round, q - p + 1, min, max, CandidateFor(verdict, rounds, judgments, p, q, ctx)));
                p = q + 1;
            }
            var (emin, emax) = Duration(rounds, start, end, breakAfter);
            episodes.Add(new Episode(rounds[start].Round, rounds[end].Round, parts, emin, emax));
            i++;
        }
        return episodes;
    }

    /// <summary>이어진 두 회차인지 (M3 연속 규칙: 번호가 이어지고, 건너뜀 없고, 공백 아님, 공백 경계가 사이에 없음).</summary>
    private static bool Contiguous(RoundData a, RoundData b, HashSet<long> breakAfter) =>
        b.Round == a.Round + 1 && b.Skipped == 0 && !a.Gap && !b.Gap && !breakAfter.Contains(a.Round);

    /// <summary>최소 = 마지막 − 첫 회차, 최대 = 직후 회차 − 직전 회차 (이어져 있을 때만, 아니면 열린 끝).</summary>
    private static (long Min, long? Max) Duration(RoundData[] rounds, int first, int last, HashSet<long> breakAfter)
    {
        long min = rounds[last].AtMs - rounds[first].AtMs;
        bool hasBefore = first > 0 && Contiguous(rounds[first - 1], rounds[first], breakAfter);
        bool hasAfter = last + 1 < rounds.Length && Contiguous(rounds[last], rounds[last + 1], breakAfter);
        long? max = hasBefore && hasAfter ? rounds[last + 1].AtMs - rounds[first - 1].AtMs : null;
        return (min, max);
    }

    private static Candidate CandidateFor(Verdict verdict, RoundData[] rounds, RoundJudgment[] judgments, int first, int last, JudgeContext ctx)
    {
        switch (verdict)
        {
            case Verdict.LinkDown:
            {
                var lostTarget = judgments[first].FailedTargets.FirstOrDefault();
                var idx = lostTarget is null ? null : ctx.LastOkInterface(lostTarget, rounds[first].Round);
                var alias = idx is int i ? ctx.Links.AdapterFor(i, rounds[first].AtMs) : null;
                var kind = alias is not null && ctx.Links.Adapters.TryGetValue(alias, out var a) ? a.Kind : null;
                return new Candidate(CandidateKind.PcLink, null, kind);
            }
            case Verdict.ExternalOutage:
            {
                var locals = ctx.BaselineLocals.Select(t => t.Id).ToArray();
                if (locals.Length == 0) return new Candidate(CandidateKind.None, ctx.Targets.Any(t => t.Role == TargetRole.Local) ? "no-local-baseline" : "no-local");
                // 하위 구간 전체에서 관련 대상 모두가 성공한 경로로 같은 인터페이스를 썼다는 근거가 있어야 구간을 좁힌다 (G4 1라운드 4)
                var ext = ctx.BaselineExternals.Select(t => t.Id).ToArray();
                int? shared = null;
                for (int k = first; k <= last; k++)
                    foreach (var id in ext.Concat(locals))
                    {
                        var route = ctx.RouteOf(id, rounds[k].Round);
                        if (route is not { Ok: true, InterfaceIndex: int idx }) return new Candidate(CandidateKind.None, route is null ? "no-route" : "route-failed");
                        if (shared is null) shared = idx;
                        else if (shared != idx) return new Candidate(CandidateKind.None, "different-interface");
                    }
                bool allOk = true, allLost = true;
                for (int k = first; k <= last; k++)
                    foreach (var id in locals)
                    {
                        var res = rounds[k].Results.FirstOrDefault(x => x.TargetId == id);
                        if (res?.Outcome != ProbeOutcome.Success) allOk = false;
                        if (res?.Outcome != ProbeOutcome.Lost) allLost = false;
                    }
                if (allOk) return new Candidate(CandidateKind.AfterRouter, null);
                if (allLost) return new Candidate(CandidateKind.PcToRouterOrRouter, null);
                return new Candidate(CandidateKind.None, "local-mixed");
            }
            case Verdict.TargetFailure: return new Candidate(CandidateKind.None, "single-target");
            case Verdict.RouteHold: return new Candidate(CandidateKind.None, "route-hold");
            default: return new Candidate(CandidateKind.None, "insufficient");
        }
    }

    // ---------------- 관측 충족과 등급 (B5) ----------------

    private static Coverage CoverageOf(RecordHeader header, RoundData[] rounds, IReadOnlyList<GapSpan> gaps, RecordSummary? summary,
        RoundJudgment[] judgments, JudgeContext ctx, long? elapsedMs)
    {
        long lastIndex = rounds.Length == 0 ? -1 : rounds[^1].Round;
        if (summary?.LastCommittedRound is long lc && lc > lastIndex) lastIndex = lc;
        // 측정 끝 경과 시각까지 지나간 회차 (끝부분의 미처리·미저장·건너뜀도 분모에 남긴다, G4 1라운드 1)
        long? endMs = elapsedMs ?? summary?.EndMs;
        // 끝 시각보다 이르게 예정된 회차까지 (끝 시각에 막 예정된 회차는 아직 낼 수 없었다), 계획 회차 상한 안에서 (2라운드 5)
        long interval = Math.Max(1, header.IntervalMs);
        if (endMs is long em && em > 0)
            lastIndex = Math.Max(lastIndex, Math.Min((em - 1) / interval, header.PlannedMs / interval - 1));
        // 관측해야 했던 회차: 0 ~ 마지막 지난 회차, 절전 공백에 든 회차만 뺀다 (2-5)
        long suspendRounds = gaps.Where(g => g.Reason == GapReason.Suspend).Sum(g => Math.Max(0, (g.ToMs - g.FromMs) / Math.Max(1, header.IntervalMs)));
        long nonGap = rounds.Count(r => !r.Gap);
        long expected = Math.Max(nonGap, lastIndex + 1 - suspendRounds);

        var ext = ctx.BaselineExternals.Select(t => t.Id).ToArray();
        var judgeableRounds = ext.Length == 0 ? [] : rounds.Where(r => !r.Gap && ext.All(id => r.Results.Any(x => x.TargetId == id && x.Outcome != ProbeOutcome.Error))).ToArray();
        long judgeable = judgeableRounds.Length;
        // 등급용 외부 손실률도 판정 가능한 회차에서만 (G4 1라운드 6)
        double? extLoss = judgeable == 0 ? null : ext.Average(id => judgeableRounds.Count(r => r.Results.Any(x => x.TargetId == id && x.Outcome == ProbeOutcome.Lost)) / (double)judgeable);
        // 지연 상승·대상 응답 실패 비율도 판정 가능한 회차 집합에서 센다 (2라운드 6)
        var judgeableSet = new HashSet<long>(judgeableRounds.Select(r => r.Round));
        long latencyHigh = judgments.Count(j => j.Verdict == Verdict.LatencyHigh && judgeableSet.Contains(j.Round));
        long targetFailure = judgments.Count(j => j.Verdict == Verdict.TargetFailure && judgeableSet.Contains(j.Round));
        long undecidable = judgments.Count(j => j.Verdict is Verdict.RouteHold or Verdict.Insufficient);
        long loss = judgments.Count(IsLoss);
        return new Coverage(expected, judgeable, nonGap, extLoss, latencyHigh, targetFailure, undecidable, loss);
    }

    private static GradeResult GradeOf(Coverage c, VerdictStats[] stats, List<Episode> episodes, JudgeContext ctx, LinkEvidence links,
        RecordHeader header, RecordSummary? summary, bool startedDisconnected)
    {
        var notes = new List<string>();
        if (!links.HasEvidence) notes.Add("no-link-evidence");
        if (header.Expected is null) notes.Add("expected-unrecorded");
        if (summary is not null && summary.EventsDropped is null) notes.Add("events-dropped-unrecorded");
        if (summary?.EventsDropped is > 0) notes.Add("events-dropped");
        if (ctx.Targets.Any(t => !ctx.Baseline.Contains(t.Id)) && !startedDisconnected) notes.Add("no-baseline-target");

        GradeResult Result(Grade g, string reason) => new(g, reason, Sentences.For(g, reason, stats, c), notes);

        if (startedDisconnected) return Result(Grade.Undecidable, "started-disconnected");
        if (!ctx.Diverse) return Result(Grade.Insufficient, "not-diverse");
        if (c.JudgeableRounds < MinJudgeableRounds) return Result(Grade.Insufficient, "too-few-rounds");
        if (c.JudgeableRounds < MinCoverageRatio * c.ExpectedRounds) return Result(Grade.Insufficient, "low-coverage");
        if (c.UndecidableRounds >= 3 && c.UndecidableRounds * 2 >= c.LossRounds) return Result(Grade.Undecidable, "undecidable");

        var link = stats.First(s => s.Verdict == Verdict.LinkDown);
        var outage = stats.First(s => s.Verdict == Verdict.ExternalOutage);
        double ext = c.ExternalLossRate ?? 0;
        double latencyShare = c.JudgeableRounds == 0 ? 0 : (double)c.LatencyHighRounds / c.JudgeableRounds;
        double failureShare = c.JudgeableRounds == 0 ? 0 : (double)c.TargetFailureRounds / c.JudgeableRounds;

        if (link.Count >= 1) return Result(Grade.VeryBad, "link-down");
        if (outage.Count >= 3) return Result(Grade.VeryBad, "outage-many");
        if (outage.LongestMinMs >= 30_000) return Result(Grade.VeryBad, "outage-long");
        if (ext >= 0.05) return Result(Grade.VeryBad, "external-loss-high");
        if (outage.Count >= 1) return Result(Grade.Bad, "outage");
        if (ext >= 0.01) return Result(Grade.Bad, "external-loss");
        if (latencyShare >= 0.10) return Result(Grade.Bad, "latency-high");
        if (failureShare >= 0.01) return Result(Grade.Fair, "target-failure");
        if (latencyShare >= 0.03) return Result(Grade.Fair, "latency");
        if (ext > 0) return Result(Grade.Fair, "external-loss-some");
        return Result(Grade.Good, "good");
    }
}

/// <summary>한 문장 결론 (N2). 등급 + 가장 큰 사유 하나. 문장은 여기 상수로만 만든다 (시험 대상).</summary>
public static class Sentences
{
    public static string GradeName(Grade g) => g switch
    {
        Grade.Insufficient => "관측 부족", Grade.Undecidable => "판단 불가", Grade.VeryBad => "매우 나쁨",
        Grade.Bad => "나쁨", Grade.Fair => "보통", _ => "좋음",
    };

    public static string For(Grade grade, string reason, IReadOnlyList<VerdictStats> stats, Coverage c)
    {
        var outage = stats.First(s => s.Verdict == Verdict.ExternalOutage);
        var link = stats.First(s => s.Verdict == Verdict.LinkDown);
        string Seconds(long ms) => (ms / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        string Pct(double? v) => ((v ?? 0) * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return reason switch
        {
            "started-disconnected" => "어느 대상도 응답한 적이 없습니다. 끊긴 상태에서 측정을 시작했을 수 있습니다.",
            "not-diverse" => "서로 다른 사업자의 외부 대상이 둘 이상 응답해야 인터넷 연결을 판단할 수 있습니다.",
            "too-few-rounds" => "판단할 수 있는 관측이 1분 분량보다 적습니다.",
            "low-coverage" => "측정 시간 중 판단할 수 있는 관측이 80%보다 적습니다.",
            "undecidable" => "경로가 바뀌었거나 근거가 엇갈려 끊김의 원인을 판단할 수 없는 경우가 많습니다.",
            "link-down" => $"이 PC의 네트워크 연결(링크)이 끊긴 것이 {link.Count}번 관측됐습니다.",
            "outage-many" => $"인터넷 연결이 {outage.Count}번 끊긴 것으로 보입니다 (최장 {Seconds(outage.LongestMinMs)}초 이상).",
            "outage-long" => $"인터넷 연결이 {Seconds(outage.LongestMinMs)}초 이상 끊긴 적이 있습니다.",
            "external-loss-high" => $"외부 대상의 응답 손실이 {Pct(c.ExternalLossRate)}%로 많습니다.",
            "outage" => $"인터넷 연결이 {outage.Count}번 끊긴 것으로 보입니다 (최장 {Seconds(outage.LongestMinMs)}초 이상).",
            "external-loss" => $"외부 대상의 응답 손실이 {Pct(c.ExternalLossRate)}%입니다.",
            "latency-high" => "지연이 평소보다 크게 튄 순간이 많습니다.",
            "target-failure" => "일부 대상만 응답하지 않은 순간이 있습니다. 그 대상의 문제일 수 있습니다.",
            "latency" => "지연이 평소보다 튄 순간이 조금 있습니다.",
            "external-loss-some" => "외부 대상의 응답 손실이 조금 있었습니다.",
            _ => "측정하는 동안 끊김이 관측되지 않았습니다.",
        };
    }
}
