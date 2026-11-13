using NetPulse.Core.Measurement;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

public sealed class RoundSchedulerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static (List<RoundTick> Ticks, Task Run, CancellationTokenSource Cts) Launch(ManualClock clock, long? limit = null)
    {
        var ticks = new List<RoundTick>();
        var cts = new CancellationTokenSource();
        var scheduler = new RoundScheduler(clock, Second, limit);
        var run = Task.Run(async () => { await foreach (var t in scheduler.RunAsync(cts.Token)) lock (ticks) ticks.Add(t); });
        return (ticks, run, cts);
    }

    private static int Count(List<RoundTick> ticks) { lock (ticks) return ticks.Count; }

    [Fact]
    public async Task FirstRoundIsZeroAndStartsImmediately()
    {
        var clock = new ManualClock(Start);
        var (ticks, _, cts) = Launch(clock, limit: 1);
        await Eventually(() => Count(ticks) == 1, "0번 회차");
        Assert.Equal(0, ticks[0].Index);
        Assert.Equal(0, ticks[0].SkippedBefore);
        cts.Cancel();
    }

    [Fact]
    public async Task EarlyWakeNeverRepeatsARound()
    {
        // M0 스파이크 버그: 타이머가 일찍 깨면 같은 회차를 두 번 냈다.
        var clock = new ManualClock(Start) { EarlyWake = TimeSpan.FromMilliseconds(40) };
        var (ticks, run, _) = Launch(clock, limit: 6);
        for (int i = 0; i < 5; i++)
        {
            await Eventually(() => Count(ticks) == i + 1, $"{i}번 회차");
            // 일찍 깨운 뒤 아직 예정 시각 전이면 스케줄러는 다시 기다려야 한다
            await AdvanceWhenWaiting(clock, Second - TimeSpan.FromMilliseconds(30));
            await AdvanceWhenWaiting(clock, TimeSpan.FromMilliseconds(30));
        }
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([0L, 1, 2, 3, 4, 5], ticks.Select(t => t.Index));
    }

    [Fact]
    public async Task LateRoundsAreSkippedAndCounted()
    {
        var clock = new ManualClock(Start);
        var (ticks, _, cts) = Launch(clock);
        await Eventually(() => Count(ticks) == 1, "0번 회차");
        await AdvanceWhenWaiting(clock, TimeSpan.FromMilliseconds(3500));
        await Eventually(() => Count(ticks) == 2, "다음 회차");
        Assert.Equal(3, ticks[1].Index);
        Assert.Equal(2, ticks[1].SkippedBefore);
        Assert.Equal(TimeSpan.FromMilliseconds(500), ticks[1].Late);
        cts.Cancel();
    }

    [Fact]
    public async Task CancellationStops()
    {
        var clock = new ManualClock(Start);
        var (ticks, run, cts) = Launch(clock);
        await Eventually(() => clock.WaiterCount > 0, "기다림 시작");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(ticks);
    }

    [Fact]
    public async Task RoundLimitIsExclusive()
    {
        var clock = new ManualClock(Start);
        var (ticks, run, _) = Launch(clock, limit: 3);
        for (int i = 0; i < 2; i++) await AdvanceWhenWaiting(clock, Second);
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([0L, 1, 2], ticks.Select(t => t.Index));
    }
}
