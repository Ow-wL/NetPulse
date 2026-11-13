using NetPulse.Core.Measurement;
using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>공백·시각 변경 판정 (M3 설계 7절).</summary>
public sealed class GapTrackerTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static GapVerdict At(GapTracker g, long round, long skipped = 0, long? startMs = null, long? endMs = null)
    {
        long start = startMs ?? round * 1000;
        return g.OnRound(round, skipped, start, endMs ?? start + 20, W0.AddMilliseconds(start));
    }

    [Fact]
    public void SmallSkipsStaySkips()
    {
        var g = new GapTracker(Second);
        At(g, 0);
        var v = At(g, 10, skipped: 9);
        Assert.Equal(9, v.Skipped);
        Assert.False(v.Gap);
        Assert.Empty(v.Closed);
    }

    [Fact]
    public void StallBecomesGapNotSkips()
    {
        var g = new GapTracker(Second);
        At(g, 0);
        var v = At(g, 11, skipped: 10, startMs: 11_050);
        Assert.Equal(0, v.Skipped);   // 중복 계수 없음
        Assert.False(v.Gap);          // 이 회차 자체는 관측됨
        var span = Assert.Single(v.Closed);
        Assert.Equal(GapReason.Stall, span.Reason);
        Assert.Equal((1000L, 11_000L), (span.FromMs, span.ToMs));
        Assert.Equal((0L, 11L), (span.LastRoundBefore!.Value, span.FirstRoundAfter!.Value));
        Assert.Equal(W0.AddMilliseconds(1000), span.FromWall);
    }

    [Fact]
    public void SuspendWithStoppedMonotonicClockKeepsRoundNumbersButMakesGap()
    {
        var g = new GapTracker(Second);
        At(g, 0); At(g, 1);
        g.OnSuspend(TimeSpan.FromMilliseconds(1500), W0.AddMilliseconds(1500));
        g.OnResume(TimeSpan.FromMilliseconds(1510), W0.AddHours(1));   // 단조 시계는 거의 멈춤, 벽시계는 1시간
        var v = At(g, 2);
        Assert.Equal(0, v.Skipped);
        var span = Assert.Single(v.Closed);
        Assert.Equal(GapReason.Suspend, span.Reason);
        Assert.Equal((1L, 2L), (span.LastRoundBefore!.Value, span.FirstRoundAfter!.Value));
        Assert.Equal(W0.AddHours(1), span.ToWall);
    }

    [Fact]
    public void RoundInFlightAcrossSuspendIsGap()
    {
        var g = new GapTracker(Second);
        At(g, 0);
        g.OnSuspend(TimeSpan.FromMilliseconds(1300), W0);
        g.OnResume(TimeSpan.FromMilliseconds(5000), W0.AddMinutes(5));
        var inFlight = At(g, 1, startMs: 1000, endMs: 5100);   // 절전 전에 시작해 복귀 뒤에 끝남
        Assert.True(inFlight.Gap);
        Assert.Empty(inFlight.Closed);                         // 복귀 뒤 첫 정상 회차가 아직 없다
        var after = At(g, 5, skipped: 3, startMs: 5200);
        Assert.False(after.Gap);
        Assert.Equal(0, after.Skipped);                        // 절전으로 빠진 회차는 공백에 속한다
        var span = Assert.Single(after.Closed);
        Assert.Equal((0L, 5L), (span.LastRoundBefore!.Value, span.FirstRoundAfter!.Value));
    }

    [Fact]
    public void RoundStartedWhileSuspendingIsGap()
    {
        var g = new GapTracker(Second);
        g.OnSuspend(TimeSpan.FromMilliseconds(900), W0);
        Assert.True(g.Suspended);
        Assert.True(At(g, 1).Gap);   // 절전 알림 뒤, 실제로 잠들기 전에 시작한 회차
    }

    [Fact]
    public void LongSuspendWithRunningClockIsNotAlsoAStall()
    {
        var g = new GapTracker(Second);
        At(g, 0);
        g.OnSuspend(TimeSpan.FromMilliseconds(500), W0);
        g.OnResume(TimeSpan.FromSeconds(3600), W0.AddHours(1));
        var v = At(g, 3600, skipped: 3599);
        var span = Assert.Single(v.Closed);
        Assert.Equal(GapReason.Suspend, span.Reason);
        Assert.Equal(0, v.Skipped);
    }

    [Fact]
    public void ResumeAfterPlannedEndClosesAtMeasurementEnd()
    {
        var g = new GapTracker(Second);
        At(g, 0);
        g.OnSuspend(TimeSpan.FromMilliseconds(500), W0.AddMilliseconds(500));
        g.OnResume(TimeSpan.FromSeconds(100), W0.AddSeconds(100));
        var closed = Assert.Single(g.Close(60_000, W0.AddSeconds(60)));
        Assert.Equal((500L, 60_000L), (closed.FromMs, closed.ToMs));
        Assert.Equal(W0.AddSeconds(60), closed.ToWall);
        Assert.Null(closed.FirstRoundAfter);
        Assert.Equal(0, closed.LastRoundBefore);
        Assert.Empty(g.Close(60_000, W0));   // 한 번만
    }

    [Fact]
    public void SuspendWithoutResumeClosesAtEnd()
    {
        var g = new GapTracker(Second);
        g.OnSuspend(TimeSpan.FromMilliseconds(500), W0);
        g.OnSuspend(TimeSpan.FromMilliseconds(600), W0);   // 중복 알림은 무시
        var closed = Assert.Single(g.Close(2_000, W0.AddSeconds(2)));
        Assert.Equal(2_000, closed.ToMs);
        Assert.Null(closed.LastRoundBefore);
    }

    [Fact]
    public void WallClockChangeIsReportedButSuspendOnlyRebases()
    {
        var g = new GapTracker(Second);
        Assert.Null(g.CheckClock(TimeSpan.Zero, W0));
        Assert.Null(g.CheckClock(Second, W0.AddSeconds(1).AddSeconds(4)));   // 4초 차이: 기준 이하
        Assert.Equal(10_000, g.CheckClock(Second * 2, W0.AddSeconds(12)));
        Assert.Null(g.CheckClock(Second * 3, W0.AddSeconds(13)));            // 새 기준
        g.OnSuspend(Second * 3, W0.AddSeconds(13));
        g.OnResume(Second * 3, W0.AddHours(2));
        Assert.Null(g.CheckClock(Second * 4, W0.AddHours(2).AddSeconds(1)));  // 절전 차이는 시각 변경이 아니다
        Assert.Null(g.CheckClock(Second * 5, W0.AddHours(2).AddSeconds(2)));
    }
}
