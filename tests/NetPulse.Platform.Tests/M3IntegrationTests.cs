using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetPulse.Core.Demo;
using NetPulse.Core.Detection;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;

namespace NetPulse.Platform.Tests;

/// <summary>
/// M3 G3 대체: 화면 없는 통합 시나리오 (M3 설계 10절). 외부 네트워크 응답에 의존하지 않는다
/// (루프백·문서용 주소·데모만). 주소·어댑터 이름은 출력하지 않는다.
/// </summary>
public sealed class M3IntegrationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("np-g3-").FullName;
    private static readonly TargetResult[] NoResults = [];

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private (DiskRecordRoot Primary, DiskRecordRoot Fallback) Roots() =>
        (new DiskRecordRoot(Path.Combine(root, "primary"), "primary"), new DiskRecordRoot(Path.Combine(root, "fallback"), "fallback"));

    private MeasurementRecord Reread(string id) =>
        RecordReader.Read(RecordReader.FindSegments(id, [Path.Combine(root, "primary"), Path.Combine(root, "fallback")]));

    private static void SameRounds(IReadOnlyList<RoundData> expected, IReadOnlyList<RoundData> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i] with { Results = NoResults, Wall = default }, actual[i] with { Results = NoResults, Wall = default });
            Assert.Equal(RecordSchema.Iso(expected[i].Wall), RecordSchema.Iso(actual[i].Wall));
            Assert.Equal(expected[i].Results.Select(r => r with { RttMs = r.RttMs is double v ? Math.Round(v, 3) : null }), actual[i].Results);
        }
    }

    [Fact]
    public async Task RealClockIcmpRoutesAndFilesSurviveStopAndReread()
    {
        var (primary, fallback) = Roots();
        var profile = new Profile("g3", "G3", [
            new Target("loopback", "루프백", IPAddress.Loopback, TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("doc", "문서용 주소", IPAddress.Parse("192.0.2.1"), TargetRole.External, "TEST-NET-1", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Any);
        var session = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = new SystemClock(), ProbeFor = t => ProbeFactory.For(t, profile.Timeout),
            Primary = primary, Fallback = fallback, Routes = new RouteObserver(), Power = new PowerEvents(), AppVersion = "g3",
        });
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var result = await session.StopAsync();

        Assert.Equal(EndReason.Stopped, result.EndReason);
        Assert.False(result.SealFailed);
        Assert.Equal(0, result.Unsaved);
        var rec = Reread(session.Id);
        Assert.Equal(RecordStatus.Ok, rec.Status);
        Assert.Equal(EndReason.Stopped, rec.EndReason);
        Assert.InRange(rec.Rounds.Count, 3, 7);
        SameRounds(session.ObservedRounds, rec.Rounds);
        Assert.Equal(result.Summary!.Observed, rec.Rounds.Count);
        // 경로 이벤트: 시작 때 대상마다 하나 이상 (값이든 실패 상태든)
        var routes = rec.Events.Where(e => (string?)e["type"] == "route").ToArray();
        foreach (var id in new[] { "loopback", "doc" })
        {
            var first = Assert.Single(routes, e => (string?)e["target"] == id && (long?)e["round"] == rec.Rounds[0].Round);
            Assert.Contains((string?)first["status"], new[] { "ok", "failed" });
            if ((string?)first["status"] == "ok") Assert.NotNull(first["interfaceIndex"]);
            else Assert.NotNull(first["code"]);
        }
        // 루프백은 성공이거나 명시적 검사 오류 (손실로 꾸미지 않음)
        Assert.All(rec.Rounds.Where(r => !r.Gap), r => Assert.NotEqual(ProbeOutcome.Lost, r.Results.Single(x => x.TargetId == "loopback").Outcome));
    }

    [Fact]
    public async Task DemoOutageGoesThroughRealFilesAndRecoversFromTruncation()
    {
        var scenario = Scenario.Load(Path.Combine(AppContext.BaseDirectory, "scenarios", "outage.json"));
        var demo = new DemoProbe(scenario);
        var (primary, fallback) = Roots();
        var profile = new Profile("demo", "데모",
            scenario.ProbeTargets.Select(t => new Target(t.Id, t.Id, t.Address, t.Role == "local" ? TargetRole.Local : TargetRole.External, t.Id, ProbeMethod.Icmp)).ToArray(),
            Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Any);
        var session = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = new ScaledClock(20, scenario.StartAt ?? DateTimeOffset.UnixEpoch), ProbeFor = _ => demo,
            Primary = primary, Fallback = fallback,
        });
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);

        var rec = Reread(session.Id);
        Assert.Equal(RecordStatus.Ok, rec.Status);
        SameRounds(session.ObservedRounds, rec.Rounds);
        foreach (var r in rec.Rounds)
            foreach (var x in r.Results)
                Assert.Equal(demo.Resolve(x.TargetId, r.Round).Outcome, x.Outcome);
        // 시나리오의 외부 단절(20~26회차)이 손실 구간으로 나온다
        var extA = rec.Stats!.Targets.Single(t => t.TargetId == "ext-a");
        Assert.Contains(extA.Runs, run => run.FirstRound >= 20 && run.LastRound <= 26);
        Assert.Equal(0, rec.Stats.Targets.Single(t => t.TargetId == "gateway").Lost);

        // 끝을 반쯤 자르고 요약을 지운다: 앱이 죽은 기록처럼 읽혀야 한다
        var folder = Path.Combine(root, "primary", session.Id);
        var rounds = Path.Combine(folder, "rounds.jsonl");
        var bytes = File.ReadAllBytes(rounds);
        File.WriteAllBytes(rounds, bytes[..(bytes.Length - 15)]);
        File.Delete(Path.Combine(folder, "summary.json"));
        var cut = Reread(session.Id);
        Assert.Equal(RecordStatus.Damaged, cut.Status);
        Assert.Equal(1, cut.Damage.TruncatedTail);
        Assert.Equal(EndReason.Interrupted, cut.EndReason);
        Assert.Equal(rec.Rounds.Count - 1, cut.Rounds.Count);
        SameRounds(rec.Rounds.Take(rec.Rounds.Count - 1).ToArray(), cut.Rounds);
    }

    [Fact]
    public async Task TcpProbeOnLoopback()
    {
        var token = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int open = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = Task.Run(async () => { try { using var c = await listener.AcceptTcpClientAsync(token); } catch (Exception) { } }, token);
        var target = new ProbeTarget("lo", "local", IPAddress.Loopback);
        try
        {
            var ok = await new TcpProbe(TimeSpan.FromMilliseconds(800), open).ProbeAsync(target, 0, token);
            Assert.Equal(ProbeOutcome.Success, ok.Outcome);
            Assert.NotNull(ok.RttMs);
        }
        finally { listener.Stop(); }

        // 닫힌 포트: 거부(RST) = 성공, 그리고 제한시간보다 훨씬 빨리 온다 (SYN 재전송 끔)
        var watch = Stopwatch.StartNew();
        var refused = await new TcpProbe(TimeSpan.FromMilliseconds(800), open).ProbeAsync(target, 1, token);
        Assert.Equal(ProbeOutcome.Success, refused.Outcome);
        Assert.True(watch.ElapsedMilliseconds < 600, $"거부가 {watch.ElapsedMilliseconds} ms 걸림");

        // 응답 없는 문서용 주소: 시간 초과 = 손실 (경로가 아예 없는 러너면 검사 오류)
        var silent = await new TcpProbe(TimeSpan.FromMilliseconds(300), 9).ProbeAsync(new ProbeTarget("doc", "external", IPAddress.Parse("192.0.2.1")), 2, token);
        Assert.Contains(silent.Outcome, new[] { ProbeOutcome.Lost, ProbeOutcome.Error });
        await accepting;
    }

    [Fact]
    public void ThisPcDetectionGivesDefinedStateWithoutPrintingAddresses()
    {
        var snapshot = NetworkInfo.Snapshot(DetectionPlanner.RouteProbeAddress);
        var result = DetectionPlanner.Plan(snapshot);
        Assert.True(Enum.IsDefined(result.Status));
        if (result.Proposal is { } p) Assert.False(ProfileValidator.HasErrors(ProfileValidator.Validate(p)));
        else Assert.Equal(DetectionStatus.NoNetwork, result.Status);
        // 출력은 상태·유형·개수만
        TestContext.Current.TestOutputHelper?.WriteLine($"탐지: {result.Status}, 유형 {result.Kind}, 가상 {result.Virtual}, 다른 게이트웨이 {result.OtherGateways}");
    }

    [Fact]
    public void RouteObserverReportsLoopbackInterface()
    {
        var observer = new RouteObserver();
        ProbeTarget[] lo = [new ProbeTarget("lo", "local", IPAddress.Loopback)];
        var s = Assert.Single(observer.Observe(lo, 1));
        Assert.True(s.Ok, "루프백 경로 조회는 성공해야 한다: " + s.Error);
        Assert.NotNull(s.InterfaceIndex);
        Assert.Equal("loopback", s.Kind);                 // 처음 본 인터페이스는 30회차 주기와 상관없이 유형을 조회한다
        Assert.Null(observer.Observe(lo, 2)[0].Kind);     // 같은 인터페이스면 유형은 30회차마다
        Assert.Equal("loopback", observer.Observe(lo, 30)[0].Kind);
    }

    [Fact]
    public void PowerEventsSubscribeAndUnsubscribe()
    {
        using var subscription = new PowerEvents().Subscribe(() => { }, () => { });
        Assert.NotNull(subscription);
    }

    [Fact]
    public void FlushToDiskCostPerRoundIsMeasured()
    {
        // 회차마다 디스크 flush 비용 (M3 설계 6.5, 11절). 기준: p95 50 ms 이하.
        // 관측 구간 (M10 설계 D2 [1-7][2-9]): 앞 20번은 데우기로 버리고, 미리 정한 900번의 p95로 한 번 판정한다 (재시도 없음).
        // M3의 "첫 쓰기를 포함한 300번"에서 바뀌었다: 300개 p95는 느린 표본 15개로 넘어, 짧은 디스크 지연 묶음에 판정이 흔들렸다
        // (M9 PR CI p95 86 ms, 같은 커밋 push CI 통과 — 실행기 원인은 추정). 100개 구간마다의 p95·최대를 함께 남긴다 (누적 아님).
        const int warmup = 20, samples = 900, window = 100;
        var folder = new DiskRecordFolder(Path.Combine(root, "flush"), "primary");
        var line = RecordSchema.RoundLine("flush-test", new RoundData(0, 0, 0, DateTimeOffset.UnixEpoch, 0, false,
            [new TargetResult("a", ProbeOutcome.Success, 1.234, null), new TargetResult("b", ProbeOutcome.Lost, null, null), new TargetResult("c", ProbeOutcome.Success, 20.5, null)]));
        var times = new List<double>();
        using (var w = new LineAppender(folder, "rounds"))
            for (int i = 0; i < warmup + samples; i++)
            {
                var watch = Stopwatch.StartNew();
                Assert.True(w.TryAppend(line, out var failure), failure is null ? null : LineAppender.Reason(failure));
                if (i >= warmup) times.Add(watch.Elapsed.TotalMilliseconds);
            }
        static double P95(List<double> xs) { var s = xs.Order().ToList(); return s[(int)Math.Ceiling(0.95 * s.Count) - 1]; }
        var windows = new JsonArray();
        for (int start = 0; start < times.Count; start += window)
        {
            var part = times.Skip(start).Take(window).ToList();
            windows.Add(new JsonObject { ["from"] = start, ["p95Ms"] = Math.Round(P95(part), 3), ["maxMs"] = Math.Round(part.Max(), 3) });
        }
        double p95 = P95(times), max = times.Max();
        TestContext.Current.TestOutputHelper?.WriteLine($"flush p95 {p95:F2} ms, 최대 {max:F2} ms (데우기 {warmup}, 표본 {samples})");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "flush-cost.json"),
            new JsonObject { ["p95Ms"] = Math.Round(p95, 3), ["maxMs"] = Math.Round(max, 3), ["count"] = times.Count, ["warmup"] = warmup, ["windows"] = windows }.ToJsonString());
        Assert.True(p95 <= 50, $"flush p95 {p95:F2} ms > 50 ms");
    }
}
