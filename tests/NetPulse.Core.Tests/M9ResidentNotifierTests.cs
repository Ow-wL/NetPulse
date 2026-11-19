using NetPulse.Core.Presentation;
using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>M9 설계 A4: 창 상태별 알림 표, 알림 구간 30초 경계, 절전 공백, 설정, 측정 끝, 끌 수 없는 알림 [1-12].</summary>
public sealed class M9ResidentNotifierTests
{
    private readonly ResidentNotifier n = new();
    private long version;

    private IReadOnlyList<ResidentNotice> At(long ms, ObservationKind o, WindowPresence w = WindowPresence.Hidden, NoticePrefs? prefs = null,
        bool storage = false, string session = "m1", bool measuring = true, string? finished = null, EndReason? reason = null, string? startError = null)
        => n.Feed(new ResidentInput(++version, session, measuring, ms, o, storage, finished, reason, false, startError), w, prefs ?? NoticePrefs.Default);

    private static ResidentNotice[] Balloons(IEnumerable<ResidentNotice> x) => x.Where(i => i.Balloon).ToArray();
    private static ResidentNotice[] InApp(IEnumerable<ResidentNotice> x) => x.Where(i => i.InApp).ToArray();

    [Fact]
    public void HiddenSegmentStartGivesOneBalloonAndKindChangesInsideAreSilent()
    {
        Assert.Empty(At(0, ObservationKind.None));
        var start = At(1000, ObservationKind.ExternalNone);
        var b = Assert.Single(Balloons(start));
        Assert.Equal(NoticeKind.SegmentStart, b.Kind);
        Assert.Equal("외부 응답 없음 관측", b.Title);
        Assert.Empty(InApp(start));                                     // 숨김: 앱 안 알림 없음
        Assert.Empty(Balloons(At(2000, ObservationKind.LinkDown)));     // 같은 구간 안 종류 바뀜
        Assert.Empty(Balloons(At(3000, ObservationKind.None)));
        Assert.Empty(Balloons(At(3000 + 29_999, ObservationKind.LinkDown)));   // 29,999 ms 뒤 다시: 같은 구간
        Assert.Equal(1, n.HiddenSegmentStarts);
    }

    [Fact]
    public void ThirtySecondsWithoutObservationClosesTheSegment()
    {
        At(1000, ObservationKind.LinkDown);
        At(5000, ObservationKind.None);
        Assert.Empty(At(5000 + 29_999, ObservationKind.None));
        Assert.Empty(At(5000 + 30_000, ObservationKind.None));         // 닫힘 풍선은 기본 끔
        var again = Balloons(At(5000 + 30_001, ObservationKind.LinkDown));
        Assert.Equal(NoticeKind.SegmentStart, Assert.Single(again).Kind);
        Assert.Equal(2, n.HiddenSegmentStarts);
    }

    [Fact]
    public void RecoveredBalloonOnlyWhenEnabledAndSaysObservedSeconds()
    {
        var prefs = NoticePrefs.Default with { NotifyRecovered = true };
        At(10_000, ObservationKind.ExternalNone, prefs: prefs);
        At(12_000, ObservationKind.None, prefs: prefs);
        At(20_000, ObservationKind.ExternalNone, prefs: prefs);           // 같은 구간
        At(25_500, ObservationKind.None, prefs: prefs);
        Assert.Empty(At(25_500 + 29_999, ObservationKind.None, prefs: prefs));
        var end = Assert.Single(Balloons(At(25_500 + 30_000, ObservationKind.None, prefs: prefs)));
        Assert.Equal(NoticeKind.SegmentEnd, end.Kind);
        Assert.Contains("15초", end.Body);                               // 첫 관측 10,000 → 마지막 끝 25,500
    }

    [Fact]
    public void SleepGapLongerThanThirtySecondsStartsANewSegment()
    {
        At(1000, ObservationKind.LinkDown);
        At(2000, ObservationKind.None);
        // 절전 10초 공백 뒤 다시 관측: 같은 구간
        Assert.Empty(Balloons(At(12_000, ObservationKind.LinkDown)));
        At(13_000, ObservationKind.None);
        // 절전 40초 공백 (그동안 장면 없음): 새 구간
        Assert.Single(Balloons(At(53_000, ObservationKind.LinkDown)));
    }

    [Fact]
    public void WindowStateTable()
    {
        // 앞: 앱 안만
        var front = At(1000, ObservationKind.LinkDown, WindowPresence.Front);
        Assert.Single(InApp(front));
        Assert.Empty(Balloons(front));
        At(2000, ObservationKind.None, WindowPresence.Front);
        // 뒤 (다른 구간): 앱 안 + 풍선
        var back = At(40_000, ObservationKind.ExternalNone, WindowPresence.Back);
        Assert.Single(InApp(back));
        Assert.Single(Balloons(back));
        Assert.Equal(0, n.HiddenSegmentStarts);                          // 숨긴 동안만 센다
    }

    [Fact]
    public void WindowChangingMidSegmentDoesNotRenotify()
    {
        At(1000, ObservationKind.LinkDown, WindowPresence.Front);          // 앞에서 시작: 풍선 없음
        At(2000, ObservationKind.None, WindowPresence.Hidden);
        Assert.Empty(Balloons(At(10_000, ObservationKind.LinkDown, WindowPresence.Hidden)));   // 숨겨도 같은 구간
    }

    [Fact]
    public void DropoutSettingOffSuppressesSegmentBalloonsButNotStorageOrStartFailure()
    {
        var off = new NoticePrefs(false, true, false);
        Assert.Empty(Balloons(At(1000, ObservationKind.LinkDown, prefs: off)));
        var storage = Balloons(At(2000, ObservationKind.LinkDown, prefs: off, storage: true));
        Assert.Equal(NoticeKind.StorageFailing, Assert.Single(storage).Kind);
        Assert.Empty(Balloons(At(3000, ObservationKind.LinkDown, prefs: off, storage: true)));   // 시작에 한 번
        var failed = Balloons(At(0, ObservationKind.None, prefs: off, session: "m2", measuring: false, startError: "기록을 저장할 위치가 없습니다"));
        Assert.Equal(NoticeKind.StartFailed, Assert.Single(failed).Kind);
    }

    [Fact]
    public void FinishClosesTheOpenSegmentWithOneFinishedBalloon()
    {
        var prefs = NoticePrefs.Default with { NotifyRecovered = true };
        At(1000, ObservationKind.LinkDown, prefs: prefs);
        var end = At(2000, ObservationKind.LinkDown, prefs: prefs, measuring: false, finished: "m1", reason: EndReason.Completed);
        var b = Assert.Single(Balloons(end));
        Assert.Equal(NoticeKind.Finished, b.Kind);
        Assert.Equal("계획한 시간을 마쳤습니다.", b.Body);
        Assert.Empty(InApp(end));                                          // 끝나면 조용히 None (앱 안 끝 알림은 따로)
        Assert.Empty(At(40_000, ObservationKind.None, prefs: prefs, measuring: false, finished: "m1"));   // 닫힘 풍선 없음, 끝 풍선 반복 없음
    }

    [Fact]
    public void FinishedBalloonFollowsItsSetting()
    {
        At(1000, ObservationKind.None);
        Assert.Empty(At(2000, ObservationKind.None, prefs: new NoticePrefs(true, false, false), measuring: false, finished: "m1", reason: EndReason.Stopped));
    }

    [Fact]
    public void OldSnapshotsArriveLateAreIgnored()
    {
        n.Feed(new ResidentInput(10, "m1", true, 1000, ObservationKind.LinkDown, false, null, null, false, null), WindowPresence.Hidden, NoticePrefs.Default);
        Assert.Empty(n.Feed(new ResidentInput(9, "m1", true, 900, ObservationKind.None, false, null, null, false, null), WindowPresence.Hidden, NoticePrefs.Default));
        Assert.Equal(ObservationKind.LinkDown, n.Current);
    }

    [Fact]
    public void NewMeasurementStartsFresh()
    {
        At(1000, ObservationKind.LinkDown);
        At(2000, ObservationKind.None, measuring: false, finished: "m1", reason: EndReason.Stopped);
        Assert.Single(Balloons(At(500, ObservationKind.LinkDown, session: "m2")));   // 경과가 작아도 새 측정이면 새 구간
    }

    [Fact]
    public void HiddenSummaryIsTakenOnce()
    {
        At(1000, ObservationKind.LinkDown);
        At(2000, ObservationKind.None);
        At(40_000, ObservationKind.ExternalNone);
        Assert.Equal(2, n.TakeHiddenSummary());
        Assert.Equal(0, n.TakeHiddenSummary());
    }

    [Fact]
    public void BalloonTextHasNoTargetDetails()
    {
        var all = new List<ResidentNotice>();
        all.AddRange(At(1000, ObservationKind.LinkDown));
        all.AddRange(At(2000, ObservationKind.LinkDown, storage: true));
        Assert.All(all, x => Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", x.Title + x.Body));
    }
}
