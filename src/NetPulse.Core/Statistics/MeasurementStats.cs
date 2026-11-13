using NetPulse.Core.Measurement;
using NetPulse.Core.Records;

namespace NetPulse.Core.Statistics;

/// <summary>
/// 연속 손실 구간 (M3 설계 8절). 길이는 경과 시간 범위로만 말한다:
/// 최소 = 마지막 손실 − 첫 손실, 최대 = 직후 성공 − 직전 성공. 직전·직후 성공이 없거나 그 사이에 끊김이 있으면 그 끝은 열린 끝(null).
/// 첫·마지막 손실 표본 차이를 끊김 길이처럼 보여 주지 않는다 (기존 앱에서 이어받지 않는 표현).
/// </summary>
public sealed record LossRun(string TargetId, long FirstRound, long LastRound, int Count, long MinDurationMs, long? MaxDurationMs,
    DateTimeOffset FirstLossWall, DateTimeOffset LastLossWall);

/// <summary>대상 하나의 통계. 값이 없으면 null (0으로 쓰지 않는다).</summary>
public sealed record TargetStats(
    string TargetId,
    int Success, int Lost, int Error,
    double? LossRate,
    double? AvgRttMs, double? MinRttMs, double? MaxRttMs, double? P95RttMs,
    int MaxConsecutiveLoss,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastLoss, DateTimeOffset? LastError,
    IReadOnlyList<LossRun> Runs);

/// <summary>측정 전체 통계.</summary>
public sealed record MeasurementStats(
    int Rounds, long Skipped, int GapRounds, int Incomplete,
    IReadOnlyList<TargetStats> Targets);

/// <summary>
/// 통계 계산 (M3 설계 8절, 기획서 6.3 이어받을 판단 규칙). 순수 함수.
/// - 검사 오류는 손실률 분모에서 뺀다. 지연은 성공만. 값이 없으면 null.
/// - 연속 손실은 그 대상의 결과가 회차 번호로 이어진 손실일 때만 같은 구간: 건너뜀·공백·오류·결과 없음에서 끊는다.
/// - 활성 대상 결과가 빠졌거나 검사 오류가 있는 회차는 불완전 회차.
/// </summary>
public static class StatsCalculator
{
    public static MeasurementStats Compute(IReadOnlyList<RoundData> rounds, IReadOnlyList<string> activeTargets, IReadOnlyList<GapSpan> gaps)
    {
        // 측정 중에는 회차가 늘 때마다 다시 구하므로 큰 할당을 줄인다 (M5 bench): 이미 회차 순이면 정렬하지 않고 복사만
        var ordered = new RoundData[rounds.Count];
        bool sorted = true;
        for (int i = 0; i < ordered.Length; i++)
        {
            ordered[i] = rounds[i];
            if (i > 0 && ordered[i].Round < ordered[i - 1].Round) sorted = false;
        }
        if (!sorted) ordered = ordered.OrderBy(r => r.Round).ToArray();
        // 공백이 "직전 회차 a 뒤"에 있으면 a와 그 다음 회차 사이에서 구간을 끊는다
        var breakAfter = new HashSet<long>(gaps.Where(g => g.LastRoundBefore is not null).Select(g => g.LastRoundBefore!.Value));
        int gapRounds = ordered.Count(r => r.Gap);
        int incomplete = ordered.Count(r => !r.Gap && (activeTargets.Any(t => r.Results.All(x => x.TargetId != t)) || r.Results.Any(x => x.Outcome == ProbeOutcome.Error)));
        long skipped = ordered.Sum(r => r.Skipped);
        var per = activeTargets.Select(t => ForTarget(t, ordered, breakAfter)).ToArray();
        return new MeasurementStats(ordered.Count(r => !r.Gap), skipped, gapRounds, incomplete, per);
    }

    private static TargetStats ForTarget(string id, RoundData[] rounds, HashSet<long> breakAfter)
    {
        int ok = 0, lost = 0, err = 0, maxRun = 0;
        var rtts = new List<double>(rounds.Length);
        DateTimeOffset? lastOk = null, lastLoss = null, lastErr = null;
        var runs = new List<LossRun>();

        // 구간 추적
        RoundData? prev = null;           // 이 대상 기준 직전에 본 회차 (연속성 판단)
        RoundData? lastSuccessRound = null; // 끊김 없이 이어진 직전 성공
        bool successChainIntact = false;  // 직전 성공과 현재 위치 사이에 끊김이 없었는지
        var current = new List<RoundData>();
        long? runOpenStartMs = null;      // 구간 앞쪽 경계 (직전 성공 경과 ms, 없으면 열린 끝)

        void CloseRun(RoundData? successAfter)
        {
            if (current.Count == 0) return;
            var first = current[0];
            var last = current[^1];
            long min = last.AtMs - first.AtMs;
            long? max = runOpenStartMs is long s && successAfter is not null ? successAfter.AtMs - s : null;
            runs.Add(new LossRun(id, first.Round, last.Round, current.Count, min, max, first.Wall, last.Wall));
            maxRun = Math.Max(maxRun, current.Count);
            current.Clear();
            runOpenStartMs = null;
        }

        foreach (var r in rounds)
        {
            bool contiguous = prev is not null && r.Round == prev.Round + 1 && r.Skipped == 0 && !breakAfter.Contains(prev.Round) && !prev.Gap;
            if (!contiguous)
            {
                // 끊김: 열린 구간은 뒤쪽 경계를 모른 채 닫는다
                CloseRun(null);
                successChainIntact = false;
            }
            prev = r;
            if (r.Gap)
            {
                CloseRun(null);
                successChainIntact = false;
                continue;
            }
            var res = r.Results.FirstOrDefault(x => x.TargetId == id);
            if (res is null)
            {
                CloseRun(null);
                successChainIntact = false;
                continue;
            }
            switch (res.Outcome)
            {
                case ProbeOutcome.Success:
                    ok++;
                    if (res.RttMs is double ms) rtts.Add(ms);
                    lastOk = r.Wall;
                    CloseRun(r);
                    lastSuccessRound = r;
                    successChainIntact = true;
                    break;
                case ProbeOutcome.Lost:
                    lost++;
                    lastLoss = r.Wall;
                    if (current.Count == 0) runOpenStartMs = successChainIntact && lastSuccessRound is not null ? lastSuccessRound.AtMs : null;
                    current.Add(r);
                    break;
                default:
                    err++;
                    lastErr = r.Wall;
                    CloseRun(null);
                    successChainIntact = false;
                    break;
            }
        }
        CloseRun(null);

        double? rate = ok + lost == 0 ? null : (double)lost / (ok + lost);
        double? avg = rtts.Count == 0 ? null : rtts.Average();
        double? p95 = null;
        if (rtts.Count > 0)
        {
            var s = rtts.ToArray();
            Array.Sort(s);
            int rank = (int)Math.Ceiling(0.95 * s.Length);   // 최근접 순위법
            p95 = s[Math.Clamp(rank, 1, s.Length) - 1];
        }
        return new TargetStats(id, ok, lost, err, rate, avg, rtts.Count == 0 ? null : rtts.Min(), rtts.Count == 0 ? null : rtts.Max(), p95,
            maxRun, lastOk, lastLoss, lastErr, runs);
    }
}
