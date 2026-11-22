using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

/// <summary>M12 설계 G2: 주사율 보정과 성능 프로필 (DECISIONS "M12 노트북 성능 기준").</summary>
public sealed class M12FrameContractTests
{
    [Theory]
    [InlineData(165, 16.7, 33)]            // 데스크톱: 지금 기준 그대로
    [InlineData(144, 16.7, 33)]
    [InlineData(60, 20.83, 41.67)]
    [InlineData(59.94, 20.85, 41.71)]
    [InlineData(59, 21.19, 42.37)]
    [InlineData(30, 41.67, 83.33)]
    public void IdleAndLongGapFollowTheRefreshInterval(double hz, double idle, double longGap)
    {
        Assert.Equal(idle, Math.Round(FrameContract.IdleP95(hz), 2));
        Assert.Equal(longGap, Math.Round(FrameContract.LongGap(hz), 2));
    }

    [Fact]
    public void BoundaryWhereTheRefreshTermTakesOver()
    {
        // 1.25T = 16.7 ↔ T = 13.36 ms (약 74.85 Hz), 2.5T = 33 ↔ T = 13.2 ms (약 75.76 Hz)
        Assert.Equal(FrameBudget.IdleP95, FrameContract.IdleP95(75));
        Assert.True(FrameContract.IdleP95(74) > FrameBudget.IdleP95);
        Assert.Equal(FrameBudget.Hitch, FrameContract.LongGap(76));
        Assert.True(FrameContract.LongGap(75) > FrameBudget.Hitch);
    }

    [Fact]
    public void StandardKeepsTheCurrentBudgets()
    {
        var s = FrameContract.For(PerfProfile.Standard, 165);
        Assert.Equal((50.0, 2), (s.TransitionMax, s.TransitionLongGaps));
        Assert.Equal((80.0, 6), (s.WorstOpenMax, s.WorstOpenLongGaps));
        Assert.Equal((80.0, 2), (s.WorstScrollMax, s.WorstScrollLongGaps));
        Assert.Equal((50.0, 50.0, 2), (s.NoticeFirstLayout, s.NoticeMax, s.NoticeLongGaps));
        Assert.Equal(FrameContract.Version, s.Version);
    }

    [Fact]
    public void LowRaisesOnlyTheAgreedCaps()
    {
        var l = FrameContract.For(PerfProfile.Low, 60);
        Assert.Equal((100.0, 2), (l.TransitionMax, l.TransitionLongGaps));
        Assert.Equal((100.0, 6), (l.WorstOpenMax, l.WorstOpenLongGaps));
        Assert.Equal((80.0, 2), (l.WorstScrollMax, l.WorstScrollLongGaps));   // 최악 스크롤은 늘리지 않는다
        Assert.Equal((100.0, 100.0, 2), (l.NoticeFirstLayout, l.NoticeMax, l.NoticeLongGaps));
        // 공통 보정은 프로필과 무관
        Assert.Equal(FrameContract.For(PerfProfile.Standard, 60).IdleP95, l.IdleP95);
        Assert.Equal(FrameContract.For(PerfProfile.Standard, 60).LongGapMs, l.LongGapMs);
    }

    [Theory]
    [InlineData(null, PerfProfile.Standard)]
    [InlineData("", PerfProfile.Standard)]
    [InlineData("standard", PerfProfile.Standard)]
    [InlineData(" LOW ", PerfProfile.Low)]
    public void ProfileNames(string? name, PerfProfile expected) => Assert.Equal(expected, FrameContract.Parse(name));

    [Fact]
    public void UnknownProfileAndBadRefreshAreRejected()
    {
        Assert.Throws<ArgumentException>(() => FrameContract.Parse("laptop"));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameContract.IdleP95(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameContract.LongGap(-1));
    }
}
