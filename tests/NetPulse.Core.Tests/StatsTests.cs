using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Tests;

/// <summary>통계 규칙 (M3 설계 8절). 합성 회차로 경계 사례를 본다.</summary>
public sealed class StatsTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    /// <summary>패턴 한 글자 = 대상 a의 회차 하나: o 성공, x 손실, e 오류, _ 결과 없음, g 공백 회차.</summary>
    internal static List<RoundData> Rounds(string pattern, long first = 0, double rtt = 10)
    {
        var list = new List<RoundData>();
        for (int i = 0; i < pattern.Length; i++)
        {
            long r = first + i;
            var c = pattern[i];
            var results = c switch
            {
                'o' => new[] { new TargetResult("a", ProbeOutcome.Success, rtt + i, null) },
                'x' => [new TargetResult("a", ProbeOutcome.Lost, null, null)],
                'e' => [new TargetResult("a", ProbeOutcome.Error, null, "E")],
                _ => [],
            };
            list.Add(new RoundData(r, r * 1000, r * 1000, W0.AddSeconds(r), 0, c == 'g', results));
        }
        return list;
    }

    private static TargetStats A(IReadOnlyList<RoundData> rounds, params GapSpan[] gaps) =>
        StatsCalculator.Compute(rounds, ["a"], gaps).Targets.Single();

    [Fact]
    public void CountsAndRatesExcludeErrors()
    {
        var s = A(Rounds("ooxe"));
        Assert.Equal((2, 1, 1), (s.Success, s.Lost, s.Error));
        Assert.Equal(1.0 / 3, s.LossRate!.Value, 9);
        Assert.Equal(W0.AddSeconds(1), s.LastSuccess);
        Assert.Equal(W0.AddSeconds(2), s.LastLoss);
        Assert.Equal(W0.AddSeconds(3), s.LastError);
    }

    [Fact]
    public void ErrorOnlyTargetHasNoValues()
    {
        var s = A(Rounds("eee"));
        Assert.Null(s.LossRate);
        Assert.Null(s.AvgRttMs);
        Assert.Null(s.P95RttMs);
        Assert.Null(s.LastSuccess);
        Assert.Empty(s.Runs);
    }

    [Fact]
    public void NoSuccessMeansNoLatencyButFullLoss()
    {
        var s = A(Rounds("xxx"));
        Assert.Equal(1.0, s.LossRate);
        Assert.Null(s.AvgRttMs);
        var run = Assert.Single(s.Runs);
        // 처음부터 끝까지 손실: 앞뒤 성공이 없으니 최대 길이는 열린 끝
        Assert.Equal((0L, 2L, 3), (run.FirstRound, run.LastRound, run.Count));
        Assert.Equal(2000, run.MinDurationMs);
        Assert.Null(run.MaxDurationMs);
        Assert.Equal(3, s.MaxConsecutiveLoss);
    }

    [Fact]
    public void RunBetweenSuccessesHasBothBounds()
    {
        var s = A(Rounds("oxxxo"));
        var run = Assert.Single(s.Runs);
        Assert.Equal(2000, run.MinDurationMs);   // 마지막 손실 − 첫 손실
        Assert.Equal(4000, run.MaxDurationMs);   // 직후 성공 − 직전 성공
    }

    [Theory]
    [InlineData("oxx_xxo", 2)]   // 결과 없음에서 끊김
    [InlineData("oxxexxo", 2)]   // 오류에서 끊김
    [InlineData("oxxgxxo", 2)]   // 공백 회차에서 끊김
    public void RunsBreakOnMissingErrorOrGap(string pattern, int runs)
    {
        var s = A(Rounds(pattern));
        Assert.Equal(runs, s.Runs.Count);
        Assert.All(s.Runs, r => Assert.Equal(2, r.Count));
        Assert.Equal(2, s.MaxConsecutiveLoss);
        // 끊긴 쪽 끝은 열린 끝
        Assert.All(s.Runs, r => Assert.Null(r.MaxDurationMs));
    }

    [Fact]
    public void RunsBreakOnSkippedRounds()
    {
        var rounds = Rounds("oxx");
        rounds.AddRange(Rounds("xxo", first: 5));   // 3, 4번 회차를 건너뜀
        rounds[3] = rounds[3] with { Skipped = 2 };
        var s = A(rounds);
        Assert.Equal(2, s.Runs.Count);
        Assert.Equal(2, s.MaxConsecutiveLoss);
    }

    [Fact]
    public void RunsBreakAtGapEvenWhenRoundNumbersContinue()
    {
        // 절전 중 단조 시계가 멈춰 회차 번호가 이어진 경우: 공백 이벤트의 직전 회차 뒤에서 끊는다
        var rounds = Rounds("oxxxxo");
        var gap = new GapSpan(GapReason.Suspend, 2500, 2600, W0, W0.AddHours(1), 2, 3);
        var s = A(rounds, gap);
        Assert.Equal(2, s.Runs.Count);
        Assert.Equal((1L, 2L), (s.Runs[0].FirstRound, s.Runs[0].LastRound));
        Assert.Equal((3L, 4L), (s.Runs[1].FirstRound, s.Runs[1].LastRound));
        // 공백 쪽 끝은 열린 끝 (공백 사이의 손실을 끊김 길이로 잇지 않는다)
        Assert.All(s.Runs, r => Assert.Null(r.MaxDurationMs));
    }

    [Fact]
    public void LossWithErrorRoundIsIncompleteButCounted()
    {
        var rounds = new List<RoundData>
        {
            new(0, 0, 0, W0, 0, false, [new("a", ProbeOutcome.Lost, null, null), new("b", ProbeOutcome.Error, null, "E")]),
            new(1, 1000, 1000, W0, 0, false, [new("a", ProbeOutcome.Success, 5, null)]),   // b 결과 없음
            new(2, 2000, 2000, W0, 0, false, [new("a", ProbeOutcome.Success, 5, null), new("b", ProbeOutcome.Success, 5, null)]),
        };
        var stats = StatsCalculator.Compute(rounds, ["a", "b"], []);
        Assert.Equal(2, stats.Incomplete);
        Assert.Equal(1, stats.Targets[0].Lost);
    }

    [Fact]
    public void P95UsesNearestRank()
    {
        // 지연 1..20: ⌈0.95·20⌉ = 19번째 = 19
        var rounds = Enumerable.Range(1, 20).Select(i => new RoundData(i, i, i, W0, 0, false, [new TargetResult("a", ProbeOutcome.Success, i, null)])).ToArray();
        var s = A(rounds);
        Assert.Equal(19, s.P95RttMs);
        Assert.Equal(10.5, s.AvgRttMs);
        Assert.Equal((1.0, 20.0), (s.MinRttMs!.Value, s.MaxRttMs!.Value));
        // 하나뿐이면 그 값
        Assert.Equal(7, A([new RoundData(0, 0, 0, W0, 0, false, [new TargetResult("a", ProbeOutcome.Success, 7, null)])]).P95RttMs);
    }

    [Fact]
    public void WholeMeasurementCounts()
    {
        var rounds = Rounds("oogo");
        rounds[3] = rounds[3] with { Skipped = 4 };
        var stats = StatsCalculator.Compute(rounds, ["a"], []);
        Assert.Equal(3, stats.Rounds);
        Assert.Equal(1, stats.GapRounds);
        Assert.Equal(4, stats.Skipped);
        Assert.Equal(0, stats.Incomplete);
    }
}
