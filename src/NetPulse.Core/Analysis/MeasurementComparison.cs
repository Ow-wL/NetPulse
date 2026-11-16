using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Analysis;

/// <summary>두 측정 가운데 어느 쪽 값이 더 작은지 (우열 표시). None = 표시하지 않음 (차이가 작거나 억제).</summary>
public enum Lower { None, A, B }

/// <summary>비교 조건 알림 (M7 설계 3.4 표).</summary>
public enum CompareCondition
{
    IntervalDiffers, TimeoutDiffers, LengthDiffers, LowCoverage, Damaged, ExpectedDiffers, CompositionDiffers, NoCommonTargets,
}

/// <summary>측정 단위 값 한 쌍. Suppressed면 왜 우열을 표시하지 않는지.</summary>
public sealed record MetricPair<T>(T A, T B, Lower Lower, string? Suppressed);

/// <summary>대상 하나의 통계 (비교용).</summary>
public sealed record TargetSide(Target Target, int Valid, int Lost, int Errors, double? LossRate, double? AvgMs, double? MaxMs,
    int MaxConsecutive, long? LongestRunMinMs, bool LongestRunOpen);

/// <summary>대응한 대상 한 쌍. 손실률과 최대 연속 손실(경과 시간)만 우열을 표시한다.</summary>
public sealed record TargetPair(TargetSide A, TargetSide B, Lower LossRate, string? LossSuppressed, Lower Consecutive, string? ConsecutiveSuppressed);

/// <summary>대응하지 않은 대상과 사유.</summary>
public sealed record Unmatched(Target Target, bool InA, string Reason);

/// <summary>
/// 두 측정 비교 결과 (F6, M7 설계 3.4, 구현 중 결정 1·2).
/// 우열 임계는 표시 임계일 뿐 통계적 유의성이나 회선 개선을 뜻하지 않는다.
/// </summary>
/// <param name="PerHour">시간당 끊김 수 (판정 가능한 관측 시간 기준). 관측이 10분보다 짧으면 null</param>
public sealed record ComparisonResult(
    IReadOnlyList<CompareCondition> Conditions,
    MetricPair<Grade> Grade,
    MetricPair<int> Outages,
    MetricPair<double?> PerHour,
    MetricPair<(long MinMs, bool Open)?> Longest,
    IReadOnlyList<TargetPair> Targets,
    IReadOnlyList<Unmatched> NotCompared);

/// <summary>비교 한쪽: 읽은 기록 + 분석 + 수명.</summary>
public sealed record CompareSide(MeasurementRecord Record, AnalysisResult Analysis, RecordLifecycle Lifecycle);

public static class MeasurementComparison
{
    public const double PerHourThreshold = 0.5, LossThreshold = 0.005, CoverageMin = 0.8, LengthRatio = 0.5;
    public const long LongestThresholdMs = 10_000, MinObservedMs = 600_000;
    public const int MinValid = 100;

    /// <summary>고를 수 없는 이유 (null이면 고를 수 있음).</summary>
    public static string? CannotPick(RecordStatus status, RecordLifecycle life) =>
        life == RecordLifecycle.Running ? "측정 중인 기록은 비교할 수 없습니다."
        : status is RecordStatus.Unreadable or RecordStatus.Unsupported ? "읽지 못한 기록은 비교할 수 없습니다."
        : null;

    public static ComparisonResult Compare(CompareSide a, CompareSide b)
    {
        var ha = a.Record.Header ?? throw new ArgumentException("머리가 없는 기록입니다.", nameof(a));
        var hb = b.Record.Header ?? throw new ArgumentException("머리가 없는 기록입니다.", nameof(b));
        if (ReferenceEquals(a.Record, b.Record))
            throw new ArgumentException("같은 기록 둘은 비교하지 않습니다.");

        var conditions = new List<CompareCondition>();
        bool intervalDiffers = ha.IntervalMs != hb.IntervalMs;
        bool timeoutDiffers = ha.TimeoutMs != hb.TimeoutMs;
        if (intervalDiffers) conditions.Add(CompareCondition.IntervalDiffers);
        if (timeoutDiffers) conditions.Add(CompareCondition.TimeoutDiffers);

        long obsA = Observed(a), obsB = Observed(b);
        if (Math.Min(obsA, obsB) < Math.Max(obsA, obsB) * LengthRatio) conditions.Add(CompareCondition.LengthDiffers);
        bool lowCoverage = Coverage(a) < CoverageMin || Coverage(b) < CoverageMin;
        if (lowCoverage) conditions.Add(CompareCondition.LowCoverage);
        bool roundsDamaged = RoundsDamaged(a) || RoundsDamaged(b);
        if (roundsDamaged || OtherDamage(a) || OtherDamage(b)) conditions.Add(CompareCondition.Damaged);
        if (ha.Expected != hb.Expected) conditions.Add(CompareCondition.ExpectedDiffers);
        bool composition = Composition(ha.Targets) != Composition(hb.Targets);
        if (composition) conditions.Add(CompareCondition.CompositionDiffers);

        // 손실에 기대는 지표의 공통 억제 (구현 중 결정 1)
        string? lossBased = timeoutDiffers ? "응답 제한시간이 다릅니다"
            : roundsDamaged ? "읽지 못한 회차가 있습니다"
            : null;
        string? measureLevel = lossBased ?? (composition ? "대상 구성이 다릅니다" : null);

        // 등급
        var ga = a.Analysis.Grade.Grade;
        var gb = b.Analysis.Grade.Grade;
        string? gradeOff = measureLevel ?? (IsUndecided(ga) || IsUndecided(gb) ? "관측 부족이거나 판단할 수 없는 쪽이 있습니다" : null);
        // 등급은 높을수록 좋다: "더 작은 쪽" 대신 나쁜 쪽(낮은 등급)을 Lower로 둔다
        var grade = new MetricPair<Grade>(ga, gb, gradeOff is null && ga != gb ? (ga < gb ? Lower.A : Lower.B) : Lower.None, gradeOff);

        // 끊김 수 (원시 수는 값만) · 시간당
        int ea = a.Analysis.Episodes.Count, eb = b.Analysis.Episodes.Count;
        var outages = new MetricPair<int>(ea, eb, Lower.None, "횟수는 측정 길이에 따라 달라 값만 보입니다");
        double? pa = PerHour(a, ea), pb = PerHour(b, eb);
        string? perHourOff = measureLevel ?? (lowCoverage ? "관측하지 못한 시간이 많습니다" : null)
            ?? (pa is null || pb is null ? "관측 시간이 짧아 빈도를 계산하지 않습니다" : null);
        var perHour = new MetricPair<double?>(pa, pb,
            perHourOff is null && Math.Abs(pa!.Value - pb!.Value) >= PerHourThreshold ? (pa < pb ? Lower.A : Lower.B) : Lower.None, perHourOff);

        // 최장 끊김 (최소 길이 기준)
        var la = Longest(a.Analysis);
        var lb = Longest(b.Analysis);
        string? longestOff = measureLevel ?? (intervalDiffers ? "측정 주기가 다릅니다" : null)
            ?? (la is { Open: true } || lb is { Open: true } ? "끝을 모르는 끊김이 있습니다" : null);
        long ma = la?.MinMs ?? 0, mb = lb?.MinMs ?? 0;
        var longest = new MetricPair<(long, bool)?>(la, lb,
            longestOff is null && Math.Abs(ma - mb) >= LongestThresholdMs ? (ma < mb ? Lower.A : Lower.B) : Lower.None, longestOff);

        // 대상 대응: 역할 + 주소 + 방식(포트)
        var statsA = Stats(a);
        var statsB = Stats(b);
        var pairs = new List<TargetPair>();
        var notCompared = new List<Unmatched>();
        var usedB = new HashSet<string>();
        foreach (var t in ha.Targets.Where(t => t.Enabled))
        {
            var match = hb.Targets.FirstOrDefault(x => x.Enabled && !usedB.Contains(x.Id) && Same(t, x));
            if (match is null)
            {
                var near = hb.Targets.FirstOrDefault(x => x.Enabled && x.Address.Equals(t.Address));
                notCompared.Add(new Unmatched(t, true, near is null ? "다른 쪽 측정에 같은 대상이 없습니다"
                    : near.Role != t.Role ? "같은 주소지만 역할이 다릅니다" : $"같은 주소지만 측정 방식이 다릅니다 ({Method(t.Method)} / {Method(near.Method)})"));
                continue;
            }
            usedB.Add(match.Id);
            pairs.Add(Pair(Side(t, statsA), Side(match, statsB), lossBased));
        }
        foreach (var t in hb.Targets.Where(t => t.Enabled && !usedB.Contains(t.Id)))
            if (!notCompared.Any(u => u.Target.Address.Equals(t.Address)))
                notCompared.Add(new Unmatched(t, false, "다른 쪽 측정에 같은 대상이 없습니다"));
        if (pairs.Count == 0) conditions.Add(CompareCondition.NoCommonTargets);

        return new ComparisonResult(conditions, grade, outages, perHour, longest, pairs, notCompared);
    }

    private static TargetPair Pair(TargetSide a, TargetSide b, string? lossBased)
    {
        string? lossOff = lossBased ?? (a.Valid < MinValid || b.Valid < MinValid ? "유효한 응답이 100번보다 적습니다" : null)
            ?? (a.LossRate is null || b.LossRate is null ? "값 없음" : null);
        var loss = lossOff is null && Math.Abs(a.LossRate!.Value - b.LossRate!.Value) >= LossThreshold
            ? (a.LossRate < b.LossRate ? Lower.A : Lower.B) : Lower.None;
        // 연속 손실은 경과 시간 범위(최소)로만 비교한다. 주기가 달라도 시간 범위는 비교할 수 있다 (횟수는 값만).
        // 유효한 응답이 적은 쪽은 손실이 없어서가 아니라 관측이 없어서일 수 있다: 손실률과 같은 기준으로 억제한다 (G4 M7 1라운드 a3)
        string? runOff = lossBased ?? (a.Valid < MinValid || b.Valid < MinValid ? "유효한 응답이 100번보다 적습니다" : null)
            ?? (a.LongestRunOpen || b.LongestRunOpen ? "끝을 모르는 연속 손실이 있습니다" : null);
        long ra = a.LongestRunMinMs ?? 0, rb = b.LongestRunMinMs ?? 0;
        var run = runOff is null && Math.Abs(ra - rb) >= LongestThresholdMs ? (ra < rb ? Lower.A : Lower.B) : Lower.None;
        return new TargetPair(a, b, loss, lossOff, run, runOff);
    }

    public static bool Same(Target a, Target b) => a.Role == b.Role && a.Address.Equals(b.Address) && a.Method == b.Method;

    private static string Method(ProbeMethod m) => m.Kind == ProbeMethodKind.Icmp ? "Ping" : $"TCP {m.Port}";

    private static bool IsUndecided(Grade g) => g is Grade.Insufficient or Grade.Undecidable;

    /// <summary>대상 구성: 역할별 수와 외부 사업자 집합.</summary>
    private static string Composition(IReadOnlyList<Target> targets)
    {
        var on = targets.Where(t => t.Enabled).ToArray();
        var providers = on.Where(t => t.Role == TargetRole.External).Select(t => (t.Provider ?? "").Trim().ToLowerInvariant()).Order(StringComparer.Ordinal);
        return $"{on.Count(t => t.Role == TargetRole.Local)}|{on.Count(t => t.Role == TargetRole.External)}|{string.Join(",", providers)}";
    }

    private static long Observed(CompareSide s) => s.Analysis.Coverage.JudgeableRounds * (long)(s.Record.Header?.IntervalMs ?? 1000);

    private static double Coverage(CompareSide s) =>
        s.Analysis.Coverage.ExpectedRounds <= 0 ? 0 : (double)s.Analysis.Coverage.JudgeableRounds / s.Analysis.Coverage.ExpectedRounds;

    private static bool RoundsDamaged(CompareSide s) =>
        s.Record.Damage.Total > 0 || s.Record.MissingSegments.Count > 0 || s.Lifecycle == RecordLifecycle.NoSummary;

    private static bool OtherDamage(CompareSide s) => s.Record.Damage.DamagedEvents > 0 || s.Record.DamagedNotes > 0;

    /// <summary>시간당 끊김 수. 분모는 판정 가능한 관측 시간 (구현 중 결정 2). 10분 미만이면 null.</summary>
    private static double? PerHour(CompareSide s, int episodes)
    {
        long ms = Observed(s);
        return ms < MinObservedMs ? null : episodes / (ms / 3_600_000.0);
    }

    private static (long MinMs, bool Open)? Longest(AnalysisResult a)
    {
        if (a.Episodes.Count == 0) return null;
        var e = a.Episodes.MaxBy(x => x.MinDurationMs)!;
        return (e.MinDurationMs, e.MaxDurationMs is null);
    }

    private static MeasurementStats Stats(CompareSide s) =>
        s.Record.Stats ?? StatsCalculator.Compute(s.Record.Rounds, s.Record.Header!.Targets.Where(t => t.Enabled).Select(t => t.Id).ToArray(), s.Record.Gaps);

    private static TargetSide Side(Target t, MeasurementStats stats)
    {
        var s = stats.Targets.FirstOrDefault(x => x.TargetId == t.Id);
        if (s is null) return new TargetSide(t, 0, 0, 0, null, null, null, 0, null, false);
        var run = s.Runs.MaxBy(r => r.MinDurationMs);
        return new TargetSide(t, s.Success + s.Lost, s.Lost, s.Error, s.LossRate, s.AvgRttMs, s.MaxRttMs, s.MaxConsecutiveLoss,
            run?.MinDurationMs, run is not null && run.MaxDurationMs is null);
    }
}
