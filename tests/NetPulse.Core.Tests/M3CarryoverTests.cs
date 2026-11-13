using System.Net;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M3 G4 3라운드에서 M4로 넘긴 지적 (`docs/loop/STATE.md`).</summary>
public sealed class M3CarryoverTests : IDisposable
{
    private const string Id = "20261113T000000Z_carry1";
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    // 이월 1: 복귀 시각이 직전 표본과 같아도(단조 시계 정지·밀리초 절삭) 복귀 뒤 첫 표본에서 기준을 다시 잡는다
    [Fact]
    public void ResumeAtSameElapsedAsPreviousSampleStillRebases()
    {
        var g = new GapTracker(TimeSpan.FromSeconds(1));
        Assert.Null(g.CheckClock(TimeSpan.Zero, W0));
        g.OnSuspend(TimeSpan.FromMilliseconds(900), W0.AddMilliseconds(900));
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(1000), W0.AddMilliseconds(1000)));             // 잠들기 전 표본
        g.OnResume(TimeSpan.FromMilliseconds(1000), W0.AddHours(1));                                       // 직전 표본과 같은 경과 시간
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(2000), W0.AddHours(1).AddMilliseconds(1000)));
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(3000), W0.AddHours(1).AddMilliseconds(2000)));
        Assert.Equal(15_000, g.CheckClock(TimeSpan.FromMilliseconds(4000), W0.AddHours(1).AddMilliseconds(3000 + 15_000)));
    }

    [Fact]
    public void ResumeIsConsumedOnlyOnce()
    {
        var g = new GapTracker(TimeSpan.FromSeconds(1));
        Assert.Null(g.CheckClock(TimeSpan.Zero, W0));
        g.OnSuspend(TimeSpan.FromMilliseconds(500), W0);
        g.OnResume(TimeSpan.FromMilliseconds(600), W0.AddHours(1));
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(1000), W0.AddHours(1).AddMilliseconds(400)));
        // 같은 복귀로 다시 재설정하지 않는다: 그 뒤 진짜 변경은 잡힌다
        Assert.Equal(-10_000, g.CheckClock(TimeSpan.FromMilliseconds(2000), W0.AddHours(1).AddMilliseconds(1400 - 10_000)));
    }

    // 이월 3: 복귀 뒤 회차를 꺼낸 뒤·분류 잠금 전에 마감되는 원래 경쟁 순서를 고정한다
    [Fact]
    public async Task SealBetweenDequeueAndClassificationKeepsSuspendGap()
    {
        var clock = new ManualClock(Start);
        var power = new FakePower();
        var primary = new FaultyRoot(new DiskRecordRoot(dir.Sub("p"), "primary"), "VOL-A");
        var fallback = new FaultyRoot(new DiskRecordRoot(dir.Sub("f"), "fallback"), "VOL-B");
        var profile = new Profile("p", "시험",
            [
                new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
                new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
            ], Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Any);
        var s = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = clock, ProbeFor = _ => new RecordingProbe(), Primary = primary, Fallback = fallback, Power = power,
            NewId = _ => Id, DrainTimeout = TimeSpan.FromMilliseconds(200), SealTimeout = TimeSpan.FromSeconds(5),
        });
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        for (int i = 0; i < 2; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        await Eventually(() => s.Status.Observed == 3, "관측 3");
        power.Suspend();
        power.Resume();
        // 복귀 뒤 첫 회차(3)를 꺼낸 직후 멈춰 세운다. 이 회차가 분류되면 절전 공백이 닫힐 참이다
        s.TestHookAfterDequeue = round => { if (round == 3) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "회차 3을 꺼냄");
        var stopping = s.StopAsync();
        await Eventually(() => s.Sealed, "마감");
        release.Set();
        var result = await stopping;

        Assert.False(result.SealFailed);
        Assert.Equal(1, result.Summary!.Unprocessed);   // 회차 3은 미처리
        var rec = RecordReader.Read(RecordReader.FindSegments(Id, [primary.Path!, fallback.Path!]));
        var gap = Assert.Single(rec.Gaps);              // 공백은 사라지지 않고 측정 끝에서 닫힌다
        Assert.Equal(GapReason.Suspend, gap.Reason);
        Assert.Equal(2, gap.LastRoundBefore);
        Assert.Null(gap.FirstRoundAfter);
    }

    private sealed class FakePower : IPowerEvents
    {
        private Action? suspend, resume;
        public IDisposable Subscribe(Action onSuspend, Action onResume) { suspend = onSuspend; resume = onResume; return new Nothing(); }
        public void Suspend() => suspend!();
        public void Resume() => resume!();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
