using System.Net;
using NetPulse.Core.Analysis;
using NetPulse.Core.Demo;
using NetPulse.Core.Detection;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M5 설계 3.3~3.5: 기록 목록, 데모 세계, 화면용 순수 모델.</summary>
public sealed class M5ModelTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static readonly Target Router = new("r", "우리집 공유기", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp);
    private static readonly Target ExtA = new("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp);
    private static readonly Target ExtB = new("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "Y", ProbeMethod.Icmp);
    private static readonly Target[] Three = [Router, ExtA, ExtB];

    private static TargetResult Ok(string id, double ms) => new(id, ProbeOutcome.Success, ms, null);
    private static TargetResult Lost(string id) => new(id, ProbeOutcome.Lost, null, null);
    private static TargetResult Err(string id) => new(id, ProbeOutcome.Error, null, "x");
    private static RoundData R(long round, params TargetResult[] results) => new(round, round * 1000, round * 1000 + 3, Start.AddSeconds(round), 0, false, results);
    private static RoundData Fine(long round, double ext = 20) => R(round, Ok("r", 2), Ok("a", ext), Ok("b", ext + 4));
    private static RoundData Out(long round) => R(round, Ok("r", 2), Lost("a"), Lost("b"));

    // ---------------- 기록 목록 ----------------

    private string MakeRecord(string root, string folderName, string id, DateTimeOffset started, int segment = 0, string? summaryText = null, bool withSummary = true)
    {
        var folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(folder);
        var header = new RecordHeader(id, segment, "1.0", started, 540, Three, 1000, 800, 600_000, "primary", ExpectedConnection.Wired);
        File.WriteAllText(Path.Combine(folder, "measurement.json"), RecordSchema.HeaderJson(header));
        if (withSummary)
            File.WriteAllText(Path.Combine(folder, "summary.json"), summaryText ?? RecordSchema.SummaryJson(
                new RecordSummary(id, EndReason.Stopped, started.AddMinutes(5), 300, 300, 0, 0, 0, 299, false, null, 0, 300_000)));
        return folder;
    }

    [Fact]
    public void CatalogGroupsSegmentsAndKeepsLifecycleApartFromIntegrity()
    {
        var p = dir.Sub("p");
        var f = dir.Sub("f");
        MakeRecord(p, "20261113T010000Z_aaaaaa", "20261113T010000Z_aaaaaa", Start.AddHours(1));                                     // 끝남
        MakeRecord(p, "20261113T020000Z_bbbbbb", "20261113T020000Z_bbbbbb", Start.AddHours(2), withSummary: false);              // 지금 도는 측정
        MakeRecord(p, "20261113T030000Z_cccccc", "20261113T030000Z_cccccc", Start.AddHours(3), withSummary: false);              // 끝 기록 없음
        MakeRecord(p, "20261113T040000Z_dddddd", "20261113T040000Z_dddddd", Start.AddHours(4), summaryText: "{\"broken\"");      // 요약 손상
        MakeRecord(p, "20261113T050000Z_eeeeee", "20261113T050000Z_eeeeee", Start.AddHours(5), withSummary: false);
        MakeRecord(f, "20261113T050000Z_eeeeee~2", "20261113T050000Z_eeeeee", Start.AddHours(5), segment: 2);                    // 조각 1이 없음
        Directory.CreateDirectory(Path.Combine(p, "20261113T060000Z_ffffff"));                                                    // 머리 없음
        Directory.CreateDirectory(Path.Combine(p, "not-a-record"));

        var list = RecordCatalog.List([p, f], 10, "20261113T020000Z_bbbbbb");
        Assert.Equal(["20261113T060000Z_ffffff", "20261113T050000Z_eeeeee", "20261113T040000Z_dddddd", "20261113T030000Z_cccccc",
            "20261113T020000Z_bbbbbb", "20261113T010000Z_aaaaaa"], list.Select(e => e.Id));
        var byId = list.ToDictionary(e => e.Id[^6..]);
        Assert.Equal((RecordLifecycle.Ended, RecordIntegrity.Ok, (EndReason?)EndReason.Stopped), (byId["aaaaaa"].Lifecycle, byId["aaaaaa"].Integrity, byId["aaaaaa"].EndReason));
        Assert.Equal(RecordLifecycle.Running, byId["bbbbbb"].Lifecycle);
        Assert.Equal((RecordLifecycle.NoSummary, RecordIntegrity.Ok), (byId["cccccc"].Lifecycle, byId["cccccc"].Integrity));
        Assert.Equal((RecordLifecycle.NoSummary, RecordIntegrity.Damaged), (byId["dddddd"].Lifecycle, byId["dddddd"].Integrity));
        Assert.Equal((RecordIntegrity.MissingSegments, 2), (byId["eeeeee"].Integrity, byId["eeeeee"].Segments.Count));
        Assert.Equal(RecordIntegrity.Unreadable, byId["ffffff"].Integrity);
        Assert.Equal(2, RecordCatalog.List([p, f], 2, null).Count);
    }

    // ---------------- 데모 세계 ----------------

    private static Scenario Load(string name) => Scenario.Load(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json"));

    [Fact]
    public void DemoDetectionGoesThroughThePlannerWithScenarioTargets()
    {
        var world = new DemoWorld(Load("outage-long"), new DemoTimeBase(Start, 1));
        var r = world.Detect();
        Assert.Equal(DetectionStatus.Ok, r.Status);
        Assert.Equal(InterfaceKind.Wired, r.Kind);
        Assert.Equal(["공유기", "예시 통신사 A", "예시 통신사 B"], r.Proposal!.Targets.Select(t => t.Name));
        Assert.Equal(["예시 통신사 A", "예시 통신사 B"], r.Proposal.Targets.Where(t => t.Role == TargetRole.External).Select(t => t.Provider!));
        Assert.Equal(TimeSpan.FromMinutes(3), r.Proposal.Duration);
        Assert.Empty(ProfileValidator.Validate(r.Proposal));          // 경고 없음 (다양성 충족)

        Assert.Equal(DetectionStatus.NoNetwork, new DemoWorld(Load("no-network"), new DemoTimeBase(Start, 1)).Detect().Status);
        var vpn = new DemoWorld(Load("vpn"), new DemoTimeBase(Start, 1)).Detect();
        Assert.Contains("가상 어댑터", vpn.Notice);
        Assert.Contains("공유기(게이트웨이)가 1개 더", vpn.Notice);
        var fail = new DemoWorld(Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "outage-long.json"))
            .Replace("\"seed\": 23,", "\"seed\": 23, \"faults\": { \"detect\": \"unknown\" },")), new DemoTimeBase(Start, 1)).Detect();
        Assert.Equal(DetectionStatus.Unknown, fail.Status);
        Assert.DoesNotContain(fail.Proposal!.Targets, t => t.Role == TargetRole.Local);
        // M9 C1: IPv4 기본 경로 없음 (연결은 있음) → 실측과 같은 안내, 외부 대상만 제안
        var noIpv4 = new DemoWorld(Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "outage-long.json"))
            .Replace("\"seed\": 23,", "\"seed\": 23, \"faults\": { \"detect\": \"no-ipv4\" },")), new DemoTimeBase(Start, 1)).Detect();
        Assert.Equal(DetectionStatus.NoIpv4Route, noIpv4.Status);
        Assert.Contains("IPv4 인터넷 경로가 없습니다", noIpv4.Notice);
        Assert.Equal(["예시 통신사 A", "예시 통신사 B"], noIpv4.Proposal!.Targets.Select(t => t.Name));
        Assert.Throws<ScenarioException>(() => Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "outage-long.json"))
            .Replace("\"seed\": 23,", "\"seed\": 23, \"faults\": { \"detect\": \"bogus\" },")));
    }

    [Fact]
    public async Task EachMeasurementClockStartsAtZeroAndKeepsTheWallClockOffset()
    {
        var timeBase = new DemoTimeBase(Start, 600);                  // 처리 속도에 맞춰 진행 (가상)
        var world = new DemoWorld(Load("steady"), timeBase);
        var root = new DiskRecordRoot(dir.Sub("p"), "primary");
        var a = world.NewRun(root, root, new DemoRunRules(), () => true).Clock;
        await a.Delay(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(90), a.Elapsed);
        var b = world.NewRun(root, root, new DemoRunRules(), () => true).Clock;   // 다시 측정: 0부터, 벽시계는 이어짐
        Assert.Equal(TimeSpan.Zero, b.Elapsed);
        Assert.Equal(Start.AddSeconds(90), b.Now);
        await b.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(b.Now - Start - TimeSpan.FromSeconds(90), b.Elapsed);
    }

    [Fact]
    public async Task SpeedSwitchKeepsNowMinusElapsedAndHoldFreezesBoth()
    {
        var timeBase = new DemoTimeBase(Start, 600);
        var world = new DemoWorld(Load("steady"), timeBase);
        var root = new DiskRecordRoot(dir.Sub("p"), "primary");
        var clock = world.NewRun(root, root, new DemoRunRules(HoldRound: 12, SpeedAfter: (10, 20)), () => true).Clock;
        var offset = clock.Now - clock.Elapsed;
        await clock.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);   // 가상 진행 → 10회차에서 20배속
        Assert.Equal(20, timeBase.Speed);
        await clock.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);    // 실제 약 50 ms
        // 시각 변경으로 보이지 않는다 (두 값을 읽는 사이에도 시간이 흐르므로 1 ms 안)
        Assert.InRange((clock.Now - clock.Elapsed - offset).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = clock.Delay(TimeSpan.FromSeconds(10), cts.Token);                      // 12.5초에서 멈춘다
        await Eventually(() => timeBase.Held, "멈춤");
        var frozen = (clock.Elapsed, clock.Now);
        Assert.Equal(TimeSpan.FromMilliseconds(12_500), frozen.Elapsed);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(frozen, (clock.Elapsed, clock.Now));
        Assert.False(waiting.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task DemoSessionTakesTheProductPathAndFindsTheOutageCandidate()
    {
        // 데모는 가장 바깥만 바꿔 끼운다: 세션·기록·분석은 제품 코드 (기획서 4.3)
        var world = new DemoWorld(Load("outage-long"), new DemoTimeBase(Start, 600));
        var root = new DiskRecordRoot(dir.Sub("p"), "primary");
        var fallback = new DiskRecordRoot(dir.Sub("f"), "fallback");
        MeasurementSession? session = null;
        var run = world.NewRun(root, fallback, new DemoRunRules(), () => session is null || session.Engine.State.Rounds - session.Status.Observed < 20);
        session = MeasurementSession.Start(new SessionOptions
        {
            Profile = world.Detect().Proposal!, Clock = run.Clock, ProbeFor = run.ProbeFor, Primary = run.Primary, Fallback = run.Fallback,
            Routes = run.Network, Links = run.Network, Power = run.Power, LinkLogSettle = TimeSpan.Zero,
        });
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);
        var record = RecordReader.Read(RecordReader.FindSegments(session.Id, [root.Path!, fallback.Path!]));
        Assert.Equal(180, record.Rounds.Count);
        Assert.Equal(0, record.Summary!.Unprocessed);
        Assert.DoesNotContain(record.Events, e => (string?)e["type"] is "clock" or "gap");   // 처리 속도 진행이 시각 변경·공백으로 보이지 않는다
        var analysis = MeasurementAnalysis.Analyze(record);
        var outage = Assert.Single(analysis.Episodes);
        Assert.Equal((90L, 149L), (outage.FirstRound, outage.LastRound));
        Assert.Equal(CandidateKind.AfterRouter, outage.Parts.Single().Candidate.Kind);
        Assert.Equal(MapCandidate.LocalToInternet, PathMapModel.PickCandidate(analysis, ended: true).Candidate);
    }

    [Fact]
    public async Task StorageFaultsGoThroughTheRealWritePath()
    {
        var scenario = Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json"))
            .Replace("\"seed\": 11,", "\"seed\": 11, \"durationRounds\": 120, \"faults\": { \"storageFailFrom\": 40, \"storageRecoverAt\": 60 },")
            .Replace("\"durationRounds\": 3600,", ""));
        var world = new DemoWorld(scenario, new DemoTimeBase(Start, 600));
        var root = new DiskRecordRoot(dir.Sub("p"), "primary");
        var fallback = new DiskRecordRoot(dir.Sub("f"), "fallback");
        MeasurementSession? session = null;
        var run = world.NewRun(root, fallback, new DemoRunRules(), () => session is null || session.Engine.State.Rounds - session.Status.Observed < 20);
        session = MeasurementSession.Start(new SessionOptions
        {
            Profile = world.Detect().Proposal!, Clock = run.Clock, ProbeFor = run.ProbeFor, Primary = run.Primary, Fallback = run.Fallback,
            Routes = run.Network, Power = run.Power, RetryTimeout = TimeSpan.FromMilliseconds(300),
        });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var record = RecordReader.Read(RecordReader.FindSegments(session.Id, [root.Path!, fallback.Path!]));
        Assert.Contains(record.Events, e => (string?)e["type"] == "storage" && (string?)e["kind"] == "writeFailed");
        Assert.Contains(record.Events, e => (string?)e["type"] == "storage" && (string?)e["kind"] == "recovered");

        var startFail = new DemoWorld(Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json"))
            .Replace("\"seed\": 11,", "\"seed\": 11, \"faults\": { \"storageStartFail\": true },")), new DemoTimeBase(Start, 600));
        var r2 = startFail.NewRun(root, fallback, new DemoRunRules(), () => true);
        Assert.Throws<SessionStartException>(() => MeasurementSession.Start(new SessionOptions
        {
            Profile = startFail.Detect().Proposal!, Clock = r2.Clock, ProbeFor = r2.ProbeFor, Primary = r2.Primary, Fallback = r2.Fallback,
        }));
    }

    // ---------------- 타임라인 ----------------

    [Fact]
    public void InternetLatencyIsTheMedianOfRespondingExternals()
    {
        Assert.Equal((2.0, 22.0), TimelineModel.Representative(R(0, Ok("r", 2), Ok("a", 20), Ok("b", 24)), Three));
        Assert.Equal((null, 20.0), TimelineModel.Representative(R(0, Lost("r"), Ok("a", 20), Lost("b")), Three));
        Assert.Equal((2.0, (double?)null), TimelineModel.Representative(R(0, Ok("r", 2), Lost("a"), Err("b")), Three));
        Assert.Equal(20.0, TimelineModel.Representative(R(0, Ok("a", 10), Ok("b", 20), Ok("c", 30)),
            [ExtA, ExtB, ExtB with { Id = "c" }]).Internet);
    }

    [Fact]
    public void BandsAreRoundSlotsWithRangeDurationsAndErrorsAreNotBands()
    {
        var rounds = new List<RoundData>();
        for (long i = 0; i < 30; i++) rounds.Add(i is >= 10 and <= 14 ? Out(i) : i == 20 ? R(i, Ok("r", 2), Err("a"), Err("b")) : i == 22 ? Out(i) : Fine(i));
        var bands = TimelineModel.Bands(rounds, Three, [], 1000);
        var ext = bands.Where(b => b.Kind == BandKind.ExternalNone).ToArray();
        Assert.Equal(2, ext.Length);                                  // 검사 오류(20)는 띠가 아님
        Assert.Equal((10_000L, 15_000L, 5), (ext[0].FromMs, ext[0].ToMs, ext[0].Rounds));
        Assert.Equal((4000L, (long?)6000), (ext[0].MinMs, ext[0].MaxMs));
        Assert.Equal("약 4초~6초", TimelineModel.DurationText(ext[0]));
        Assert.Equal("1회차 · 최대 약 2초", TimelineModel.DurationText(ext[1]));

        // 측정 끝까지, 측정 시작부터, 누락 회차는 잇지 않음, 한 회차 끝까지
        var tail = new List<RoundData> { Out(0), Out(1), Fine(2), Out(3), Out(5), Fine(6), Out(7) };
        var b2 = TimelineModel.Bands(tail, Three, [], 1000).Where(b => b.Kind == BandKind.ExternalNone).ToArray();
        Assert.Equal(4, b2.Length);
        Assert.True(b2[0].OpenStart);
        Assert.Equal("1초 이상", TimelineModel.DurationText(b2[0]));   // 앞 성공이 없어 범위 최댓값 없음
        Assert.Equal("1회차", TimelineModel.DurationText(b2[1]));      // 뒤가 누락이라 최댓값 없음
        Assert.Equal("1회차 · 이어지는 중", TimelineModel.DurationText(b2[3]));
        Assert.True(b2[3].OpenEnd);
        var longTail = TimelineModel.Bands([Fine(0), Out(1), Out(2), Out(3)], Three, [], 1000).Single(b => b.Kind == BandKind.ExternalNone);
        Assert.Equal("끝까지 이어짐 (2초 이상)", TimelineModel.DurationText(longTail));
    }

    [Fact]
    public void BandsDoNotDependOnRoundOrderAndNeedEveryExternalLost()
    {
        // 회차마다 사전을 만들지 않고 결과를 훑게 바꾼 뒤 (M8 게이트 bench): 순서가 섞인 입력, 일부 대상만 응답 없음, 결과가 빠진 대상
        var rounds = new List<RoundData>();
        for (long i = 0; i < 20; i++)
            rounds.Add(i is >= 3 and <= 5 ? Out(i)
                : i == 6 ? R(i, Ok("r", 2), Lost("a"), Ok("b", 20))           // 외부 띠 바로 뒤: 외부 하나만 응답해도 외부 띠를 끝내는 성공
                : i is 8 or 9 ? R(i, Ok("r", 2), Lost("a"), Ok("b", 20))      // 외부 하나만 응답 없음 → 일부 띠
                : i == 12 ? R(i, Ok("r", 2), Lost("a"))                        // 외부 b의 결과 없음 → 외부 띠 아님, 일부 띠
                : Fine(i));
        var ordered = TimelineModel.Bands(rounds, Three, [], 1000);
        var shuffled = TimelineModel.Bands(rounds.OrderBy(r => (r.Round * 7) % 20).ToList(), Three, [], 1000);
        Assert.Equal(ordered, shuffled);
        Assert.Equal([(BandKind.ExternalNone, 3L, 5L), (BandKind.PartialNone, 6L, 6L), (BandKind.PartialNone, 8L, 9L), (BandKind.PartialNone, 12L, 12L)],
            ordered.Select(b => (b.Kind, b.FirstRound, b.LastRound)).ToArray());
        Assert.Equal(4000L, ordered[0].MaxMs);   // 뒤 회차 6은 외부 b만 응답 → 외부 띠의 성공
        Assert.Null(ordered[1].MaxMs);           // 일부 띠의 앞 회차 5는 모든 대상 응답이 아님
        Assert.Equal(3000L, ordered[2].MaxMs);
        Assert.Equal(2000L, ordered[3].MaxMs);
    }

    [Fact]
    public void DecimationKeepsSpikesAndNeverBridgesMissingRoundsOrGaps()
    {
        var rounds = Enumerable.Range(0, 1000).Select(i => Fine(i, i == 500 ? 400 : 20)).ToList();
        var frame = TimelineModel.Frame(rounds, Three, [], [], [], [], 0, 1_000_000, 100, 1000, "전체");
        var points = frame.Internet.SelectMany(s => s).ToArray();
        Assert.True(points.Length <= 200);
        Assert.Contains(points, p => p.MaxMs >= 402);                  // 급증이 살아남음
        Assert.Equal(420, frame.AxisMaxMs);                            // 최댓값 402 ms → 20 ms 단위로 올림

        rounds.RemoveAt(300);                                          // 누락 회차
        var gap = new GapSpan(GapReason.Suspend, 700_500, 760_000, Start, Start, 700, 760);
        var withHoles = TimelineModel.Frame(rounds.Where(r => r.Round is < 701 or > 759).ToArray(), Three, [gap], [], [], [], 0, 1_000_000, 100, 1000, "전체");
        Assert.True(withHoles.Internet.Count >= 3, "누락과 공백에서 선이 끊긴다");
        var near = TimelineModel.Frame(rounds, Three, [], [], [], [], 290_000, 310_000, 400, 1000, "20초");
        Assert.Equal(2, near.Internet.Count);                          // 회차마다 점, 누락 회차 앞뒤가 따로
    }

    [Fact]
    public void RepresentativeCacheGivesTheSameFrameAndNumbersAsRecomputing()
    {
        // 캐시(그릴 때마다 새 회차만 구함)와 매번 다시 구한 결과가 같다: 회차가 늘어날 때, 앞부분이 바뀐 다른 목록일 때, 대상 목록이 바뀔 때
        var all = Enumerable.Range(0, 900).Select(i => i % 97 == 5 ? Out(i) : Fine(i, 15 + i % 13)).ToList();
        var cache = new RepresentativeCache();
        var stats = new Statistics.MeasurementStats(0, 0, 0, 0, []);
        void Same(IReadOnlyList<RoundData> rounds, IReadOnlyList<Target> targets, long from, long to, int columns)
        {
            var plain = TimelineModel.Frame(rounds, targets, [], [], [], [], from, to, columns, 1000, "창");
            var cached = TimelineModel.Frame(rounds, targets, [], [], [], [], from, to, columns, 1000, "창", cache);
            Assert.Equal(Flat(plain.Internet), Flat(cached.Internet));
            Assert.Equal(Flat(plain.Router), Flat(cached.Router));
            Assert.Equal(plain.AxisMaxMs, cached.AxisMaxMs);
            Assert.Equal(LiveSummary.Numbers(to, null, rounds, targets, stats), LiveSummary.Numbers(to, null, rounds, targets, stats, cache));
        }
        static string Flat(IReadOnlyList<IReadOnlyList<TimelinePoint>> s) => string.Join(" | ", s.Select(seg => string.Join(" ", seg.Select(p => p.ToString()))));

        for (int n = 100; n <= 900; n += 200) Same(all.Take(n).ToArray(), Three, 0, n * 1000L, 50);      // 늘어남 (전체 보기, 칸마다)
        Same(all.Take(900).ToArray(), Three, 800_000, 900_000, 400);                                       // 일부 창, 회차마다
        var changed = all.Take(900).ToArray();
        changed[10] = Out(10);                                                                              // 앞부분이 바뀐 목록
        Same(changed, Three, 0, 900_000, 50);
        Same(changed, Three.Take(2).ToArray(), 0, 900_000, 50);                                             // 대상 목록이 바뀜
    }

    [Fact]
    public void ViewFollowsNowUntilPannedAndKeepsCenterWhenZooming()
    {
        var v = new TimelineView();
        Assert.Equal((480_000L, 600_000L), v.Window(600_000));
        v.PanLeft(600_000);
        Assert.False(v.Follow);
        Assert.Equal((450_000L, 570_000L), v.Window(600_000));
        Assert.Equal((450_000L, 570_000L), v.Window(700_000));         // 따라가지 않는다
        v.ZoomOut(700_000);
        Assert.Equal("5분", v.Label);
        var (from, to) = v.Window(700_000);
        Assert.Equal(510_000L, (from + to) / 2);                      // 가운데 유지
        v.ToNow();
        Assert.Equal(700_000L, v.Window(700_000).To);
        v.ShowAll(700_000);
        Assert.Equal((0L, 700_000L), v.Window(700_000));
        v.ZoomIn(700_000);
        Assert.False(v.IsAll);
        var early = new TimelineView(5);                              // 1시간 창인데 측정은 30초
        Assert.Equal((0L, 30_000L), early.Window(30_000));
    }

    // ---------------- 경로 맵 ----------------

    private static AnalysisResult Analysis(IReadOnlyList<Episode> episodes, IReadOnlyList<RoundJudgment>? rounds = null, IReadOnlyList<string>? noBaseline = null) =>
        new(1, true, rounds ?? [], episodes, [], [], new Coverage(0, 0, 0, null, 0, 0, 0, 0), new GradeResult(Grade.Insufficient, "x", "", []),
            LinkEvidence.None, noBaseline ?? [], false);

    private static SubRun Part(Verdict v, long from, long to, CandidateKind k, string? reason = null) =>
        new(v, from, to, (int)(to - from + 1), (to - from) * 1000, null, new Candidate(k, reason));

    [Fact]
    public void LiveCandidateIsTheLastPartOfTheOngoingEpisodeOnly()
    {
        var ongoing = new Episode(90, 99, [Part(Verdict.LinkDown, 90, 94, CandidateKind.PcLink), Part(Verdict.ExternalOutage, 95, 99, CandidateKind.AfterRouter)], 9000, null);
        var judged = Enumerable.Range(0, 100).Select(i => new RoundJudgment(i, Verdict.Normal, false, [], [])).ToArray();
        Assert.Equal(MapCandidate.LocalToInternet, PathMapModel.PickCandidate(Analysis([ongoing], judged), ended: false).Candidate);
        var recovered = Enumerable.Range(0, 105).Select(i => new RoundJudgment(i, Verdict.Normal, false, [], [])).ToArray();
        Assert.Equal(MapCandidate.None, PathMapModel.PickCandidate(Analysis([ongoing], recovered), ended: false).Candidate);
        var closed = ongoing with { MaxDurationMs = 11_000 };
        var (c, note, _) = PathMapModel.PickCandidate(Analysis([closed], judged), ended: false);
        Assert.Equal((MapCandidate.None, "지금은 끊김이 관측되지 않습니다."), (c, note));
    }

    [Fact]
    public void FinishedMixedEpisodeDoesNotPickOnePlace()
    {
        var mixed = new Episode(90, 99, [Part(Verdict.LinkDown, 90, 94, CandidateKind.PcLink), Part(Verdict.ExternalOutage, 95, 99, CandidateKind.AfterRouter)], 9000, 11000);
        var shortOne = new Episode(10, 11, [Part(Verdict.ExternalOutage, 10, 11, CandidateKind.AfterRouter)], 1000, 3000);
        var (c, note, parts) = PathMapModel.PickCandidate(Analysis([shortOne, mixed]), ended: true);
        Assert.Equal(MapCandidate.None, c);
        Assert.Equal([MapCandidate.PcToLocal, MapCandidate.LocalToInternet], parts);
        Assert.Contains("먼저 이 PC 쪽 링크, 이어서 공유기 너머", note);
        var noBaseline = new Episode(10, 20, [Part(Verdict.ExternalOutage, 10, 20, CandidateKind.None, "no-local-baseline")], 10000, null);
        Assert.Equal((MapCandidate.None, "로컬 대상이 아직 한 번도 응답하지 않아 구간을 판단하지 않습니다."),
            (PathMapModel.PickCandidate(Analysis([noBaseline]), ended: true).Candidate, PathMapModel.PickCandidate(Analysis([noBaseline]), ended: true).Note));
    }

    [Fact]
    public void MapNodesAreObservationsNamedByTheUser()
    {
        var rounds = new List<RoundData> { Fine(0), Fine(1), R(2, Lost("r"), Ok("a", 20), Lost("b")), R(3, Lost("r"), Lost("a"), Lost("b")) };
        var view = PathMapModel.From(Three, rounds, "wireless", null, null, ended: false);
        Assert.Equal("우리집 공유기", view.Local.Name);                 // "공유기"라고 단정하지 않는다
        Assert.Equal(("응답 없음 2번 연속", NodeState.NotResponding), (view.Local.Detail, view.Local.State));
        Assert.True(view.LocalConfirmed);                             // 최근 5회차 안에 응답 있음
        Assert.Equal("2개 중 0개 응답", view.Internet.Detail);
        Assert.Equal("Wi‑Fi", view.Pc.Detail);
        Assert.Equal(MapCandidate.None, view.Candidate);              // 분석 전에는 칠하지 않음

        var noLocal = PathMapModel.From([ExtA, ExtB], [R(0, Ok("a", 20), Ok("b", 24))], null, null, null, ended: false);
        Assert.Equal((NodeState.NotMeasured, false), (noLocal.Local.State, noLocal.LocalConfirmed));
        var two = PathMapModel.LocalName([Router, Router with { Id = "r2", Name = "확장기" }, ExtA]);
        Assert.Equal("우리집 공유기 외 1개", two);

        var links = new LinkLive(true, LinkCapability.Supported, null,
            [new AdapterLive("a1", 7, "wired", false, LinkState.Down, 3000, 5000, 5, 0, 5, [], 0)], [], 0, false);
        var down = PathMapModel.From(Three, rounds, "wired", null, links, ended: false);
        Assert.Equal(NodeState.LinkDownObserved, down.Pc.State);
        Assert.StartsWith("개념도. 이 PC 링크 끊김 관측", down.AccessibleText);
    }

    // ---------------- 요약 ----------------

    [Fact]
    public void CardsUseTheStatisticsAndPlainSentences()
    {
        var rounds = new List<RoundData> { Fine(0), Out(1), R(2, Ok("r", 2), Err("a"), Ok("b", 30)), Fine(3) };
        var stats = StatsCalculator.Compute(rounds, Three.Select(t => t.Id).ToArray(), []);
        var cards = LiveSummary.Cards(Three, stats, rounds[^1]);
        var a = cards.Single(c => c.TargetId == "a");
        var s = stats.Targets.Single(t => t.TargetId == "a");
        Assert.Equal("3번 중 1번 응답이 없었습니다", a.LossSentence);     // 검사 오류는 분모에서 뺀다
        Assert.Equal(s.MaxConsecutiveLoss, a.MaxConsecutiveLoss);
        Assert.Equal((s.LastSuccess, s.LastLoss, 1), (a.LastSuccess, a.LastLoss, a.Errors));
        Assert.Equal("20 ms", a.Latest);
        Assert.Equal(("A", 4, false), (LiveSummary.Badge(Grade.Good).Letter, LiveSummary.Badge(Grade.Good).Fill, LiveSummary.Badge(Grade.Good).Dashed));
        Assert.Equal(("–", true), (LiveSummary.Badge(Grade.Insufficient).Letter, LiveSummary.Badge(Grade.Insufficient).Dashed));
        Assert.Equal("0.1% 미만", LiveSummary.Percent(0.0004));
        Assert.Equal("오늘 오후 1:20", LiveSummary.When(new DateTimeOffset(2026, 11, 13, 13, 20, 0, TimeSpan.FromHours(9)), Start.AddHours(8)));
        Assert.Equal("어제 오후 9:02", LiveSummary.When(new DateTimeOffset(2026, 11, 12, 21, 2, 0, TimeSpan.FromHours(9)), Start));
    }
}
