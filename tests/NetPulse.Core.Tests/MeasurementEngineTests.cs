using NetPulse.Core.Measurement;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

public sealed class MeasurementEngineTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ResultsBeforeSubscribeAreDeliveredOnSubscribe()
    {
        // 화면이 준비되기 전에 도착한 결과 (M0 C안 버그의 원인)
        var clock = new ManualClock(Start);
        await using var engine = new MeasurementEngine(clock, new RecordingProbe(), [Target("a")], Second);
        engine.Start();
        await Eventually(() => engine.State.Rounds == 1, "0번 회차");
        await AdvanceWhenWaiting(clock, Second);
        await Eventually(() => engine.State.Rounds == 2, "1번 회차");

        EngineState? first = null;
        using var subscription = engine.Subscribe(s => first ??= s);
        await Eventually(() => first is not null, "구독 직후 알림");
        Assert.Equal(2, first!.Rounds);
        Assert.Equal(1, first.Latest!.Tick.Index);
    }

    [Fact]
    public async Task ResubscribingContinuesFromLatest()
    {
        var clock = new ManualClock(Start);
        await using var engine = new MeasurementEngine(clock, new RecordingProbe(), [Target("a")], Second);
        var seen = new List<long>();
        var subscription = engine.Subscribe(s => { lock (seen) seen.Add(s.Rounds); });
        engine.Start();
        await Eventually(() => { lock (seen) return seen.Contains(1); }, "0번 회차 알림");
        subscription.Dispose();
        await AdvanceWhenWaiting(clock, Second);
        await Eventually(() => engine.State.Rounds == 2, "1번 회차");
        int before; lock (seen) before = seen.Count;
        Assert.DoesNotContain(2L, seen);   // 끊긴 동안은 알림 없음

        EngineState? resumed = null;
        using var again = engine.Subscribe(s => resumed ??= s);
        await Eventually(() => resumed is not null, "다시 구독한 직후 알림");
        Assert.Equal(2, resumed!.Rounds);   // 다시 붙으면 최신부터
        lock (seen) Assert.Equal(before, seen.Count);
    }

    [Fact]
    public async Task StopDropsLaterResultsAndFinishesWithinTimeout()
    {
        var clock = new ManualClock(Start);
        var gate = new TaskCompletionSource();
        var probe = new SlowProbe(gate.Task);
        var engine = new MeasurementEngine(clock, probe, [Target("a")], Second);
        engine.Start();
        await Eventually(() => probe.Started, "측정 시작");
        var stop = engine.StopAsync(TimeSpan.FromSeconds(3));
        gate.SetResult();   // 멈추는 중에 결과가 도착한다
        Assert.True(await stop);
        Assert.Equal(0, engine.State.Rounds);   // 멈춘 뒤 도착한 결과는 버린다
        Assert.False(engine.State.Running);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ProbeExceptionBecomesErrorResult()
    {
        var clock = new ManualClock(Start);
        await using var engine = new MeasurementEngine(clock, new RecordingProbe((_, _) => throw new InvalidOperationException()), [Target("a")], Second);
        engine.Start();
        await Eventually(() => engine.State.Rounds == 1, "0번 회차");
        var result = engine.State.Latest!.Results[0].Result;
        Assert.Equal(ProbeOutcome.Error, result.Outcome);
        Assert.StartsWith("probe-exception:", result.Status);
    }

    [Fact]
    public async Task SubscriberExceptionDoesNotStopMeasuring()
    {
        var clock = new ManualClock(Start);
        await using var engine = new MeasurementEngine(clock, new RecordingProbe(), [Target("a")], Second);
        using var bad = engine.Subscribe(_ => throw new InvalidOperationException());
        engine.Start();
        await Eventually(() => engine.State.Rounds == 1, "0번 회차");
        await AdvanceWhenWaiting(clock, Second);
        await Eventually(() => engine.State.Rounds == 2, "1번 회차");
    }

    [Fact]
    public async Task FinishesAtRoundLimit()
    {
        var clock = new ManualClock(Start);
        await using var engine = new MeasurementEngine(clock, new RecordingProbe(), [Target("a")], Second, roundLimit: 2);
        engine.Start();
        await AdvanceWhenWaiting(clock, Second);
        await Eventually(() => engine.State.Finished, "끝");
        Assert.Equal(2, engine.State.Rounds);
        Assert.False(engine.State.Running);
    }

    [Fact]
    public async Task OverrunningTheLimitStillAddsUpToTheLimit()
    {
        // 0번 회차 측정이 주기보다 오래 걸려 상한을 넘기면 나머지는 건너뜀으로 센다 (G4 1라운드 지적 7)
        var clock = new ManualClock(Start);
        var probe = new RecordingProbe((_, _) => { clock.Advance(TimeSpan.FromMilliseconds(3500)); return ProbeResult.Success(1); });
        await using var engine = new MeasurementEngine(clock, probe, [Target("a")], Second, roundLimit: 3);
        engine.Start();
        await Eventually(() => engine.State.Finished, "끝");
        Assert.Equal(1, engine.State.Rounds);
        Assert.Equal(2, engine.State.Skipped);
    }

    [Fact]
    public async Task SubscribersGetStatesInOrderAndNeverConcurrently()
    {
        // 구독 초기 알림과 측정 알림이 겹쳐도 순서가 거꾸로 되거나 동시에 불리지 않는다 (G4 1라운드 지적 4)
        var clock = new ManualClock(Start);
        await using var engine = new MeasurementEngine(clock, new RecordingProbe(), [Target("a")], Second);
        engine.Start();
        int inside = 0, overlaps = 0;
        var perSubscriber = new List<List<long>>();
        var subscriptions = new List<IDisposable>();
        for (int i = 0; i < 40; i++)
        {
            var mine = new List<long>();
            perSubscriber.Add(mine);
            subscriptions.Add(engine.Subscribe(s =>
            {
                if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps);
                Thread.SpinWait(2000);
                lock (mine) mine.Add(s.Rounds);
                Interlocked.Decrement(ref inside);
            }));
            await AdvanceWhenWaiting(clock, Second);
        }
        await Eventually(() => perSubscriber.All(m => { lock (m) return m.Count > 0 && m[^1] == 41; }), "모든 구독자가 41회차를 받음");
        foreach (var s in subscriptions) s.Dispose();
        Assert.Equal(0, overlaps);
        foreach (var m in perSubscriber) lock (m) Assert.Equal(m.OrderBy(r => r), m);
    }

    [Fact]
    public async Task EachSubscriberSeesNonDecreasingRounds()
    {
        var clock = new ManualClock(Start);
        await using var engine = new MeasurementEngine(clock, new RecordingProbe(), [Target("a")], Second);
        var seen = new List<long>();
        using var subscription = engine.Subscribe(s => { lock (seen) seen.Add(s.Rounds); });
        engine.Start();
        for (int i = 0; i < 30; i++) await AdvanceWhenWaiting(clock, Second);
        await Eventually(() => { lock (seen) return seen.Count > 0 && seen[^1] == 31; }, "마지막 알림");
        lock (seen) Assert.Equal(seen.OrderBy(r => r), seen);
    }

    [Fact]
    public async Task BlockedSubscriberDoesNotBlockMeasuringOrStop()
    {
        // 화면 쪽 구독자가 멈춰도 측정과 종료가 막히지 않는다 (G4 1라운드 지적 5)
        var clock = new ManualClock(Start);
        using var release = new ManualResetEventSlim();
        var engine = new MeasurementEngine(clock, new RecordingProbe(), [Target("a")], Second);
        using var stuck = engine.Subscribe(_ => release.Wait(TimeSpan.FromSeconds(30)));
        engine.Start();
        await AdvanceWhenWaiting(clock, Second);
        await Eventually(() => engine.State.Rounds == 2, "구독자가 멈춘 동안에도 1번 회차");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await engine.StopAsync(TimeSpan.FromSeconds(3)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"종료가 구독자에 막혔다: {watch.Elapsed}");
        Assert.False(engine.State.Running);
        release.Set();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task StopGivesUpAfterTimeoutWhenProbeIgnoresCancellation()
    {
        // 취소를 무시하는 측정이 있어도 제한시간 뒤에 돌아오고, 늦게 도착한 결과는 버린다
        var clock = new ManualClock(Start);
        var gate = new TaskCompletionSource();
        var probe = new SlowProbe(gate.Task);
        var engine = new MeasurementEngine(clock, probe, [Target("a")], Second);
        engine.Start();
        await Eventually(() => probe.Started, "측정 시작");
        Assert.False(await engine.StopAsync(TimeSpan.FromMilliseconds(200)));
        Assert.False(engine.State.Running);
        gate.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, engine.State.Rounds);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task StopDoesNotBlockOnAStuckCancellationCallback()
    {
        // 공급자가 등록한 취소 콜백이 멈춰도 StopAsync를 부른 쪽(UI 스레드)은 막히지 않고 제한시간 뒤 돌아온다 (G4 2라운드 지적 1)
        var clock = new ManualClock(Start);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var probe = new StuckCancellationProbe(entered, release);
        var engine = new MeasurementEngine(clock, probe, [Target("a")], Second);
        engine.Start();
        await Eventually(() => probe.Started, "측정 시작");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stop = engine.StopAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync 호출이 취소 콜백에 막혔다: {watch.Elapsed}");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "취소 콜백이 불려야 한다");
        Assert.False(await stop);   // 콜백이 멈춰 있으니 제한시간 안에 끝나지 못했다고 알린다
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"제한시간을 지키지 않았다: {watch.Elapsed}");
        Assert.False(engine.State.Running);
        release.Set();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task StoppingAgainWaitsForTheSameUnfinishedWork()
    {
        // 첫 호출이 시간 초과로 끝난 뒤 다시 불러도, 작업이 실제로 끝나기 전에는 true가 아니다 (M1 G4 3라운드 이월 1)
        var clock = new ManualClock(Start);
        var gate = new TaskCompletionSource();
        var probe = new SlowProbe(gate.Task);
        var engine = new MeasurementEngine(clock, probe, [Target("a")], Second);
        engine.Start();
        await Eventually(() => probe.Started, "측정 시작");
        Assert.False(await engine.StopAsync(TimeSpan.FromMilliseconds(100)));
        Assert.False(await engine.StopAsync(TimeSpan.FromMilliseconds(100)));
        Assert.False(engine.IsStopCompleted);
        gate.SetResult();   // 취소를 무시하던 측정이 이제 끝난다
        Assert.True(await engine.StopAsync(TimeSpan.FromSeconds(3)));
        Assert.True(engine.IsStopCompleted);
        Assert.Equal(0, engine.State.Rounds);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task StartTwiceThrows()
    {
        await using var engine = new MeasurementEngine(new ManualClock(Start), new RecordingProbe(), [Target("a")], Second);
        engine.Start();
        Assert.Throws<InvalidOperationException>(engine.Start);
    }

    /// <summary>취소되면 콜백 안에서 멈추는 공급자 (콜백 진입은 entered로 알린다).</summary>
    private sealed class StuckCancellationProbe(ManualResetEventSlim entered, ManualResetEventSlim release) : IProbe
    {
        public volatile bool Started;

        public async Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                done.TrySetCanceled(cancellationToken);
            });
            Started = true;
            return await done.Task;
        }
    }

    private sealed class SlowProbe(Task gate) : IProbe
    {
        public volatile bool Started;

        public async Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken)
        {
            Started = true;
            await gate;
            return ProbeResult.Success(1);
        }
    }
}
