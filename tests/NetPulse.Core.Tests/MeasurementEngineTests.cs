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
        Assert.NotNull(first);
        Assert.Equal(2, first.Rounds);
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
        await Eventually(() => engine.State.Rounds == 1, "0번 회차");
        subscription.Dispose();
        await AdvanceWhenWaiting(clock, Second);
        await Eventually(() => engine.State.Rounds == 2, "1번 회차");
        int before; lock (seen) before = seen.Count;
        Assert.DoesNotContain(2L, seen);   // 끊긴 동안은 알림 없음

        EngineState? resumed = null;
        using var again = engine.Subscribe(s => resumed = s);
        Assert.Equal(2, resumed!.Rounds);   // 다시 붙으면 최신부터
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
    public async Task StartTwiceThrows()
    {
        await using var engine = new MeasurementEngine(new ManualClock(Start), new RecordingProbe(), [Target("a")], Second);
        engine.Start();
        Assert.Throws<InvalidOperationException>(engine.Start);
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
