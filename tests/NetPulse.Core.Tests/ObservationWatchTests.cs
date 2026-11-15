using NetPulse.Core.Evidence;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;

namespace NetPulse.Core.Tests;

/// <summary>관측 종류 전환 (M5 이월 3, M6 설계 6.3): 지금 장면에서 계산한 종류, 종류마다 따로 알림.</summary>
public sealed class ObservationWatchTests
{
    private static HostSnapshot Scene(HostState state, bool externalNone, bool linkDown) =>
        new(1, state, null, "s", null, 0, 0, [], [],
            [], linkDown ? [new LinkDownView("a", LinkSource.Direct, 0, 0, Ongoing: true)] : [],
            externalNone ? [new TimelineBand(BandKind.ExternalNone, 0, 0, 0, 0, 1, 0, null, OpenEnd: true, OpenStart: false)] : [],
            null, [], null, null, PathMapModel.Idle([], null), null, null, null, null);

    [Fact]
    public void ExternalNoneThenLinkDownGivesANewAlert()
    {
        var w = new ObservationWatch();
        Assert.Null(w.Update(Scene(HostState.Measuring, false, false)));
        var first = w.Update(Scene(HostState.Measuring, true, false));
        Assert.Equal((ObservationKind.None, ObservationKind.ExternalNone), (first!.From, first.To));
        // 외부 응답 없음이 이어지는 중에 링크가 끊김 → 새 종류의 전환 (전에는 "이상 있음"이 그대로라 알림이 없었다)
        var second = w.Update(Scene(HostState.Measuring, true, true));
        Assert.Equal((ObservationKind.ExternalNone, ObservationKind.LinkDown), (second!.From, second.To));
        Assert.Equal("링크 끊김 관측", ObservationWatch.Message(second).Title);
        Assert.Equal(ObservationKind.LinkDown, w.Current);   // 알약은 이 장면의 종류로 그린다
    }

    [Fact]
    public void EndingWhileMeasuringSaysWhichObservationEnded()
    {
        var w = new ObservationWatch();
        w.Update(Scene(HostState.Measuring, false, true));
        var end = w.Update(Scene(HostState.Measuring, false, false));
        var (title, body, warning) = ObservationWatch.Message(end!);
        Assert.Equal("관측이 끝났습니다", title);
        Assert.Contains("링크 끊김", body);
        Assert.False(warning);
    }

    [Fact]
    public void StoppingTheMeasurementClearsQuietly()
    {
        var w = new ObservationWatch();
        w.Update(Scene(HostState.Measuring, true, false));
        Assert.Null(w.Update(Scene(HostState.Idle, true, false)));   // 측정이 끝나면 알림 없이 None
        Assert.Equal(ObservationKind.None, w.Current);
    }

    [Fact]
    public void SameKindDoesNotRepeat()
    {
        var w = new ObservationWatch();
        Assert.NotNull(w.Update(Scene(HostState.Measuring, true, false)));
        Assert.Null(w.Update(Scene(HostState.Measuring, true, false)));
    }
}
