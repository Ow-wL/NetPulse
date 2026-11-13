using System.Text.Json.Nodes;
using NetPulse.Core.Evidence;
using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>링크 근거 모델 (M4 설계 A4~A6).</summary>
public sealed class EvidenceTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(LinkCapability.Supported, false, 1.0, LinkDisplay.ObservedNone, false)]
    [InlineData(LinkCapability.Supported, false, 0.5, LinkDisplay.Unsupported, true)]    // 부분 수집은 정상이 아님
    [InlineData(LinkCapability.Supported, true, 0.5, LinkDisplay.Down, true)]             // 다운 + 부분: 다운이 앞서고 부분은 보조
    [InlineData(LinkCapability.Denied, false, 0.0, LinkDisplay.Denied, true)]             // 거부와 범위 0: 거부가 앞섬
    [InlineData(LinkCapability.Denied, true, null, LinkDisplay.Down, false)]
    [InlineData(LinkCapability.Unsupported, false, null, LinkDisplay.Unsupported, false)]
    [InlineData(LinkCapability.Supported, false, null, LinkDisplay.ObservedNone, false)]  // 로그 경로 (범위 없음)
    public void DisplayPrecedence(LinkCapability cap, bool down, double? coverage, LinkDisplay expected, bool partial)
    {
        var (display, isPartial) = LinkDisplayRule.Decide(cap, down, coverage);
        Assert.Equal(expected, display);
        Assert.Equal(partial, isPartial);
    }

    private static RoundData R(long round, long atMs, DateTimeOffset wall) => new(round, round * 1000, atMs, wall, 0, false, []);

    [Fact]
    public void TimeMappingInsideOneSegment()
    {
        var rounds = new[] { R(0, 0, W0), R(1, 1000, W0.AddSeconds(1)), R(2, 2000, W0.AddSeconds(2)) };
        Assert.Equal(1500, TimeMapping.ToElapsedMs(rounds, W0.AddMilliseconds(1500)));
        Assert.Null(TimeMapping.ToElapsedMs(rounds, W0.AddSeconds(-2)));          // 측정 시작 전: 환산하지 않음 (G4 1라운드 3)
        Assert.Null(TimeMapping.ToElapsedMs(rounds, W0.AddSeconds(4)));           // 끝을 모르면 마지막 회차 뒤는 밖
        Assert.Equal(4000, TimeMapping.ToElapsedMs(rounds, W0.AddSeconds(4), endMs: 5000));
        Assert.Null(TimeMapping.ToElapsedMs(rounds, W0.AddSeconds(6), endMs: 5000));
    }

    [Fact]
    public void TimeMappingRefusesSuspendAndClockChangeWindows()
    {
        // 회차 1과 2 사이에 절전(단조 시계 멈춤, 벽시계 1시간): 그 사이 로그는 환산하지 않음
        var suspend = new[] { R(0, 0, W0), R(1, 1000, W0.AddSeconds(1)), R(2, 2000, W0.AddHours(1)), R(3, 3000, W0.AddHours(1).AddSeconds(1)) };
        Assert.Null(TimeMapping.ToElapsedMs(suspend, W0.AddMinutes(30)));
        Assert.Equal(500, TimeMapping.ToElapsedMs(suspend, W0.AddMilliseconds(500)));
        Assert.Equal(2500, TimeMapping.ToElapsedMs(suspend, W0.AddHours(1).AddMilliseconds(500)));

        // 시계를 10초 되돌림 (G4 1라운드 2의 반례): (0,0),(1,1),(2,-8),…,(12,2) [경과초, 벽초]
        var back = Enumerable.Range(0, 13).Select(k => R(k, k * 1000L, W0.AddSeconds(k <= 1 ? k : k - 10))).ToArray();
        Assert.Null(TimeMapping.ToElapsedMs(back, W0.AddMilliseconds(1500)));   // 변경 전 1.5초 또는 변경 사이 → 불확실
        Assert.Null(TimeMapping.ToElapsedMs(back, W0.AddMilliseconds(500)));    // 변경 전 0.5초 또는 변경 뒤 10.5초 → 두 대응
        Assert.Equal(2500, TimeMapping.ToElapsedMs(back, W0.AddSeconds(-7.5)));  // 변경 뒤에만 가능
        // 측정이 짧아 변경 뒤 대응이 측정 밖이면 하나로 정해진다
        var shortBack = new[] { R(0, 0, W0), R(1, 1000, W0.AddSeconds(1)), R(2, 2000, W0.AddSeconds(-8)), R(3, 3000, W0.AddSeconds(-7)) };
        Assert.Equal(600, TimeMapping.ToElapsedMs(shortBack, W0.AddMilliseconds(600)));
    }

    private static JsonObject Link(string collection, string? adapter = "a1", long? eventMs = null, JsonObject? extra = null, string source = "direct")
    {
        var o = new JsonObject { ["id"] = "x", ["type"] = "link", ["at"] = eventMs ?? 0, ["wall"] = RecordSchema.Iso(W0), ["source"] = source, ["collection"] = collection };
        if (adapter is not null) { o["adapter"] = adapter; o["interfaceIndex"] = 7; o["kind"] = "wired"; o["virtual"] = false; }
        if (eventMs is long ms) o["eventMs"] = ms;
        if (extra is not null) foreach (var (k, v) in extra.ToArray()) { extra.Remove(k); o[k] = v; }
        return o;
    }

    private static JsonObject Summary(string adapter, double? coverage, long? lastFirst = null, long? lastLast = null, string source = "direct", string cap = "supported")
    {
        var extra = new JsonObject { ["capability"] = cap };
        if (coverage is double c) extra["coverage"] = c;
        if (lastFirst is long f) { extra["lastState"] = "x"; extra["lastFirstMs"] = f; extra["lastLastMs"] = lastLast; }
        return Link("summary", adapter, null, extra, source);
    }

    [Fact]
    public void DownSpanEndsAtNextUpOrLastDownObservation()
    {
        var events = new[]
        {
            Link("up", eventMs: 0),
            Link("down", eventMs: 3000, extra: new JsonObject { ["prevState"] = "up", ["prevFirstMs"] = 0, ["prevLastMs"] = 2000 }),
            Link("up", eventMs: 6000, extra: new JsonObject { ["prevState"] = "down", ["prevFirstMs"] = 3000, ["prevLastMs"] = 5000 }),
            // 0초 down → 1초 down → 2초 down → 3초 unknown: 확정 다운은 마지막 다운 관측까지 (2-2)
            Link("down", eventMs: 10_000, extra: new JsonObject { ["prevState"] = "up", ["prevFirstMs"] = 6000, ["prevLastMs"] = 9000 }),
            Link("unknown", eventMs: 13_000, extra: new JsonObject { ["prevState"] = "down", ["prevFirstMs"] = 10_000, ["prevLastMs"] = 12_000 }),
            Summary("a1", 0.95),
        };
        var ev = LinkEvidenceBuilder.FromEvents(events);
        Assert.Equal([(3000L, 6000L), (10_000L, 12_000L)], ev.Downs.Select(d => (d.FromMs, d.ToMs)));
        Assert.All(ev.Downs, d => Assert.True(d.Physical));
        var s = Assert.Single(ev.Summaries);
        Assert.Equal(LinkDisplay.Down, s.Display);
    }

    [Fact]
    public void FinalDownUsesEndSummaryLastObservation()
    {
        var events = new[] { Link("down", eventMs: 1000), Summary("a1", 1.0, lastFirst: 1000, lastLast: 4000) };
        var d = Assert.Single(LinkEvidenceBuilder.FromEvents(events).Downs);
        Assert.Equal((1000L, 4000L), (d.FromMs, d.ToMs));   // 측정 끝까지 늘리지 않음
    }

    [Fact]
    public void MissingSummaryMeansInterrupted()
    {
        var ev = LinkEvidenceBuilder.FromEvents([Link("up", eventMs: 0)]);
        var s = Assert.Single(ev.Summaries);
        Assert.Equal(LinkDisplay.Unsupported, s.Display);
        Assert.Equal("interrupted", s.Code);
        Assert.False(ev.HasEvidence);
    }

    [Fact]
    public void UnlinkedAmbiguousHistoricalAndUnmappedAreNotUsed()
    {
        var events = new[]
        {
            Link("down", null, 1000, new JsonObject { ["link"] = "unlinked" }, "log"),
            Link("down", "a1", 1000, new JsonObject { ["link"] = "ambiguous" }, "log"),
            Link("down", "a1", 1000, new JsonObject { ["link"] = "historical" }, "log"),
            Link("down", "a1", null, new JsonObject { ["link"] = "linked" }, "log"),   // 시각 환산 안 됨
            Summary("a1", null, source: "log"),
        };
        var ev = LinkEvidenceBuilder.FromEvents(events);
        Assert.Empty(ev.Downs);
        Assert.Equal(2, ev.Unlinked);
        Assert.Equal(1, ev.Unmapped);
        Assert.Equal(LinkDisplay.ObservedNone, Assert.Single(ev.Summaries).Display);
    }

    [Fact]
    public void LogDownAndUpMakeSpanAndVirtualIsNotPhysical()
    {
        var events = new[]
        {
            Link("down", "a1", 2000, new JsonObject { ["link"] = "linked" }, "log"),
            Link("up", "a1", 2500, new JsonObject { ["link"] = "linked" }, "log"),
            Summary("a1", null, source: "log"),
        };
        var d = Assert.Single(LinkEvidenceBuilder.FromEvents(events).Downs);
        Assert.Equal((2000L, 2500L, LinkSource.Log), (d.FromMs, d.ToMs, d.Source));

        var tunnel = Link("down", eventMs: 100);
        tunnel["kind"] = "tunnel";
        tunnel["virtual"] = true;
        Assert.False(Assert.Single(LinkEvidenceBuilder.FromEvents([tunnel, Summary("a1", 1)]).Downs).Physical);
    }

    [Fact]
    public void PathWideFailureWithoutAdapter()
    {
        var ev = LinkEvidenceBuilder.FromEvents([Link("unsupported", null, extra: new JsonObject { ["code"] = "no-interface" })]);
        var s = Assert.Single(ev.Summaries);
        Assert.Equal(("*", LinkDisplay.Unsupported, "no-interface"), (s.Adapter, s.Display, s.Code));
        Assert.Equal(LinkDisplay.Denied, LinkEvidenceBuilder.FromEvents([Link("denied", null, source: "log")]).Summaries[0].Display);
    }
}
