using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Demo;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>측정 세션 (M3 설계 6.1, 6.4, 6.6, 7, 9절).</summary>
public sealed class SessionTests : IDisposable
{
    private readonly TempDir primaryDir = new();
    private readonly TempDir fallbackDir = new();
    private readonly FaultyRoot primary;
    private readonly FaultyRoot fallback;

    public SessionTests()
    {
        primary = new FaultyRoot(new DiskRecordRoot(primaryDir.Path, "primary"), "VOL-A");
        fallback = new FaultyRoot(new DiskRecordRoot(fallbackDir.Path, "fallback"), "VOL-B");
    }

    public void Dispose()
    {
        primaryDir.Dispose();
        fallbackDir.Dispose();
    }

    private static readonly Profile OneMinute = new("p", "시험",
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Any);

    /// <summary>회차 3의 배수에서 b가 손실인 공급자.</summary>
    private static IProbe Pattern() => new RecordingProbe((t, r) => t.Id == "b" && r % 3 == 0 ? ProbeResult.Lost("TimedOut") : ProbeResult.Success(5 + r));

    private SessionOptions Options(IClock clock, Func<SessionOptions, SessionOptions>? tweak = null)
    {
        var probe = Pattern();
        var o = new SessionOptions
        {
            Profile = OneMinute, Clock = clock, ProbeFor = _ => probe, Primary = primary, Fallback = fallback,
            NewId = _ => "20261113T000000Z_test01", RetryTimeout = TimeSpan.FromMilliseconds(300),
        };
        return tweak is null ? o : tweak(o);
    }

    private static readonly TargetResult[] NoResults = [];
    private static readonly LossRun[] NoRuns = [];

    /// <summary>1초씩 count번 돌리고, 엔진이 마지막 회차를 낼 때까지 기다린다.</summary>
    private static async Task Rounds(ManualClock clock, MeasurementSession s, int count)
    {
        for (int i = 0; i < count; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        long target = (long)clock.Elapsed.TotalSeconds;
        await Eventually(() => s.Engine.State.Latest?.Tick.Index >= target, $"엔진 회차 {target}");
    }

    private MeasurementRecord Reread(string id = "20261113T000000Z_test01") =>
        RecordReader.Read(RecordReader.FindSegments(id, [primaryDir.Path, fallbackDir.Path]));

    private static void SameRounds(IReadOnlyList<RoundData> expected, IReadOnlyList<RoundData> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i] with { Results = NoResults, Wall = default }, actual[i] with { Results = NoResults, Wall = default });
            Assert.Equal(RecordSchema.Iso(expected[i].Wall), RecordSchema.Iso(actual[i].Wall));
            Assert.Equal(expected[i].Results, actual[i].Results);
        }
    }

    private static JsonObject[] Events(MeasurementRecord rec, string type) => rec.Events.Where(e => (string?)e["type"] == type).ToArray();

    [Fact]
    public async Task CompletedMeasurementRoundTripsThroughFiles()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 59);
        var result = await s.Completion.WaitAsync(TestSupport.SessionLimit, TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);
        Assert.Equal((60L, 60L, 0L, 0L, 0L), (result.Summary!.Observed, result.Summary.Committed, result.Summary.Pending, result.Summary.Dropped, result.Summary.Unprocessed));
        Assert.Equal(59, result.Summary.LastCommittedRound);
        Assert.Equal("primary", result.LocationKind);

        var rec = Reread();
        Assert.Equal(RecordStatus.Ok, rec.Status);
        Assert.Equal(EndReason.Completed, rec.EndReason);
        SameRounds(s.ObservedRounds, rec.Rounds);
        Assert.Equal(result.Stats, rec.Stats, new StatsComparer());
        Assert.Equal(20, rec.Stats!.Targets.Single(t => t.TargetId == "b").Lost);
        Assert.Null(rec.MissingNotice);
    }

    [Fact]
    public async Task StopEndsWithStoppedAndEverythingCommitted()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 4);
        var result = await s.StopAsync();
        Assert.Equal(EndReason.Stopped, result.EndReason);
        Assert.False(result.SealFailed);
        Assert.Equal(result.Summary!.Observed, result.Summary.Committed);
        var rec = Reread();
        Assert.Equal(EndReason.Stopped, rec.EndReason);
        SameRounds(s.ObservedRounds, rec.Rounds);
        Assert.Same(result, await s.StopAsync());   // 두 번째 호출은 같은 결과
    }

    [Fact]
    public async Task SlowStorageWithFastDemoCountsEveryRoundExactlyOnce()
    {
        // 30배속(회차 약 33 ms)에 쓰기마다 50 ms: 처리가 밀려 큐가 찼다가 끝에 다 배출된다
        var scenario = Scenario.Load(Path.Combine(AppContext.BaseDirectory, "scenarios", "outage.json"));
        var clock = new ScaledClock(30, Start);
        primary.Delay = () => TimeSpan.FromMilliseconds(50);
        var demo = new DemoProbe(scenario);
        var profile = OneMinute with
        {
            Targets = scenario.ProbeTargets.Select(t => new Target(t.Id, t.Id, t.Address, t.Role == "local" ? TargetRole.Local : TargetRole.External, t.Id, ProbeMethod.Icmp)).ToArray(),
        };
        var s = MeasurementSession.Start(Options(clock, o => o with { Profile = profile, ProbeFor = _ => demo }));
        var result = await s.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);
        var rec = Reread();
        Assert.Equal(0, rec.Damage.Total);
        var observed = s.ObservedRounds;
        Assert.Equal(observed.Count, observed.Select(r => r.Round).Distinct().Count());
        Assert.Equal(observed.Count + result.Summary!.Unprocessed, s.Engine.State.Rounds);
        SameRounds(observed, rec.Rounds);
        // 데모 결과는 회차와 대상만으로 정해진다: 기록된 결과가 시나리오와 같아야 한다
        foreach (var r in rec.Rounds.Where(r => !r.Gap))
            foreach (var x in r.Results)
                Assert.Equal(demo.Resolve(x.TargetId, r.Round).Outcome, x.Outcome);
    }

    [Fact]
    public async Task FullQueueCountsUnprocessedRangeInsteadOfDropping()
    {
        var clock = new ManualClock(Start);
        var release = new ManualResetEventSlim(false);
        primary.Delay = () => { release.Wait(TimeSpan.FromSeconds(10)); return TimeSpan.Zero; };
        var s = MeasurementSession.Start(Options(clock, o => o with { QueueCapacity = 3 }));
        await Rounds(clock, s, 9);   // 회차 0~9: 처리 작업은 첫 쓰기에서 막혀 있다
        Assert.True(s.Status.Unprocessed >= 5, "미처리 " + s.Status.Unprocessed);
        release.Set();
        var result = await s.StopAsync();
        Assert.Equal(s.Engine.State.Rounds, result.Summary!.Observed + result.Summary.Unprocessed);
        var rec = Reread();
        var queue = Assert.Single(Events(rec, "queue"));
        Assert.Equal("unprocessed", (string?)queue["kind"]);
        Assert.Equal(result.Summary.Unprocessed, (long)queue["toRound"]! - (long)queue["fromRound"]! + 1);
        Assert.Null(rec.MissingNotice);   // 관측 수와 원본이 같다 (미처리는 관측 밖)
    }

    [Fact]
    public async Task WriteFailuresKeepPendingThenDropOldestAndRecover()
    {
        var clock = new ManualClock(Start);
        bool failing = false;
        primary.Next = name => failing && name.StartsWith("rounds", StringComparison.Ordinal) ? Fault.DiskFull : Fault.None;
        var s = MeasurementSession.Start(Options(clock, o => o with { PendingLimit = 3, Fallback = new FaultyRoot(new DiskRecordRoot(fallbackDir.Path, "fallback"), "VOL-A") }));
        await Rounds(clock, s, 1);                     // 0, 1 확정
        await Eventually(() => s.Status.Committed == 2, "확정 2");
        failing = true;
        await Rounds(clock, s, 7);                     // 2~8 실패: 대기 3, 폐기 4 (같은 볼륨 가득 참이라 옮기지 않음)
        // 관측은 쓰기 전에 더해지므로(M3 G4 1라운드), 회차 8의 실패한 쓰기와 그 뒤 폐기까지 끝난 것을 신호로 기다린다
        await Eventually(() => s.Status.Observed == 9 && primary.WritesDone("rounds") == 9 && s.Status.Dropped == 4, "회차 8 쓰기·폐기 완료");
        Assert.Equal((3L, 4L), (s.Status.Pending, s.Status.Dropped));
        Assert.True(s.Status.StorageFailing);
        Assert.Equal("primary", s.Status.LocationKind);
        failing = false;
        await Rounds(clock, s, 1);
        await Eventually(() => s.Status.Pending == 0, "대기 비움");
        var result = await s.StopAsync();
        Assert.Equal((10L, 6L, 4L), (result.Summary!.Observed, result.Summary.Committed, result.Summary.Dropped));

        var rec = Reread();
        Assert.Equal([0L, 1, 6, 7, 8, 9], rec.Rounds.Select(r => r.Round));
        Assert.Contains("관측 10회차 중 원본 6회차", rec.MissingNotice);
        var storage = Events(rec, "storage").Select(e => (string?)e["kind"]).ToArray();
        Assert.Equal(["writeFailed", "dropped", "recovered"], storage.Distinct());
        var dropped = Events(rec, "storage").Where(e => (string?)e["kind"] == "dropped").ToArray();
        Assert.Equal(2L, (long)dropped[0]["fromRound"]!);
        Assert.Equal(5L, dropped.Max(e => (long)e["toRound"]!));
    }

    [Fact]
    public async Task UnsavedRoundsAreReturnedAtEnd()
    {
        var clock = new ManualClock(Start);
        primary.Next = name => name.StartsWith("rounds", StringComparison.Ordinal) ? Fault.DiskFull : Fault.None;
        var s = MeasurementSession.Start(Options(clock, o => o with { Fallback = new FaultyRoot(new DiskRecordRoot(fallbackDir.Path, "fallback"), "VOL-A") }));
        await Rounds(clock, s, 2);
        await Eventually(() => s.Status.Observed == 3, "관측 3");
        var result = await s.StopAsync();
        Assert.Equal(3, result.Unsaved);
        Assert.Equal((3L, 0L, 3L), (result.Summary!.Observed, result.Summary.Committed, result.Summary.Pending));
    }

    [Fact]
    public async Task OtherFailureRelocatesToFallbackSegment()
    {
        var clock = new ManualClock(Start);
        bool broken = false;
        primary.Next = name => broken && name.StartsWith("rounds", StringComparison.Ordinal) ? Fault.PartialWrite : Fault.None;
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 2);
        await Eventually(() => s.Status.Committed == 3, "확정 3");
        broken = true;
        await Rounds(clock, s, 3);
        await Eventually(() => s.Status.Committed == 6, "옮긴 뒤 확정 6");
        Assert.Equal("fallback", s.Status.LocationKind);
        var result = await s.StopAsync();
        Assert.Equal("fallback", result.LocationKind);

        Assert.False(File.Exists(Path.Combine(primaryDir.Path, s.Id, "summary.json")));
        Assert.True(File.Exists(Path.Combine(fallbackDir.Path, s.Id, "summary.json")));   // 요약은 마지막 조각에
        var rec = Reread();
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (long)i), rec.Rounds.Select(r => r.Round));
        Assert.Empty(rec.MissingSegments);
        Assert.Equal(EndReason.Stopped, rec.EndReason);
        Assert.Null(rec.MissingNotice);
        var fromFallback = RecordReader.Read([Path.Combine(fallbackDir.Path, s.Id)]);
        Assert.Contains(fromFallback.Events, e => (string?)e["type"] == "storage" && (string?)e["kind"] == "relocated" && (string?)e["from"] == "primary");
        Assert.Equal([0], fromFallback.MissingSegments);   // 조각 하나만 열면 앞 조각이 없다고 알린다
    }

    [Fact]
    public async Task StartFallsBackAndRefusesWithoutStorageOrValidProfile()
    {
        var clock = new ManualClock(Start);
        primary.FailCreate = true;
        var s = MeasurementSession.Start(Options(clock));
        Assert.Equal("fallback", s.Status.LocationKind);
        await s.StopAsync();

        fallback.FailCreate = true;
        Assert.Throws<SessionStartException>(() => MeasurementSession.Start(Options(clock, o => o with { NewId = _ => "x2" })));
        primary.FailCreate = fallback.FailCreate = false;
        var bad = Assert.Throws<SessionStartException>(() => MeasurementSession.Start(Options(clock, o => o with { Profile = OneMinute with { Targets = [] } })));
        Assert.Contains("측정할 대상이 없습니다", bad.Message);   // M12 문구: 내부 코드 대신 사유 문장 (wording-applied.md)
        Assert.DoesNotContain("no-targets", bad.Message);
    }

    [Fact]
    public async Task UnexpectedFailureEndsWithError()
    {
        var clock = new ManualClock(Start);
        primary.Next = name => name.StartsWith("rounds", StringComparison.Ordinal) ? Fault.Unexpected : Fault.None;
        var s = MeasurementSession.Start(Options(clock));
        var result = await s.Completion.WaitAsync(TestSupport.SessionLimit, TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Error, result.EndReason);
        Assert.Equal(nameof(InvalidOperationException), result.Error);
        Assert.Equal(EndReason.Error, Reread().EndReason);
    }

    [Fact]
    public async Task SealsWhenDrainTakesTooLongAndKeepsResultEvenIfWorkerFinishesLate()
    {
        var clock = new ManualClock(Start);
        var release = new ManualResetEventSlim(false);
        primary.Delay = () => { release.Wait(TimeSpan.FromSeconds(10)); return TimeSpan.Zero; };
        var s = MeasurementSession.Start(Options(clock, o => o with
        {
            DrainTimeout = TimeSpan.FromMilliseconds(200), SealTimeout = TimeSpan.FromMilliseconds(200),
        }));
        await Rounds(clock, s, 4);
        var result = await s.StopAsync();
        Assert.True(result.SealFailed);   // 처리 작업이 취소를 무시하고 쓰기에 묶여 있다
        Assert.Null(result.Summary);
        release.Set();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Same(result, await s.Completion);
        Assert.False(File.Exists(Path.Combine(primaryDir.Path, s.Id, "summary.json")), "마감 실패면 요약을 쓰지 않는다");
        Assert.Equal(EndReason.Interrupted, Reread().EndReason);
    }

    [Fact]
    public async Task SealAfterDrainTimeoutTurnsQueuedRoundsIntoUnprocessed()
    {
        var clock = new ManualClock(Start);
        var gate = new SemaphoreSlim(0);
        int writes = 0;
        primary.Delay = () =>
        {
            // 첫 회차 줄 쓰기만 막는다 (이벤트 쓰기는 통과)
            if (Interlocked.Increment(ref writes) == 1) gate.Wait(TimeSpan.FromSeconds(10));
            return TimeSpan.Zero;
        };
        primary.Next = _ => Fault.None;
        var s = MeasurementSession.Start(Options(clock, o => o with { DrainTimeout = TimeSpan.FromMilliseconds(200), SealTimeout = TimeSpan.FromSeconds(5) }));
        await Rounds(clock, s, 4);
        var stopping = s.StopAsync();
        await Task.Delay(400, TestContext.Current.CancellationToken);
        gate.Release();
        var result = await stopping;
        Assert.False(result.SealFailed);
        Assert.Equal(s.Engine.State.Rounds, result.Summary!.Observed + result.Summary.Unprocessed);
        Assert.True(result.Summary.Unprocessed >= 1);
        Assert.Contains(Events(Reread(), "queue"), e => (string?)e["kind"] == "unprocessed");
    }

    [Fact]
    public async Task RouteChangeIsTaggedWithFirstChangedRoundEvenWhenStorageLags()
    {
        var clock = new ManualClock(Start);
        primary.Delay = () => TimeSpan.FromMilliseconds(30);
        var routes = new FakeRoutes(r => r < 5 ? 3 : 7);
        var s = MeasurementSession.Start(Options(clock, o => o with { Routes = routes }));
        await Rounds(clock, s, 7);
        await s.StopAsync();
        var events = Events(Reread(), "route");
        Assert.Equal(4, events.Length);   // 시작 때 대상 둘, 바뀐 회차에 대상 둘
        Assert.All(events.Take(2), e => Assert.Equal(0L, (long)e["round"]!));
        Assert.All(events.Skip(2), e => Assert.Equal(5L, (long)e["round"]!));
        Assert.All(events.Skip(2), e => Assert.Equal(7, (int)e["interfaceIndex"]!));
    }

    [Fact]
    public async Task RouteFailureIsRecordedAsFailed()
    {
        var clock = new ManualClock(Start);
        var routes = new FakeRoutes(_ => null);
        var s = MeasurementSession.Start(Options(clock, o => o with { Routes = routes }));
        await Rounds(clock, s, 2);
        await s.StopAsync();
        var events = Events(Reread(), "route");
        Assert.Equal(2, events.Length);
        Assert.All(events, e => Assert.Equal("failed", (string?)e["status"]));
        Assert.All(events, e => Assert.Equal("E1168", (string?)e["code"]));
    }

    [Fact]
    public async Task SuspendMakesGapEventAndBreaksRuns()
    {
        var clock = new ManualClock(Start);
        var power = new FakePower();
        var s = MeasurementSession.Start(Options(clock, o => o with { Power = power }));
        await Rounds(clock, s, 2);                    // 0, 1, 2
        await Eventually(() => s.Status.Observed == 3, "관측 3");
        power.Suspend();
        clock.ShiftWall(TimeSpan.FromHours(1));       // 단조 시계는 멈춘 채 벽시계만 1시간
        power.Resume();
        await Rounds(clock, s, 2);                    // 3, 4 (회차 번호는 이어진다)
        var result = await s.StopAsync();
        var rec = Reread();
        var gap = Assert.Single(rec.Gaps);
        Assert.Equal(GapReason.Suspend, gap.Reason);
        Assert.Equal((2L, 3L), (gap.LastRoundBefore!.Value, gap.FirstRoundAfter!.Value));
        Assert.Empty(Events(rec, "clock"));          // 절전 차이는 시각 변경으로 보지 않는다
        Assert.Equal(result.Stats, rec.Stats, new StatsComparer());
    }

    [Fact]
    public async Task WallClockChangeIsRecordedButIsNotAGap()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 2);
        clock.ShiftWall(TimeSpan.FromSeconds(-30));
        await Rounds(clock, s, 2);
        await s.StopAsync();
        var rec = Reread();
        var change = Assert.Single(Events(rec, "clock"));
        Assert.Equal(-30_000, (long)change["deltaMs"]!);
        Assert.Empty(rec.Gaps);
    }

    private sealed class FakeRoutes(Func<long, int?> index) : IRouteObserver
    {
        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round) =>
            targets.Select(t => index(round) is int i ? new RouteSample(t.Id, i, round % 30 == 0 ? "wired" : null, true, null)
                : new RouteSample(t.Id, null, null, false, "E1168")).ToArray();
    }

    private sealed class FakePower : IPowerEvents
    {
        private Action? suspend, resume;
        public IDisposable Subscribe(Action onSuspend, Action onResume) { suspend = onSuspend; resume = onResume; return new Nothing(); }
        public void Suspend() => suspend!();
        public void Resume() => resume!();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>통계 비교 (목록 필드는 내용으로).</summary>
    private sealed class StatsComparer : IEqualityComparer<NetPulse.Core.Statistics.MeasurementStats?>
    {
        public bool Equals(NetPulse.Core.Statistics.MeasurementStats? x, NetPulse.Core.Statistics.MeasurementStats? y)
        {
            if (x is null || y is null) return x is null && y is null;
            if ((x.Rounds, x.Skipped, x.GapRounds, x.Incomplete) != (y.Rounds, y.Skipped, y.GapRounds, y.Incomplete)) return false;
            return x.Targets.Zip(y.Targets).All(p =>
                p.First with { Runs = NoRuns, LastSuccess = null, LastLoss = null, LastError = null } == p.Second with { Runs = NoRuns, LastSuccess = null, LastLoss = null, LastError = null }
                && p.First.Runs.Select(r => (r.FirstRound, r.LastRound, r.Count, r.MinDurationMs, r.MaxDurationMs))
                    .SequenceEqual(p.Second.Runs.Select(r => (r.FirstRound, r.LastRound, r.Count, r.MinDurationMs, r.MaxDurationMs))));
        }
        public int GetHashCode(NetPulse.Core.Statistics.MeasurementStats? obj) => 0;
    }
}
