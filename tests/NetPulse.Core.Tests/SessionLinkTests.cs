using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>세션의 링크 근거 흐름: 표본 → 파일 → 다시 읽기 (M4 설계 A5, 10절).</summary>
public sealed class SessionLinkTests : IDisposable
{
    private const string Id = "20261113T000000Z_link01";
    private readonly TempDir dir = new();
    private readonly FaultyRoot primary;
    private readonly FaultyRoot fallback;

    public SessionLinkTests()
    {
        primary = new FaultyRoot(new DiskRecordRoot(dir.Sub("p"), "primary"), "VOL-A");
        fallback = new FaultyRoot(new DiskRecordRoot(dir.Sub("f"), "fallback"), "VOL-A");
    }

    public void Dispose() => dir.Dispose();

    private static readonly Profile OneMinute = new("p", "시험",
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Wired);

    private sealed class FakeRoutes : IRouteObserver
    {
        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round) =>
            targets.Select(t => new RouteSample(t.Id, 7, round == 0 ? "wired" : null, true, null)).ToArray();
    }

    private sealed class FakeLinks : ILinkObserver
    {
        public static readonly AdapterInfo Info = new("{KEY-1}", 7, "wired", false);
        private readonly HashSet<int> watched = [];
        private Action<LinkSample>? sink;
        private int polls;
        public Func<int, LinkState> StateAt = _ => LinkState.Up;
        public (LinkCapability, string?) StartResult = (LinkCapability.Supported, null);
        public int ExtraNotifyPerPoll;
        public bool Disposed;

        public (LinkCapability Capability, string? Code) Start(Action<LinkSample> sink) { this.sink = sink; return StartResult; }
        public void Watch(IReadOnlyCollection<int> interfaceIndexes) { lock (watched) foreach (var i in interfaceIndexes) watched.Add(i); }
        public void Poll()
        {
            lock (watched) if (watched.Count == 0) return;
            var state = StateAt(polls++);
            sink?.Invoke(new LinkSample(Info, state, true));
            for (int i = 0; i < ExtraNotifyPerPoll; i++) sink?.Invoke(new LinkSample(Info, i % 2 == 0 ? LinkState.Down : LinkState.Up, false));
        }
        public void Notify(LinkState s) => sink?.Invoke(new LinkSample(Info, s, false));
        public IReadOnlyList<AdapterInfo> Adapters { get { lock (watched) return watched.Count == 0 ? [] : [Info]; } }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeLog(Func<DateTimeOffset, DateTimeOffset, LinkLogResult> read) : ILinkLogSource
    {
        public LinkLogResult Read(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<AdapterInfo> adapters, TimeSpan timeout) => read(fromUtc, toUtc);
    }

    private SessionOptions Options(IClock clock, ILinkObserver? links, ILinkLogSource? log = null, bool routes = true) => new()
    {
        Profile = OneMinute, Clock = clock, ProbeFor = _ => new RecordingProbe(), Primary = primary, Fallback = fallback, NewId = _ => Id,
        Routes = routes ? new FakeRoutes() : null, Links = links, LinkLog = log, LinkLogSettle = TimeSpan.Zero, RetryTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static async Task Rounds(ManualClock clock, MeasurementSession s, int count)
    {
        for (int i = 0; i < count; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        long target = (long)clock.Elapsed.TotalSeconds;
        await Eventually(() => s.Engine.State.Latest?.Tick.Index >= target, $"엔진 회차 {target}");
        await Eventually(() => s.Status.Observed >= target + 1, "관측");
    }

    private MeasurementRecord Reread() => RecordReader.Read(RecordReader.FindSegments(Id, [primary.Path!, fallback.Path!]));

    private static JsonObject[] Links(MeasurementRecord r) => r.Events.Where(e => (string?)e["type"] == "link").ToArray();

    [Fact]
    public async Task DirectSamplesSurviveFilesWithTransitionsAndCoverage()
    {
        var clock = new ManualClock(Start);
        var links = new FakeLinks { StateAt = p => p is >= 3 and <= 5 ? LinkState.Down : LinkState.Up };
        var s = MeasurementSession.Start(Options(clock, links));
        await Rounds(clock, s, 8);
        await s.StopAsync();
        Assert.True(links.Disposed);

        var rec = Reread();
        Assert.Equal(ExpectedConnection.Wired, rec.Header!.Expected);
        var ev = LinkEvidenceBuilder.FromEvents(rec.Events);
        var down = Assert.Single(ev.Downs);
        Assert.Equal((3000L, 6000L, true), (down.FromMs, down.ToMs, down.Physical));   // 다운 처음 관측 → 다음 업 처음 관측
        var direct = ev.Summaries.Single(x => x.Source == LinkSource.Direct);
        Assert.Equal((LinkDisplay.Down, 1.0), (direct.Display, direct.Coverage!.Value));
        Assert.Equal("a1", direct.Adapter);
        // 같은 상태 연속은 줄로 남기지 않는다: up, down, up 세 줄 + 요약
        Assert.Equal(["up", "down", "up", "summary"], Links(rec).Where(e => (string?)e["source"] == "direct").Select(e => (string)e["collection"]!));
        Assert.DoesNotContain(Links(rec), e => e.ToJsonString().Contains("KEY-1"));   // 어댑터 Key(GUID)는 기록하지 않는다
    }

    [Fact]
    public async Task DownThenUnknownEndsAtLastDownAndLowersCoverage()
    {
        var clock = new ManualClock(Start);
        var links = new FakeLinks { StateAt = p => p < 3 ? LinkState.Up : p <= 5 ? LinkState.Down : LinkState.Unknown };
        var s = MeasurementSession.Start(Options(clock, links));
        await Rounds(clock, s, 8);
        await s.StopAsync();
        var ev = LinkEvidenceBuilder.FromEvents(Reread().Events);
        Assert.Equal((3000L, 5000L), (ev.Downs[0].FromMs, ev.Downs[0].ToMs));   // 측정 끝까지 늘리지 않음 (2-2)
        var direct = ev.Summaries.Single(x => x.Source == LinkSource.Direct);
        Assert.Equal(LinkDisplay.Down, direct.Display);
        Assert.True(direct.Partial);   // 9회 중 6회만 판별 → 부분 (보조 사유)
        Assert.Contains("partial", direct.Secondary);
    }

    [Fact]
    public async Task OverflowIsRecorded()
    {
        var clock = new ManualClock(Start);
        var links = new FakeLinks { ExtraNotifyPerPoll = 3000 };
        var s = MeasurementSession.Start(Options(clock, links));
        // M5 (M4 이월 3): 회차마다 알림 3000개 중 큐(1024)에 들어간 것은 모두 상태 변화라 줄마다 디스크까지 내린다.
        // 느린 디스크에서는 처리가 몇 초 걸릴 수 있으므로 고정 5초가 아니라 처리 완료(관측 수)를 기다린다
        for (int i = 0; i < 2; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        await EventuallyWithin(() => s.Status.Observed >= 3, "넘침 회차 처리", TimeSpan.FromSeconds(60));
        await s.StopAsync();
        var rec = Reread();
        var overflow = Assert.Single(Links(rec), e => (string?)e["collection"] == "overflow");
        Assert.True((long)overflow["count"]! > 0);
        Assert.NotEmpty(overflow["spans"]!.AsArray());
        // 알림 표본만 넘쳐도 끊김 없음(정상)으로 말하지 않는다 (G4 1라운드 7)
        var direct = LinkEvidenceBuilder.FromEvents(rec.Events).Summaries.Single(x => x.Source == LinkSource.Direct);
        Assert.True(direct.Partial);
        Assert.Contains("overflow", direct.Secondary);
        Assert.NotEqual(LinkDisplay.ObservedNone, direct.Display);
    }

    [Fact]
    public async Task CallbacksAfterStopAreIgnored()
    {
        var clock = new ManualClock(Start);
        var links = new FakeLinks();
        var s = MeasurementSession.Start(Options(clock, links));
        await Rounds(clock, s, 2);
        await s.StopAsync();
        int before = Links(Reread()).Length;
        links.Notify(LinkState.Down);   // 해제 뒤 도착한 콜백
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(before, Links(Reread()).Length);
    }

    /// <summary>회차 index에서 풀어 줄 때까지 응답하지 않는 공급자 (취소를 무시: 엔진 정지가 기다리게).</summary>
    private sealed class BlockingProbe(long index, ManualResetEventSlim release) : IProbe
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken) =>
            round == index ? Task.Run(() => { Entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return ProbeResult.Success(1); }) : Task.FromResult(ProbeResult.Success(1));
    }

    [Fact]
    public async Task SamplesAfterFixedEndAreIgnoredWhileEngineIsStopping()
    {
        // G4 3·4라운드: 끝 경계를 고정한 뒤 엔진 정지를 기다리는 동안(링크가 아직 닫히지 않음) 온 표본은 판정에 들어가지 않는다
        var clock = new ManualClock(Start);
        var links = new FakeLinks();
        var release = new ManualResetEventSlim(false);
        var probe = new BlockingProbe(3, release);
        var s = MeasurementSession.Start(Options(clock, links) with { ProbeFor = _ => probe });
        await Rounds(clock, s, 2);
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));   // 회차 3: 공급자가 막힘 → 엔진 정지가 기다린다
        Assert.True(probe.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "회차 3 공급자 진입");
        var stopping = s.StopAsync();                                // 끝 경계 = 3초
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(s.LinkClosed, "엔진 정지를 기다리는 동안 링크 수집은 아직 열려 있어야 한다 (시험 전제)");
        clock.Advance(TimeSpan.FromMilliseconds(600));
        links.Notify(LinkState.Down);                                // 3.6초 다운 (끝 뒤)
        release.Set();
        await stopping;
        Assert.Empty(LinkEvidenceBuilder.FromEvents(Reread().Events).Downs);
        Assert.DoesNotContain(Links(Reread()), e => (string?)e["collection"] == "down");
    }

    [Fact]
    public async Task SamplesAfterPlannedEndAreIgnored()
    {
        // G4 4라운드 2: 계획 끝(60초)을 넘긴 표본은 종료 처리가 늦게 시작해도 받지 않는다.
        // 마지막 회차(59) 공급자를 막아 계획 끝을 넘긴 시각에 표본을 넣는다 (종료 처리는 아직 시작 전)
        var clock = new ManualClock(Start);
        var links = new FakeLinks();
        var release = new ManualResetEventSlim(false);
        var probe = new BlockingProbe(59, release);
        var s = MeasurementSession.Start(Options(clock, links) with { ProbeFor = _ => probe });
        await Rounds(clock, s, 58);
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));   // 회차 59 시작, 공급자 막힘
        Assert.True(probe.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "회차 59 공급자 진입");
        clock.Advance(TimeSpan.FromMilliseconds(1500));               // 60.5초
        Assert.False(s.LinkClosed);
        links.Notify(LinkState.Down);                                  // 계획 끝 뒤 표본
        release.Set();
        var result = await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);
        Assert.Equal(60_000, result.Summary!.EndMs);                  // 끝 = min(종료 처리 시작 60.5초, 계획 끝 60초)
        Assert.Empty(LinkEvidenceBuilder.FromEvents(Reread().Events).Downs);
    }

    [Fact]
    public async Task LateOverflowRegistrationCannotMergeDownsAcrossTheLostSample()
    {
        // G4 5라운드 1: 넘친 표본의 넘침 등록이 늦어져도, 그 뒤 표본은 등록이 끝난 뒤에야 큐에 들어가므로
        // 넘침 전 다운과 넘침 뒤 다운이 하나로 이어지지 않는다 (2.7초에 유실된 업이 확정 다운 안에 들면 안 된다)
        // M5 (M4 이월 2): 시간 대기 없이 신호로 단계를 고정한다. 넘친 표본(2.7초 업) 뒤에 들어온 재관측 다운이
        // 넘침 순번을 건너 처리되어 앞의 다운과 이어지지 않음을, 두 다운 구간의 존재와 경계로 검사한다
        var clock = new ManualClock(Start);
        var links = new FakeLinks();
        var s = MeasurementSession.Start(Options(clock, links, routes: false) with { LinkQueueCapacity = 1 });
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        links.Notify(LinkState.Down);                                     // 1.0초 다운
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));         // 회차 2: 처리 작업이 1.0초 다운을 처리
        await Eventually(() => s.Status.Observed == 3 && s.LinkQueueCount == 0, "회차 2 처리와 1.0초 다운 배출");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        links.Notify(LinkState.Down);                                     // 2.5초 다운 → 큐가 가득 참
        Assert.Equal(1, s.LinkQueueCount);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        s.TestHookAfterLinkEnqueueFailed = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var lost = Task.Run(() => links.Notify(LinkState.Up), TestContext.Current.CancellationToken);            // 2.7초 업 → 넘침, 등록 전(잠금 안)에 멈춤
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "넘침 지점 진입");
        s.TestHookAfterLinkEnqueueFailed = null;
        // 원래 경쟁을 겹친다 (G4 M5 1라운드 C1): 넘침 등록이 멈춘 동안 ① 처리 작업이 2.5초 다운을 꺼내 큐에 자리가 나고
        // ② 재관측 생산자(3.0초 다운)가 들어온다. 둘 다 진입을 신호로 확인한 뒤 등록을 풀어 준다
        var dequeued = new ManualResetEventSlim(false);
        var arrived = new ManualResetEventSlim(false);
        s.TestHookLinkDequeued = () => dequeued.Set();
        await AdvanceWhenWaiting(clock, TimeSpan.FromMilliseconds(300));  // 회차 3 (3.0초): 처리 작업이 2.5초 다운을 꺼냄
        Assert.True(dequeued.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "소비자가 꺼냄 (큐에 자리)");
        Assert.Equal(0, s.LinkQueueCount);
        s.TestHookLinkSampleArrived = () => arrived.Set();
        var reobserved = Task.Run(() => links.Notify(LinkState.Down), TestContext.Current.CancellationToken);    // 3.0초 다운 재관측
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "재관측 생산자 진입");
        Assert.False(reobserved.IsCompleted, "넘침 등록 전에는 큐에 들어가지 못한다");
        s.TestHookLinkSampleArrived = null;
        s.TestHookLinkDequeued = null;
        release.Set();
        await lost;                                                       // 넘침 등록 끝
        await reobserved;                                                 // 그 뒤에야 재관측이 큐에 (넘침 순번을 싣고)
        await Eventually(() => s.Status.Observed == 4, "회차 3 처리");
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));         // 회차 4: 재관측 처리
        await Eventually(() => s.Status.Observed == 5 && s.LinkQueueCount == 0, "회차 4 처리와 재관측 배출");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        links.Notify(LinkState.Up);                                       // 4.5초 업
        await AdvanceWhenWaiting(clock, TimeSpan.FromMilliseconds(500));  // 회차 5: 업 처리
        await Eventually(() => s.Status.Observed == 6 && s.LinkQueueCount == 0, "회차 5 처리");
        await s.StopAsync();

        var downs = LinkEvidenceBuilder.FromEvents(Reread().Events).Downs.OrderBy(d => d.FromMs).ToArray();
        Assert.DoesNotContain(downs, d => d.FromMs <= 2700 && d.ToMs >= 2700);   // 유실된 업을 덮지 않는다
        Assert.Equal(2, downs.Length);                                           // 넘침 전과 넘침 뒤가 따로
        Assert.Equal(1000L, downs[0].FromMs);
        Assert.True(downs[0].ToMs <= 2500, $"앞 구간 끝 {downs[0].ToMs}");
        Assert.Equal(3000L, downs[1].FromMs);
        Assert.True(downs[1].ToMs is >= 3000 and <= 4500, $"뒤 구간 끝 {downs[1].ToMs}");
    }

    [Fact]
    public async Task CutoffRejectsSamplesWhenTheyArrive()
    {
        // M4 이월 1-가: 받을 때 검사. 마감 뒤 시각의 표본은 큐에 들어가지 않는다 (꺼낼 때 검사만 남으면 큐 수가 1이 되어 실패)
        var clock = new ManualClock(Start);
        var links = new FakeLinks();
        var s = MeasurementSession.Start(Options(clock, links));
        await Rounds(clock, s, 2);
        s.TestSetLinkCutoff(1000);
        links.Notify(LinkState.Down);                    // 2초 > 마감 1초
        Assert.Equal(0, s.LinkQueueCount);
        await s.StopAsync();
        Assert.Empty(LinkEvidenceBuilder.FromEvents(Reread().Events).Downs);
    }

    [Fact]
    public async Task CutoffDropsQueuedSamplesWhenTheyAreProcessed()
    {
        // M4 이월 1-나: 꺼낼 때 검사. 받을 때는 마감 전이라 큐에 들어갔지만, 처리 전에 마감이 앞당겨지면 버린다
        // (받을 때 검사만 남으면 다운이 기록되어 실패)
        var clock = new ManualClock(Start);
        var links = new FakeLinks();
        var s = MeasurementSession.Start(Options(clock, links, routes: false));
        await Rounds(clock, s, 2);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        links.Notify(LinkState.Down);                    // 2.3초, 큐에 들어감 (마감 = 계획 끝 60초)
        Assert.Equal(1, s.LinkQueueCount);
        s.TestSetLinkCutoff(2000);                       // 처리 전에 마감을 2초로
        await AdvanceWhenWaiting(clock, TimeSpan.FromMilliseconds(700));
        await Eventually(() => s.Status.Observed >= 4 && s.LinkQueueCount == 0, "회차 3 처리와 큐 배출");
        await s.StopAsync();
        Assert.Empty(LinkEvidenceBuilder.FromEvents(Reread().Events).Downs);
    }

    [Fact]
    public async Task LiveLinkStateCarriesAnOngoingDownIntoTheProvisionalAnalysis()
    {
        // M5 설계 3.2 [1-5, 2-2]: 같은 상태의 반복 표본은 줄로 쓰지 않으므로 이벤트만으로는 진행 중 다운이 첫 표본에서 끝난다.
        // 실시간 사본(LinkLive)을 분석에 넘기면 마지막 표본까지 이어지고, 그 회차들이 링크 끊김으로 판정된다
        var clock = new ManualClock(Start);
        var links = new FakeLinks { StateAt = p => p >= 70 ? LinkState.Down : LinkState.Up };
        var s = MeasurementSession.Start(Options(clock, links) with
        {
            Profile = OneMinute with { Duration = TimeSpan.FromMinutes(5) },
            ProbeFor = _ => new RecordingProbe((_, round) => round >= 70 ? ProbeResult.Lost("TimedOut") : ProbeResult.Success(2)),
        });
        await Rounds(clock, s, 80);
        var inputs = s.Live.AnalysisInputs();
        var adapter = Assert.Single(inputs.Links.Adapters);
        Assert.Equal(LinkState.Down, adapter.State);
        Assert.True(adapter.LastSampleMs > adapter.FirstMs, "반복 표본으로 마지막 관측이 늘어난다");
        Assert.True(adapter.LastSampleMs <= inputs.ElapsedMs, "현재 시각까지 늘리지 않는다");

        var withLive = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(inputs.Header, inputs.Rounds, inputs.Gaps, inputs.Events, null, true, inputs.ElapsedMs, inputs.Links);
        var down = Assert.Single(withLive.Links.Downs);
        Assert.Equal(adapter.LastSampleMs, down.ToMs);
        var last = withLive.Rounds.Last(r => r.Verdict is not null);
        Assert.Equal(NetPulse.Core.Analysis.Verdict.LinkDown, last.Verdict);
        var ongoing = withLive.Episodes.Last();
        Assert.Equal(NetPulse.Core.Analysis.CandidateKind.PcLink, ongoing.Parts[^1].Candidate.Kind);

        // 이벤트만으로는 진행 중 다운의 마지막 표본을 모른다 (사본을 넘겨야 하는 이유)
        var eventsOnly = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(inputs.Header, inputs.Rounds, inputs.Gaps, inputs.Events, null, true, inputs.ElapsedMs);
        Assert.True(eventsOnly.Links.Downs.All(d => d.ToMs < adapter.LastSampleMs), "이벤트만으로는 마지막 표본까지 복원하지 못한다");
        await s.StopAsync();
    }

    [Fact]
    public async Task LiveSnapshotDoesNotDuplicateTheFinalSummary()
    {
        // 끝 요약을 쓴 뒤의 사본에는 잠정 요약을 붙이지 않는다 (실제 요약과 함께 보지 않게)
        var clock = new ManualClock(Start);
        var links = new FakeLinks();
        var s = MeasurementSession.Start(Options(clock, links));
        await Rounds(clock, s, 3);
        Assert.False(s.Live.AnalysisInputs().Links.SummaryWritten);
        await s.StopAsync();
        var after = s.Live.AnalysisInputs();
        Assert.True(after.Links.SummaryWritten);
        Assert.Empty(after.Links.ToSummaryEvents(Id, after.ElapsedMs, after.Wall));
        Assert.Single(after.Events, e => (string?)e["type"] == "link" && (string?)e["collection"] == "summary" && (string?)e["source"] == "direct");
    }

    [Fact]
    public void OverflowSpansBeyondCapExtendTheLastSpan()
    {
        // G4 4라운드 1: 51번째부터의 넘침도 시각을 잃지 않는다 (마지막 범위를 늘려 보수적으로 덮음)
        var spans = new List<(long From, long To)>();
        for (int i = 0; i < 50; i++) MeasurementSession.AddOverflowSpan(spans, i * 2000L);
        Assert.Equal(50, spans.Count);
        MeasurementSession.AddOverflowSpan(spans, 102_000);
        Assert.Equal(50, spans.Count);
        Assert.Equal((98_000L, 102_000L), spans[^1]);
        // 그 범위가 다운 재관측과 이어지는 것을 막는다 (빌더): 101초 다운 → 102초 유실 → 103초 다운 → 106초 업
        var events = new List<JsonObject>
        {
            Direct("down", 101_000), Direct("down", 103_000, new JsonObject { ["prevState"] = "down", ["prevFirstMs"] = 101_000, ["prevLastMs"] = 101_000 }),
            Direct("up", 106_000, new JsonObject { ["prevState"] = "down", ["prevFirstMs"] = 103_000, ["prevLastMs"] = 103_000 }),
            new() { ["id"] = "x", ["type"] = "link", ["at"] = 0, ["wall"] = RecordSchema.Iso(Start), ["source"] = "direct", ["collection"] = "overflow", ["count"] = 51,
                ["spans"] = new JsonArray(spans.Select(x => (JsonNode)new JsonArray(x.From, x.To)).ToArray()) },
        };
        var downs = LinkEvidenceBuilder.FromEvents(events).Downs.Select(d => (d.FromMs, d.ToMs)).ToArray();
        Assert.Equal([(101_000L, 101_000L), (103_000L, 106_000L)], downs);
    }

    private static JsonObject Direct(string collection, long ms, JsonObject? extra = null)
    {
        var o = new JsonObject { ["id"] = "x", ["type"] = "link", ["at"] = ms, ["wall"] = RecordSchema.Iso(Start), ["source"] = "direct", ["adapter"] = "a1",
            ["interfaceIndex"] = 7, ["kind"] = "wired", ["virtual"] = false, ["collection"] = collection, ["eventMs"] = ms };
        if (extra is not null) foreach (var (k, v) in extra.ToArray()) { extra.Remove(k); o[k] = v; }
        return o;
    }

    [Fact]
    public async Task NoInterfaceAndStartFailureAreExplicit()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock, new FakeLinks(), routes: false));
        await Rounds(clock, s, 1);
        await s.StopAsync();
        var none = LinkEvidenceBuilder.FromEvents(Reread().Events).Summaries.Single();
        Assert.Equal(("*", LinkDisplay.Unsupported, "no-interface"), (none.Adapter, none.Display, none.Code));
    }

    [Fact]
    public async Task DeniedStartIsRecorded()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock, new FakeLinks { StartResult = (LinkCapability.Denied, "access-denied") }));
        await Rounds(clock, s, 1);
        await s.StopAsync();
        var denied = LinkEvidenceBuilder.FromEvents(Reread().Events).Summaries.Single(x => x.Source == LinkSource.Direct);
        Assert.Equal(LinkDisplay.Denied, denied.Display);
    }

    [Fact]
    public async Task LogEventsAreMappedOrKeptUnmapped()
    {
        var clock = new ManualClock(Start);
        var log = new FakeLog((from, to) => new LinkLogResult(
            new Dictionary<string, (LinkCapability, string?)> { [FakeLinks.Info.Key] = (LinkCapability.Supported, null) },
            [
                new LogLinkEvent(FakeLinks.Info.Key, LinkMatch.Linked, LinkState.Down, Start.AddMilliseconds(2500)),
                new LogLinkEvent(FakeLinks.Info.Key, LinkMatch.Linked, LinkState.Up, Start.AddMilliseconds(2700)),
                new LogLinkEvent(FakeLinks.Info.Key, LinkMatch.Linked, LinkState.Down, Start.AddHours(5)),   // 측정 밖 → 환산 안 됨
                new LogLinkEvent(null, LinkMatch.Ambiguous, LinkState.Down, Start.AddMilliseconds(1500)),
            ], 1, LinkCapability.Supported, null));
        var s = MeasurementSession.Start(Options(clock, new FakeLinks(), log));
        await Rounds(clock, s, 5);
        await s.StopAsync();
        var rec = Reread();
        var ev = LinkEvidenceBuilder.FromEvents(rec.Events);
        var logDown = Assert.Single(ev.Downs, d => d.Source == LinkSource.Log);
        Assert.Equal((2500L, 2700L), (logDown.FromMs, logDown.ToMs));
        Assert.Equal(0, ev.Unmapped);   // 측정 밖 이벤트는 환산하지 않고 참고(historical)로만 (G4 1라운드 3)
        Assert.Single(Links(rec), e => (string?)e["link"] == "historical");
        Assert.Equal(1, ev.Unlinked);
        Assert.Equal(LinkDisplay.Down, ev.Summaries.Single(x => x.Source == LinkSource.Log && x.Adapter == "a1").Display);
        Assert.Contains(Links(rec), e => (string?)e["source"] == "log" && e["unparsed"] is not null);
    }

    [Fact]
    public async Task LogFailureIsRecordedNotNormal()
    {
        var clock = new ManualClock(Start);
        var log = new FakeLog((_, _) => new LinkLogResult(new Dictionary<string, (LinkCapability, string?)>(), [], 0, LinkCapability.Denied, "access-denied"));
        var s = MeasurementSession.Start(Options(clock, new FakeLinks(), log));
        await Rounds(clock, s, 1);
        await s.StopAsync();
        var logSummary = LinkEvidenceBuilder.FromEvents(Reread().Events).Summaries.Single(x => x.Source == LinkSource.Log);
        Assert.Equal(LinkDisplay.Denied, logSummary.Display);
    }

    [Fact]
    public async Task DroppedEventsAreCountedInSummary()
    {
        var clock = new ManualClock(Start);
        primary.Next = name => name.StartsWith("events", StringComparison.Ordinal) ? Fault.DiskFull : Fault.None;
        var links = new FakeLinks { StateAt = p => p % 2 == 0 ? LinkState.Up : LinkState.Down };   // 매 회차 상태가 바뀌어 이벤트가 많다
        var s = MeasurementSession.Start(Options(clock, links) with { PendingLimit = 3 });
        await Rounds(clock, s, 10);
        var result = await s.StopAsync();
        Assert.True(result.Summary!.EventsDropped > 0, "버린 이벤트 " + result.Summary.EventsDropped);
        var rec = Reread();
        Assert.Equal(result.Summary.EventsDropped, rec.Summary!.EventsDropped);
        // 버린 이벤트는 다시 읽을 때 이벤트 손상으로 표시된다 (G4 1라운드 8)
        Assert.Equal(RecordStatus.Damaged, rec.Status);
        Assert.Contains("이벤트 유실", rec.MissingNotice);
    }
}

/// <summary>데모 시나리오의 링크 끊김이 판정·후보까지 이어지는지 (M4 설계 10절 G3 데모).</summary>
public sealed class DemoLinkScenarioTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public async Task LinkDropScenarioIsJudgedAsScripted()
    {
        var scenario = NetPulse.Core.Demo.Scenario.Load(Path.Combine(AppContext.BaseDirectory, "scenarios", "link-drop.json"));
        var demo = new NetPulse.Core.Demo.DemoProbe(scenario);
        var network = new NetPulse.Core.Demo.DemoNetwork(scenario);
        var profile = new Profile("demo", "데모",
            scenario.ProbeTargets.Select(t => new Target(t.Id, t.Id, t.Address, t.Role == "local" ? TargetRole.Local : TargetRole.External, t.Id, ProbeMethod.Icmp)).ToArray(),
            Profile.DefaultTimeout, TimeSpan.FromMinutes(3), ExpectedConnection.Wired);
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = clock, ProbeFor = _ => demo, Routes = network, Links = network, NewId = _ => "demo-link",
            Primary = new DiskRecordRoot(dir.Sub("p"), "primary"), Fallback = new DiskRecordRoot(dir.Sub("f"), "fallback"),
        });
        for (int i = 0; i < 179; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        var result = await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);

        var rec = RecordReader.Read(RecordReader.FindSegments("demo-link", [dir.Sub("p")]));
        var a = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(rec);
        var parts = a.Episodes.SelectMany(e => e.Parts).ToArray();
        var link = Assert.Single(parts, p => p.Verdict == NetPulse.Core.Analysis.Verdict.LinkDown);
        Assert.Equal((60L, 65L), (link.FirstRound, link.LastRound));
        Assert.Equal(NetPulse.Core.Analysis.CandidateKind.PcLink, link.Candidate.Kind);
        var outage = Assert.Single(parts, p => p.Verdict == NetPulse.Core.Analysis.Verdict.ExternalOutage);
        Assert.Equal((120L, 123L), (outage.FirstRound, outage.LastRound));
        Assert.Equal(NetPulse.Core.Analysis.CandidateKind.AfterRouter, outage.Candidate.Kind);
        Assert.Equal((NetPulse.Core.Analysis.Grade.VeryBad, "link-down"), (a.Grade.Grade, a.Grade.Reason));
    }
}

/// <summary>기록 확장 (M4 설계 A7, 2-4)과 이월 2-7.</summary>
public sealed class RecordExtensionTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private const string Id = "m1";

    private static RecordHeader Header(ExpectedConnection? e) => new(Id, 0, "v", W0, 0,
        [new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp)], 1000, 800, 60_000, "primary", e);

    [Theory]
    [InlineData(ExpectedConnection.Any)]
    [InlineData(ExpectedConnection.Wired)]
    [InlineData(ExpectedConnection.Wireless)]
    [InlineData(null)]
    public void ExpectedRoundTripsAndMissingMeansUnrecorded(ExpectedConnection? e)
    {
        var (h, unsupported) = RecordSchema.ParseHeader(RecordSchema.HeaderJson(Header(e)));
        Assert.False(unsupported);
        Assert.Equal(e, h!.Expected);
    }

    [Fact]
    public void UnknownExpectedValueIsHeaderDamage()
    {
        var json = RecordSchema.HeaderJson(Header(ExpectedConnection.Wired)).Replace("\"wired\"", "\"cable\"");
        Assert.Throws<FormatException>(() => RecordSchema.ParseHeader(json));
    }

    [Fact]
    public void RoundEndRoundTripsAndEndBeforeStartIsDamage()
    {
        var targets = new HashSet<string> { "a" };
        var r = new RoundData(3, 3000, 3005, W0, 0, false, [new TargetResult("a", Measurement.ProbeOutcome.Success, 1, null)], 3060);
        var (back, problem) = RecordSchema.ParseRound(RecordSchema.RoundLine(Id, r), Id, targets);
        Assert.Equal(RecordSchema.LineProblem.None, problem);
        Assert.Equal(3060, back!.EndMs);
        var noEnd = RecordSchema.ParseRound(RecordSchema.RoundLine(Id, r with { EndMs = null }), Id, targets).Round;
        Assert.Null(noEnd!.EndMs);
        Assert.Equal(RecordSchema.LineProblem.BadCombination, RecordSchema.ParseRound(RecordSchema.RoundLine(Id, r with { EndMs = 3000 }), Id, targets).Problem);
    }

    [Fact]
    public void EventsDroppedMissingIsUnrecorded()
    {
        var withValue = new RecordSummary(Id, EndReason.Stopped, W0, 1, 1, 0, 0, 0, 0, false, null, 4);
        Assert.Contains("\"eventsDropped\": 4", RecordSchema.SummaryJson(withValue));
        var old = new RecordSummary(Id, EndReason.Stopped, W0, 1, 1, 0, 0, 0, 0, false, null);
        Assert.Contains("\"eventsDropped\": null", RecordSchema.SummaryJson(old));
    }

    [Fact]
    public void LateSampleAtSameElapsedAsResumeDoesNotConsumeResume()
    {
        // 2라운드 지적 2-7의 순서: 0 → 절전 900 → (복귀 전에 잡힌 1000 표본, 처리 지연) → 복귀 1000 → 지연 표본 처리 → 2000
        var g = new GapTracker(TimeSpan.FromSeconds(1));
        Assert.Null(g.CheckClock(TimeSpan.Zero, W0));
        g.OnSuspend(TimeSpan.FromMilliseconds(900), W0.AddMilliseconds(900));
        g.OnResume(TimeSpan.FromMilliseconds(1000), W0.AddHours(1));
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(1000), W0.AddMilliseconds(1000)));            // 지연된 옛 표본
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(2000), W0.AddHours(1).AddMilliseconds(1000))); // 실제 복귀 뒤: 기준만 다시
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(3000), W0.AddHours(1).AddMilliseconds(2000)));
        Assert.Equal(8000, g.CheckClock(TimeSpan.FromMilliseconds(4000), W0.AddHours(1).AddMilliseconds(3000 + 8000)));
    }

    [Fact]
    public void ClockEventCarriesPreviousSample()
    {
        var g = new GapTracker(TimeSpan.FromSeconds(1));
        g.CheckClock(TimeSpan.Zero, W0);
        g.CheckClock(TimeSpan.FromSeconds(1), W0.AddSeconds(1));
        Assert.Equal(-60_000, g.CheckClock(TimeSpan.FromSeconds(2), W0.AddSeconds(2 - 60)));
        Assert.Equal(1000, g.PreviousSampleMs);   // 불확실 구간 시작 (2-3)
    }
}
