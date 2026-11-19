using NetPulse.Core.Demo;
using NetPulse.Core.Detection;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M9 설계 A1 종료 합류 [1-3]: 시작 중·멈추는 중에 종료해도 끝까지 기다리고, 종료 뒤에는 시작하지 않는다. 게시 사건 [2-5].</summary>
public sealed class M9HostShutdownTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    /// <summary>데모 세계를 감싸 시작을 문으로 막을 수 있게 한다.</summary>
    private sealed class GatedWorld(IAppWorld inner) : IAppWorld
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new(true);
        public string Kind => inner.Kind;
        public DateTimeOffset Now => inner.Now;
        public IReadOnlyList<string> RecordPaths => inner.RecordPaths;
        public string DefaultRecordFolder => inner.DefaultRecordFolder;
        public string CurrentRecordFolder => inner.CurrentRecordFolder;
        public string FallbackRecordFolder => inner.FallbackRecordFolder;
        public void SetRecordFolders(string? folder, IReadOnlyList<string> extra) => inner.SetRecordFolders(folder, extra);
        public DiagnosisWorld Diagnostics => inner.Diagnostics;
        public Task<DetectionResult> DetectAsync(CancellationToken cancellationToken) => inner.DetectAsync(cancellationToken);
        public MeasurementSession StartSession(Profile profile, string appVersion)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return inner.StartSession(profile, appVersion);
        }
    }

    private async Task<(MeasurementHost Host, GatedWorld World)> NewHost(string scenario = "steady", RecordWorkGate? workGate = null)
    {
        var data = dir.Sub("u");
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", scenario + ".json"));
        var demo = new DemoAppWorld(new DemoWorld(Scenario.Parse(text), new DemoTimeBase(Start, 600)), data, new DemoRunRules(), dir.Sub("fb"));
        var world = new GatedWorld(demo);
        var host = new MeasurementHost(world, data, new HostOptions { PollInterval = TimeSpan.FromMilliseconds(20), AnalysisInterval = TimeSpan.FromMilliseconds(50), WorkGate = workGate });
        var detection = await host.DetectAsync(TestContext.Current.CancellationToken);
        host.SaveProfiles(new ProfileSet([detection.Proposal!], detection.Proposal!.Id));
        return (host, world);
    }

    /// <summary>
    /// G4 M9 1라운드 a1: 시작은 작업 문의 표를 받고 측정 중 게시까지 쥔다. 옮기기는 그 표가 끝나기를 기다려야 해서
    /// 시작 확인과 세션 생성 사이에 끼어들지 못하고, 독점을 얻은 뒤 호스트 상태(측정 중)를 보고 거절한다.
    /// </summary>
    [Fact]
    public async Task StartHoldsAWorkTicketSoAMoveCannotSlipIn()
    {
        var gate = new RecordWorkGate();
        var (host, world) = await NewHost(workGate: gate);
        world.Release.Reset();
        var start = host.StartAsync();
        Assert.True(world.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, gate.Active);                                         // 세션을 만드는 동안 표를 쥐고 있다
        Assert.False(await gate.EnterExclusiveAsync(TimeSpan.FromMilliseconds(200)));   // 그 사이 독점은 기다리다 되돌린다
        world.Release.Set();
        Assert.True(await start);
        Assert.Equal(0, gate.Active);
        Assert.Equal(HostState.Measuring, host.Snapshot.State);
        Assert.True(await gate.EnterExclusiveAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(HostState.Measuring, host.Snapshot.State);              // 옮기기는 여기서 측정 중을 보고 거절한다
        gate.ExitExclusive();
        // 문이 닫혀 있으면 시작하지 않는다
        await host.ShutdownAsync();
        Assert.True(await gate.EnterExclusiveAsync(TimeSpan.FromSeconds(5)));
        var (host2, _) = await NewHost(workGate: gate);
        Assert.False(await host2.StartAsync());
        Assert.NotNull(host2.Snapshot.StartError);
        gate.ExitExclusive();
    }

    [Fact]
    public async Task ShutdownWhileStartingWaitsForTheStartThenStopsAndSaves()
    {
        var (host, world) = await NewHost();
        world.Release.Reset();
        var start = host.StartAsync();
        Assert.True(world.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(HostState.Starting, host.Snapshot.State);
        var shutdown = host.ShutdownAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(shutdown.IsCompleted);                       // 시작이 정해지기 전에는 끝나지 않는다 (예전: StopAsync가 바로 돌아옴)
        world.Release.Set();
        Assert.True(await start);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var s = host.Snapshot;
        Assert.Equal(HostState.Idle, s.State);
        Assert.NotNull(s.Finished);
        Assert.Equal(EndReason.Stopped, s.Finished!.Result.EndReason);
        Assert.False(s.Finished.Result.SealFailed);
    }

    [Fact]
    public async Task ShutdownWhileStoppingJoinsTheStop()
    {
        var (host, _) = await NewHost();
        Assert.True(await host.StartAsync());
        await EventuallyWithin(() => host.Snapshot.Rounds.Count >= 3, "3회차", TimeSpan.FromSeconds(30));
        var stop = host.StopAsync();
        var shutdown = host.ShutdownAsync();
        await Task.WhenAll(stop, shutdown).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(HostState.Idle, host.Snapshot.State);
        Assert.NotNull(host.Snapshot.Finished);
    }

    [Fact]
    public async Task NoStartAfterShutdownBegins()
    {
        var (host, _) = await NewHost();
        await host.ShutdownAsync();
        Assert.True(host.StartsBlocked);
        Assert.False(await host.StartAsync());
        Assert.Equal(HostState.Idle, host.Snapshot.State);
        host.UnblockStarts();                                     // 세션 종료 취소
        Assert.True(await host.StartAsync());
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task EveryPublishedSnapshotIsDeliveredInVersionOrderPerThread()
    {
        var (host, _) = await NewHost();
        var seen = new List<long>();
        host.Published += s => { lock (seen) seen.Add(s.Version); };
        Assert.True(await host.StartAsync());
        await EventuallyWithin(() => host.Snapshot.Rounds.Count >= 5, "5회차", TimeSpan.FromSeconds(30));
        await host.ShutdownAsync();
        long[] versions;
        lock (seen) versions = [.. seen];
        Assert.NotEmpty(versions);
        Assert.Equal(versions.Length, versions.Distinct().Count());      // 같은 장면을 두 번 주지 않는다
        Assert.Equal(host.Snapshot.Version, versions.Max());             // 마지막 장면까지 왔다
        Assert.Contains(versions, v => v > 0);
    }
}
