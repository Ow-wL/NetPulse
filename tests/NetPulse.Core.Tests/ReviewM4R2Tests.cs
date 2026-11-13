using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Analysis;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M4 G4 2라운드 지적을 막는 시험 (`.loop/reviews/m4-g4-r2-answer.md`).</summary>
public sealed class ReviewM4R2Tests : IDisposable
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private static JsonObject Direct(string collection, long eventMs, int index = 7, string alias = "a1", JsonObject? extra = null)
    {
        var o = new JsonObject { ["id"] = "m", ["type"] = "link", ["at"] = eventMs, ["wall"] = RecordSchema.Iso(W0), ["source"] = "direct", ["adapter"] = alias,
            ["interfaceIndex"] = index, ["kind"] = "wired", ["virtual"] = false, ["collection"] = collection, ["eventMs"] = eventMs };
        if (extra is not null) foreach (var (k, v) in extra.ToArray()) { extra.Remove(k); o[k] = v; }
        return o;
    }

    private static JsonObject Summary(string alias = "a1", int index = 7, string source = "direct") => new()
    {
        ["id"] = "m", ["type"] = "link", ["at"] = 9000, ["wall"] = RecordSchema.Iso(W0), ["source"] = source, ["adapter"] = alias, ["interfaceIndex"] = index,
        ["kind"] = "wired", ["virtual"] = false, ["collection"] = "summary", ["capability"] = "supported", ["coverage"] = 1.0,
    };

    // 1: 넘침이 업 표본을 삼키면 확정 다운은 넘침 전 마지막 다운 관측까지만
    [Fact]
    public void OverflowCutsConfirmedDown()
    {
        var events = new[]
        {
            Direct("up", 0),
            Direct("down", 1000, extra: new JsonObject { ["prevState"] = "up", ["prevFirstMs"] = 0, ["prevLastMs"] = 0 }),
            // 2초 업 알림은 넘쳐서 버려짐 → 다음으로 남은 업 표본은 6초 (앞 다운의 마지막 관측 1초)
            Direct("up", 6000, extra: new JsonObject { ["prevState"] = "down", ["prevFirstMs"] = 1000, ["prevLastMs"] = 1000 }),
            new JsonObject { ["id"] = "m", ["type"] = "link", ["at"] = 9000, ["wall"] = RecordSchema.Iso(W0), ["source"] = "direct", ["collection"] = "overflow",
                ["count"] = 1, ["spans"] = new JsonArray(new JsonArray(2000, 2000)) },
            Summary(),
        };
        var ev = LinkEvidenceBuilder.FromEvents(events);
        var d = Assert.Single(ev.Downs);
        Assert.Equal((1000L, 1000L), (d.FromMs, d.ToMs));   // 1~6초 전체가 아니다
    }

    // 3라운드 1: 넘침 뒤 같은 다운을 다시 봐도 넘침 전 구간과 잇지 않는다
    [Fact]
    public void ReobservedDownAfterOverflowIsSeparate()
    {
        var events = new[]
        {
            Direct("up", 0),
            Direct("down", 1000, extra: new JsonObject { ["prevState"] = "up", ["prevFirstMs"] = 0, ["prevLastMs"] = 0 }),
            // 2초 업 표본 유실(넘침) → 3초 다운 재관측: 세션은 넘침을 건넌 줄을 따로 쓴다 (앞 다운의 마지막 관측 1초)
            Direct("down", 3000, extra: new JsonObject { ["prevState"] = "down", ["prevFirstMs"] = 1000, ["prevLastMs"] = 1000 }),
            Direct("up", 6000, extra: new JsonObject { ["prevState"] = "down", ["prevFirstMs"] = 3000, ["prevLastMs"] = 3000 }),
            new JsonObject { ["id"] = "m", ["type"] = "link", ["at"] = 9000, ["wall"] = RecordSchema.Iso(W0), ["source"] = "direct", ["collection"] = "overflow",
                ["count"] = 1, ["spans"] = new JsonArray(new JsonArray(2000, 2000)) },
            Summary(),
        };
        var downs = LinkEvidenceBuilder.FromEvents(events).Downs.Select(d => (d.FromMs, d.ToMs)).ToArray();
        Assert.Equal([(1000L, 1000L), (3000L, 6000L)], downs);   // 2초는 확정 다운이 아니다
    }

    [Fact]
    public void SummaryLineDoesNotReopenRemovedIndex()
    {
        var events = new List<JsonObject> { Direct("up", 0, 7, "a1"), Direct("removed", 3000, 7, "a1"), Summary("a1"), Direct("up", 7000, 7, "a2"), Summary("a2") };
        var ev = LinkEvidenceBuilder.FromEvents(events);
        Assert.Equal("a2", ev.AdapterFor(7, 9500));   // 끝 요약이 제거된 a1의 번호 7을 다시 열지 않는다 (충돌 없음)
    }

    // 3: 로그 줄의 (끝 시점) 번호가 과거 번호 이력을 오염시키지 않는다
    [Fact]
    public void LogLinesDoNotPolluteIndexHistory()
    {
        var events = new List<JsonObject>
        {
            Direct("up", 0, 7),
            Direct("down", 1900, 7),
            Direct("up", 4100, 9),   // 재활성화로 번호 9
            new() { ["id"] = "m", ["type"] = "link", ["at"] = 9000, ["wall"] = RecordSchema.Iso(W0), ["source"] = "log", ["adapter"] = "a1", ["interfaceIndex"] = 9,
                ["kind"] = "wired", ["virtual"] = false, ["collection"] = "down", ["link"] = "linked", ["eventMs"] = 1900 },
            Summary(index: 9),
        };
        var ev = LinkEvidenceBuilder.FromEvents(events);
        Assert.Equal("a1", ev.AdapterFor(7, 2000));
        Assert.Equal("a1", ev.AdapterFor(9, 5000));
        Assert.Null(ev.AdapterFor(9, 2000));
    }

    [Fact]
    public void RemovalClosesIndexAndReuseIsNotConfused()
    {
        var events = new List<JsonObject>
        {
            Direct("up", 0, 7, "a1"),
            Direct("removed", 3000, 7, "a1"),
            Direct("up", 7000, 7, "a2"),   // 다른 어댑터가 번호 7을 다시 씀
            Summary("a2"),
        };
        var ev = LinkEvidenceBuilder.FromEvents(events);
        Assert.Equal("a1", ev.AdapterFor(7, 1000));
        Assert.Null(ev.AdapterFor(7, 4000));        // 제거 뒤 · 재사용 전 (2초 넘게 이름)
        Assert.Equal("a2", ev.AdapterFor(7, 8000));
    }

    // 5: 계획 경계에서 없는 회차를 세지 않는다, 끝 시각에 막 예정된 회차도
    [Theory]
    [InlineData(600_000L, 600L)]
    [InlineData(80_000L, 80L)]
    [InlineData(79_500L, 80L)]
    [InlineData(700_000L, 600L)]
    public void ExpectedRoundsBoundary(long endMs, long expected)
    {
        var header = new RecordHeader("m", 0, "v", W0, 0,
            [new Target("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "A사", ProbeMethod.Icmp),
             new Target("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "B사", ProbeMethod.Icmp)], 1000, 800, 600_000, "primary", ExpectedConnection.Any);
        var rounds = Enumerable.Range(0, 10).Select(i => new RoundData(i, i * 1000, i * 1000, W0.AddSeconds(i), 0, false,
            [new TargetResult("a", ProbeOutcome.Success, 10, null), new TargetResult("b", ProbeOutcome.Success, 10, null)], i * 1000 + 50)).ToArray();
        var summary = new RecordSummary("m", EndReason.Stopped, W0, 10, 10, 0, 0, 0, 9, false, null, 0, endMs);
        Assert.Equal(expected, MeasurementAnalysis.Analyze(header, rounds, [], [], summary).Coverage.ExpectedRounds);
    }

    // 6: 지연 상승 비율도 판정 가능한 회차 집합에서
    [Fact]
    public void LatencyShareUsesJudgeableRoundsOnly()
    {
        var header = new RecordHeader("m", 0, "v", W0, 0,
            [new Target("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "A사", ProbeMethod.Icmp),
             new Target("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "B사", ProbeMethod.Icmp)], 1000, 800, 600_000, "primary", ExpectedConnection.Any);
        // 앞 20회: B 검사 오류, A 지연 상승 (기준 10 → 100). 뒤 80회 모두 정상
        var rounds = Enumerable.Range(0, 100).Select(i => new RoundData(i, i * 1000, i * 1000, W0.AddSeconds(i), 0, false,
            i < 20
                ? [new TargetResult("a", ProbeOutcome.Success, 100, null), new TargetResult("b", ProbeOutcome.Error, null, "E")]
                : [new TargetResult("a", ProbeOutcome.Success, 10, null), new TargetResult("b", ProbeOutcome.Success, 10, null)], i * 1000 + 50)).ToArray();
        var a = MeasurementAnalysis.Analyze(header, rounds, [], [], new RecordSummary("m", EndReason.Stopped, W0, 100, 100, 0, 0, 0, 99, false, null, 0, 99_500));
        Assert.Equal(0, a.Coverage.LatencyHighRounds);
        Assert.NotEqual(Grade.Bad, a.Grade.Grade);
    }

    private sealed class FakeRoutes : IRouteObserver
    {
        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round) =>
            targets.Select(t => new RouteSample(t.Id, 7, "wired", true, null)).ToArray();
    }

    private sealed class FakeLinks : ILinkObserver
    {
        public static readonly AdapterInfo Info = new("{K}", 7, "wired", false);
        private Action<LinkSample>? sink;
        private bool watching;
        public (LinkCapability Capability, string? Code) Start(Action<LinkSample> sink) { this.sink = sink; return (LinkCapability.Supported, null); }
        public void Watch(IReadOnlyCollection<int> interfaceIndexes) => watching = true;
        public void Poll() { if (watching) sink?.Invoke(new LinkSample(Info, LinkState.Up, true)); }
        public IReadOnlyList<AdapterInfo> Adapters => watching ? [Info] : [];
        public void Dispose() { }
    }

    private sealed class RecordingLog(IReadOnlyList<LogLinkEvent> events) : ILinkLogSource
    {
        public DateTimeOffset From, To;
        public LinkLogResult Read(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<AdapterInfo> adapters, TimeSpan timeout)
        {
            From = fromUtc;
            To = toUtc;
            return new LinkLogResult(new Dictionary<string, (LinkCapability, string?)> { [FakeLinks.Info.Key] = (LinkCapability.Supported, null) },
                events, 0, LinkCapability.Supported, null);
        }
    }

    private static readonly Profile Profile2 = new("p", "시험",
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Any);

    // 2: 시계를 되돌린 뒤의 측정 중 로그는 버리지 않고 환산한다 (조회 범위도 측정 중 본 벽시계 전체)
    [Fact]
    public async Task LogAfterClockRollbackIsMappedNotHistorical()
    {
        var clock = new ManualClock(Start);
        var log = new RecordingLog([new LogLinkEvent(FakeLinks.Info.Key, LinkMatch.Linked, LinkState.Down, Start.AddSeconds(-7.5))]);
        var s = MeasurementSession.Start(new SessionOptions
        {
            Profile = Profile2, Clock = clock, ProbeFor = _ => new RecordingProbe(), Routes = new FakeRoutes(), Links = new FakeLinks(), LinkLog = log,
            LinkLogSettle = TimeSpan.Zero, NewId = _ => "rollback", Primary = new DiskRecordRoot(dir.Sub("p"), "primary"), Fallback = new DiskRecordRoot(dir.Sub("f"), "fallback"),
        });
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));   // 회차 1 (벽 +1s)
        await Eventually(() => s.Status.Observed == 2, "관측 2");
        clock.ShiftWall(TimeSpan.FromSeconds(-10));                // 시계를 10초 되돌림
        for (int i = 0; i < 10; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));   // 회차 2~11 (벽 -8s ~ +1s)
        await Eventually(() => s.Status.Observed == 12, "관측 12");
        await s.StopAsync();

        Assert.True(log.From <= Start.AddSeconds(-8), "조회 범위가 되돌린 벽시계를 덮어야 한다");
        var rec = RecordReader.Read(RecordReader.FindSegments("rollback", [dir.Sub("p")]));
        var line = Assert.Single(rec.Events, e => (string?)e["type"] == "link" && (string?)e["source"] == "log" && (string?)e["collection"] == "down");
        Assert.Equal("linked", (string?)line["link"]);
        Assert.Equal(2500, (long)line["eventMs"]!);
    }

    // 4: 요약 endMs는 정리(배출)를 기다린 시간을 넣지 않는다
    [Fact]
    public async Task SummaryEndIsFixedBeforeDraining()
    {
        var clock = new ManualClock(Start);
        var release = new ManualResetEventSlim(false);
        bool block = false;
        var primary = new FaultyRoot(new DiskRecordRoot(dir.Sub("p2"), "primary"), "VOL-A")
        {
            DelayFor = name => { if (Volatile.Read(ref block) && name.StartsWith("rounds", StringComparison.Ordinal)) release.Wait(TimeSpan.FromSeconds(10)); return TimeSpan.Zero; },
        };
        var s = MeasurementSession.Start(new SessionOptions
        {
            Profile = Profile2, Clock = clock, ProbeFor = _ => new RecordingProbe(), NewId = _ => "endfix",
            Primary = primary, Fallback = new FaultyRoot(new DiskRecordRoot(dir.Sub("f2"), "fallback"), "VOL-B"),
        });
        for (int i = 0; i < 3; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        await Eventually(() => s.Status.Committed == 4, "확정 4");
        Volatile.Write(ref block, true);
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));                       // 회차 4: 쓰기에서 막힘
        await Eventually(() => primary.Writes("rounds") == 5, "회차 4 쓰기 진입");      // 배출할 것이 실제로 남아 있다 (G4 3라운드 4)
        clock.Advance(TimeSpan.FromMilliseconds(500));                                  // 4.5초에 중지
        var stopping = s.StopAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(stopping.IsCompleted, "배출이 막혀 있는 동안 종료가 끝나면 안 된다");
        clock.Advance(TimeSpan.FromSeconds(10));                                         // 정리하는 동안 시간이 흐름
        Volatile.Write(ref block, false);
        release.Set();
        var result = await stopping;
        Assert.Equal(4500, result.Summary!.EndMs);
    }
}
