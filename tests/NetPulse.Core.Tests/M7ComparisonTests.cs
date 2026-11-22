using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Analysis;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

/// <summary>두 측정 비교 (M7 설계 3.4, 구현 중 결정 1·2).</summary>
public sealed class M7ComparisonTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 4, 20, 0, TimeSpan.Zero);

    private static readonly Target Router = new("router", "R", IPAddress.Parse("192.0.2.254"), TargetRole.Local, null, ProbeMethod.Icmp);
    private static readonly Target ExtA = new("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "A사", ProbeMethod.Icmp);
    private static readonly Target ExtB = new("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "B사", ProbeMethod.Icmp);

    private sealed record Spec(int Minutes, IReadOnlyList<(int From, int Count)> Outages, int IntervalMs = 1000, int TimeoutMs = 800,
        IReadOnlyList<Target>? Targets = null, ExpectedConnection? Expected = ExpectedConnection.Any, DamageCounts? Damage = null,
        RecordLifecycle Life = RecordLifecycle.Ended, string Id = "m", bool LocalDown = false, string? Broken = null);

    /// <summary>외부 둘이 함께 손실인 끊김을 넣은 기록 (공유기는 늘 응답).</summary>
    private static CompareSide Side(Spec s)
    {
        var targets = s.Targets ?? [Router, ExtA, ExtB];
        int n = s.Minutes * 60_000 / s.IntervalMs;
        var rounds = new List<RoundData>();
        for (int r = 0; r < n; r++)
        {
            bool down = s.Outages.Any(o => r >= o.From && r < o.From + o.Count);
            var results = targets.Select(t => t.Id == s.Broken ? new TargetResult(t.Id, ProbeOutcome.Error, null, null)
                : (t.Role == TargetRole.External || s.LocalDown) && down ? new TargetResult(t.Id, ProbeOutcome.Lost, null, null)
                : new TargetResult(t.Id, ProbeOutcome.Success, 10, null)).ToArray();
            long at = (long)r * s.IntervalMs;
            rounds.Add(new RoundData(r, at, at, W0.AddMilliseconds(at), 0, false, results, at + 50));
        }
        var header = new RecordHeader(s.Id, 0, "t", W0, 540, targets, s.IntervalMs, s.TimeoutMs, (long)n * s.IntervalMs, "primary", s.Expected);
        var events = targets.Select(t => new JsonObject
        {
            ["id"] = s.Id, ["type"] = "route", ["round"] = 0, ["at"] = 0, ["wall"] = RecordSchema.Iso(W0), ["target"] = t.Id,
            ["status"] = "ok", ["interfaceIndex"] = 7, ["kind"] = "wired",
        }).ToArray();
        var summary = new RecordSummary(s.Id, EndReason.Completed, W0.AddMilliseconds((long)n * s.IntervalMs), n, n, 0, 0, 0, n - 1, false, null, 0, (long)n * s.IntervalMs);
        var record = new MeasurementRecord(RecordStatus.Ok, null, header, rounds, [], events, summary, EndReason.Completed,
            s.Damage ?? new DamageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0), [], null, null);
        return new CompareSide(record, MeasurementAnalysis.Analyze(record), s.Life);
    }

    private static readonly (int, int)[] None = [];

    [Fact]
    public void SameAddressWithADifferentMethodIsNotPaired()
    {
        var tcp = ExtA with { Method = ProbeMethod.Tcp(443) };
        var r = MeasurementComparison.Compare(Side(new Spec(20, None)), Side(new Spec(20, None, Targets: [Router, tcp, ExtB])));
        Assert.Equal(["router", "b"], r.Targets.Select(p => p.A.Target.Id));
        var u = Assert.Single(r.NotCompared);
        Assert.Contains("측정 방식이 다릅니다", u.Reason);
        Assert.Contains("TCP 443", u.Reason);
    }

    [Fact]
    public void PerHourDecidesWhenLengthsDifferAndRawCountsGetNoArrow()
    {
        // A: 20분에 1번 (시간당 3번), B: 60분에 2번 (시간당 2번) — 원시 수(1 < 2)와 시간당(3 > 2)이 반대
        var r = MeasurementComparison.Compare(Side(new Spec(20, [(300, 5)])), Side(new Spec(60, [(300, 5), (2000, 5)])));
        Assert.Contains(CompareCondition.LengthDiffers, r.Conditions);
        Assert.Equal((1, 2), (r.Outages.A, r.Outages.B));
        Assert.Equal(Lower.None, r.Outages.Lower);
        Assert.Equal(Lower.B, r.PerHour.Lower);
        Assert.Equal(3.0, r.PerHour.A!.Value, 3);
    }

    [Fact]
    public void ShortObservationHasNoFrequency()
    {
        var r = MeasurementComparison.Compare(Side(new Spec(5, None)), Side(new Spec(20, None)));
        Assert.Null(r.PerHour.A);
        Assert.Equal(Lower.None, r.PerHour.Lower);
        Assert.NotNull(r.PerHour.Suppressed);
    }

    [Fact]
    public void DifferentTimeoutsSuppressEveryLossBasedArrow()
    {
        var r = MeasurementComparison.Compare(Side(new Spec(20, [(300, 30)])), Side(new Spec(20, None, TimeoutMs: 2000)));
        Assert.Contains(CompareCondition.TimeoutDiffers, r.Conditions);
        Assert.Equal(Lower.None, r.Grade.Lower);
        Assert.Equal(Lower.None, r.PerHour.Lower);
        Assert.Equal(Lower.None, r.Longest.Lower);
        Assert.All(r.Targets, p => Assert.Equal(Lower.None, p.LossRate));
        Assert.All(r.Targets, p => Assert.Equal(Lower.None, p.Consecutive));
    }

    [Fact]
    public void LossRateArrowNeedsHalfAPointAndAHundredValidAnswers()
    {
        // 1200회차 중 30회차 손실 = 2.5%p 차이 → 화살표
        var r = MeasurementComparison.Compare(Side(new Spec(20, [(300, 30)])), Side(new Spec(20, None)));
        var a = r.Targets.Single(p => p.A.Target.Id == "a");
        Assert.Equal(Lower.B, a.LossRate);
        Assert.Equal(Lower.B, a.Consecutive);   // 연속 손실 경과 시간 30초 vs 없음
        Assert.Equal(Lower.None, r.Targets.Single(p => p.A.Target.Id == "router").LossRate);
        // 1분(60회차)뿐이면 유효 응답 100번 미만 → 억제
        var shortR = MeasurementComparison.Compare(Side(new Spec(1, [(10, 5)])), Side(new Spec(1, None)));
        Assert.Equal(Lower.None, shortR.Targets.Single(p => p.A.Target.Id == "a").LossRate);
    }

    [Fact]
    public void ATargetWithNoValidAnswersIsNotCalledTheOneWithFewerLosses()
    {
        // A의 공유기는 모두 검사 오류(유효 응답 0), B의 공유기는 29초 연속 손실: "A가 더 적음"이 아니라 비교 안 함 (G4 M7 1라운드 a3)
        var r = MeasurementComparison.Compare(Side(new Spec(20, None, Broken: "router")), Side(new Spec(20, [(300, 29)], LocalDown: true)));
        var router = r.Targets.Single(p => p.A.Target.Id == "router");
        Assert.Equal(0, router.A.Valid);
        Assert.Equal(Lower.None, router.Consecutive);
        Assert.Equal("검사 오류를 뺀 요청이 100번보다 적습니다", router.ConsecutiveSuppressed);
        Assert.Equal(Lower.None, router.LossRate);
        // 외부 대상은 그대로 비교한다 (A 손실 없음, B 29초)
        Assert.Equal(Lower.A, r.Targets.Single(p => p.A.Target.Id == "a").Consecutive);
    }

    [Fact]
    public void OpenEndedLongestOutageGetsNoArrow()
    {
        // A는 끝까지 이어진 끊김 (끝을 모름)
        var r = MeasurementComparison.Compare(Side(new Spec(20, [(1170, 30)])), Side(new Spec(20, [(300, 5)])));
        Assert.True(r.Longest.A!.Value.Open);
        Assert.Equal(Lower.None, r.Longest.Lower);
        Assert.NotNull(r.Longest.Suppressed);
    }

    [Fact]
    public void DifferentCompositionSuppressesMeasureLevelArrows()
    {
        var r = MeasurementComparison.Compare(Side(new Spec(20, [(300, 30)])), Side(new Spec(20, None, Targets: [Router, ExtA, ExtB with { Provider = "C사" }])));
        Assert.Contains(CompareCondition.CompositionDiffers, r.Conditions);
        Assert.Equal(Lower.None, r.Grade.Lower);
        Assert.Equal(Lower.None, r.PerHour.Lower);
        Assert.Equal(Lower.B, r.Targets.Single(p => p.A.Target.Id == "a").LossRate);   // 대응 대상 지표는 그대로
    }

    [Fact]
    public void RoundDamageSuppressesLossArrowsButEvidenceDamageOnlyWarns()
    {
        var rounds = MeasurementComparison.Compare(Side(new Spec(20, [(300, 30)], Damage: new DamageCounts(2, 0, 0, 0, 0, 0, 0, 0, 0))), Side(new Spec(20, None)));
        Assert.Contains(CompareCondition.Damaged, rounds.Conditions);
        Assert.Equal(Lower.None, rounds.Targets.Single(p => p.A.Target.Id == "a").LossRate);
        var evidence = MeasurementComparison.Compare(Side(new Spec(20, [(300, 30)], Damage: new DamageCounts(0, 0, 0, 0, 0, 0, 0, 0, 3))), Side(new Spec(20, None)));
        Assert.Contains(CompareCondition.Damaged, evidence.Conditions);
        Assert.Equal(Lower.B, evidence.Targets.Single(p => p.A.Target.Id == "a").LossRate);
    }

    [Fact]
    public void DifferentIntervalsStillCompareRunDurationsButNotTheLongestOutage()
    {
        var r = MeasurementComparison.Compare(Side(new Spec(20, [(300, 30)])), Side(new Spec(20, None, IntervalMs: 2000)));
        Assert.Contains(CompareCondition.IntervalDiffers, r.Conditions);
        Assert.Equal(Lower.None, r.Longest.Lower);
        Assert.Equal(Lower.B, r.Targets.Single(p => p.A.Target.Id == "a").Consecutive);
    }

    [Fact]
    public void NoCommonTargetsAndPickRules()
    {
        var other = new Target("x", "X", IPAddress.Parse("198.51.100.200"), TargetRole.External, "A사", ProbeMethod.Icmp);
        var r = MeasurementComparison.Compare(Side(new Spec(20, None)), Side(new Spec(20, None, Targets: [other])));
        Assert.Contains(CompareCondition.NoCommonTargets, r.Conditions);
        Assert.Empty(r.Targets);
        Assert.NotNull(MeasurementComparison.CannotPick(RecordStatus.Ok, RecordLifecycle.Running));
        Assert.NotNull(MeasurementComparison.CannotPick(RecordStatus.Unreadable, RecordLifecycle.Ended));
        Assert.Null(MeasurementComparison.CannotPick(RecordStatus.Damaged, RecordLifecycle.NoSummary));
        var s = Side(new Spec(20, None));
        Assert.Throws<ArgumentException>(() => MeasurementComparison.Compare(s, s));
    }
}
