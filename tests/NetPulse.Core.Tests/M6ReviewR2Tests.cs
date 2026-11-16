using NetPulse.Core.Demo;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;

namespace NetPulse.Core.Tests;

/// <summary>M6 G4 2라운드 지적 1·2 (명령 시작 승인과 종료의 직렬화, 저장 확정과 종료의 직렬화).</summary>
public sealed class M6ReviewR2Tests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 13, 20, 0, TimeSpan.FromHours(9));
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private static Scenario Load(string name) =>
        Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json")));

    private static DemoWorld Frozen(Scenario s, long round)
    {
        var tb = new DemoTimeBase(Start, 1);
        tb.Freeze(TimeSpan.FromSeconds(round));
        return new DemoWorld(s, tb);
    }

    private static DiagnosisSettings Fast(Scenario s) => DemoDiagnosticProbes.Settings(s) with
    {
        AdapterTimeout = TimeSpan.FromMilliseconds(300), GatewayTimeout = TimeSpan.FromMilliseconds(300), TcpTimeout = TimeSpan.FromMilliseconds(300),
        DnsTimeout = TimeSpan.FromMilliseconds(300), HttpsTimeout = TimeSpan.FromMilliseconds(300), PortalTimeout = TimeSpan.FromMilliseconds(300),
        LinkTimeout = TimeSpan.FromMilliseconds(300),
    };

    /// <summary>명령이 시작된 "안"에서 종료가 들어오는 동작 (승인 뒤 종료).</summary>
    private sealed class ShutdownInside(Func<DiagnosisHost> host) : ISystemActions
    {
        public int Flushes;
        public Task? Shutdown;

        public async Task<SystemActionResult> FlushDnsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Flushes);
            Shutdown = host().ShutdownAsync();
            // 종료가 잠금을 잡고 끝날 때까지 기다린 뒤 성공으로 끝난다 (이미 시작한 명령은 끝까지)
            var limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (host().StartDiagnosis() != DiagnosisRequest.Closed && DateTime.UtcNow < limit) await Task.Delay(10, CancellationToken.None);
            return new(true, null);
        }

        public SystemActionResult OpenSettings(SettingsPage page) => new(true, null);
        public SystemActionResult OpenFolder(string folder, IReadOnlyList<string> allowedRoots) => new(true, null);
    }

    [Fact]
    public async Task ShutdownAfterTheCommandWasApprovedLetsOnlyThatOneRunAndNothingFollows()
    {
        var s = Load("steady");
        var folder = dir.Sub("d");
        DiagnosisHost? host = null;
        var actions = new ShutdownInside(() => host!);
        host = new DiagnosisHost(new DiagnosisWorld(new DemoDiagnosticProbes(Frozen(s, 10)), Fast(s), actions), new DiagnosisStore(folder), () => null, () => Start, "test");
        int published = 0, afterClose = -1;
        host.Changed += () => Interlocked.Increment(ref published);
        Assert.Equal(DiagnosisRequest.Started, host.RequestFlush());
        await Eventually(() => actions.Shutdown is not null, "명령 안의 종료");
        await actions.Shutdown!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        afterClose = Volatile.Read(ref published);
        await Task.Delay(500, Ct);
        Assert.Equal(1, Volatile.Read(ref actions.Flushes));            // 승인된 하나만 실행
        Assert.Equal(afterClose, Volatile.Read(ref published));         // 종료 뒤 게시 없음
        Assert.Empty(Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : []);   // 다시 진단·저장하지 않음
    }

    [Fact]
    public async Task CommitChecksAndMovesUnderTheGivenLockOnEveryAttempt()
    {
        var s = Load("steady");
        var folder = dir.Sub("c");
        var store = new DiagnosisStore(folder);
        var result = await QuickDiagnosis.RunAsync(new DemoDiagnosticProbes(Frozen(s, 10)), Fast(s), () => Start, Ct);
        var gate = new object();
        int checks = 0, unlocked = 0;
        bool Wanted()
        {
            checks++;
            if (!Monitor.IsEntered(gate)) unlocked++;
            return true;
        }
        var first = store.Save(result, "t", null, Wanted, gate);
        Assert.NotNull(first.File);
        Assert.Equal(1, checks);
        Assert.Equal(0, unlocked);

        // 원하지 않게 되면 확정하지 않고 임시 파일도 남기지 않는다
        var refused = store.Save(result, "t", null, () => false, gate);
        Assert.Null(refused.File);
        Assert.Equal("cancelled", refused.Error);
        Assert.Single(Directory.GetFiles(folder));
    }

    private static async Task Eventually(Func<bool> done, string what)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException(what);
            await Task.Delay(20, Ct);
        }
    }
}
