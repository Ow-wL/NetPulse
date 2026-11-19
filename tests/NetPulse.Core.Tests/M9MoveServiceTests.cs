using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Demo;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M9 설계 B3 흐름: 잠금 → 다시 판정 → 위치·일지 먼저 저장 → 옮기기 → 정리. 끊긴 뒤 복구.</summary>
public sealed class M9MoveServiceTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private sealed class FakeSettings : IRecordFolderSettings
    {
        public RecordFolders Current = new(null, [], null);
        public readonly List<RecordFolders> Saves = [];
        public Action<RecordFolders>? OnSave;
        public bool Writable { get; set; } = true;
        public RecordFolders Read() => Current;
        public bool Save(RecordFolders next) { OnSave?.Invoke(next); Saves.Add(next); Current = next; return true; }
    }

    private static readonly Target[] Three =
    [
        new("r", "R", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
        new("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        new("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "Y", ProbeMethod.Icmp),
    ];

    private static void Make(string root, string id)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        File.WriteAllText(Path.Combine(folder, "measurement.json"),
            RecordSchema.HeaderJson(new RecordHeader(id, 0, "1.0", Start, 540, Three, 1000, 800, 600_000, "primary", ExpectedConnection.Wired)));
        File.WriteAllLines(Path.Combine(folder, "rounds.jsonl"), Enumerable.Range(0, 10).Select(r => RecordSchema.RoundLine(id, new RoundData(r, r * 1000, r * 1000 + 3,
            Start.AddSeconds(r), 0, false, [new("r", ProbeOutcome.Success, 2, null), new("a", ProbeOutcome.Success, 20, null), new("b", ProbeOutcome.Success, 24, null)], r * 1000 + 50))));
        File.WriteAllText(Path.Combine(folder, "summary.json"), RecordSchema.SummaryJson(
            new RecordSummary(id, EndReason.Stopped, Start.AddSeconds(10), 10, 10, 0, 0, 0, 9, false, null, 0, 10_000)));
    }

    private (RecordMoveService Service, DemoAppWorld World, FakeSettings Settings, RecordWorkGate Gate, string UserData) New()
    {
        var userData = dir.Sub("u");
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json"));
        var world = new DemoAppWorld(new DemoWorld(Scenario.Parse(text), new DemoTimeBase(Start, 600)), userData, new DemoRunRules(), dir.Sub("fb"));
        var gate = new RecordWorkGate();
        var host = new MeasurementHost(world, userData, new HostOptions { WorkGate = gate });
        var settings = new FakeSettings();
        return (new RecordMoveService(world, gate, host, settings, _ => null), world, settings, gate, userData);
    }

    private static string Id(int n) => $"20261113T0{n}0000Z_abcde{n}";

    [Fact]
    public async Task LocationIsSavedBeforeAnyFileMovesAndCleanedAfter()
    {
        var (svc, world, settings, gate, _) = New();
        var old = world.CurrentRecordFolder;
        Make(old, Id(1)); Make(old, Id(2));
        var dest = dir.Sub("new");
        settings.OnSave = next =>
        {
            if (settings.Saves.Count == 0)
            {
                Assert.True(gate.Closed);                                           // 잠근 뒤에만
                Assert.Equal(2, Directory.EnumerateDirectories(old).Count());       // 아직 아무것도 옮기지 않았다
                Assert.Equal(dest, next.Folder);
                Assert.Contains(old, next.Extra);
                Assert.NotNull(next.Move);
            }
        };
        var r = await svc.RunAsync(MoveKind.New, dest, null, TestContext.Current.CancellationToken);
        Assert.Equal(MoveState.Done, r.State);
        Assert.Equal(2, r.Moved);
        Assert.Equal(dest, settings.Current.Folder);
        Assert.Empty(settings.Current.Extra);                                       // 원본이 비면 옛 위치에서 뺀다
        Assert.Null(settings.Current.Move);
        Assert.Equal(dest, world.CurrentRecordFolder);
        Assert.False(gate.Closed);
        Assert.Equal(2, RecordCatalog.List(world.RecordPaths, 10, null).Count);
    }

    [Fact]
    public async Task ZeroRecordsStillPersistsTheLocation()
    {
        var (svc, world, settings, _, _) = New();
        var dest = dir.Sub("empty-new");
        var r = await svc.RunAsync(MoveKind.New, dest, null, TestContext.Current.CancellationToken);
        Assert.Equal(MoveState.Done, r.State);
        Assert.Equal(dest, settings.Current.Folder);
        Assert.Equal(dest, world.CurrentRecordFolder);
    }

    [Fact]
    public async Task InterruptedMoveIsRecoveredAndNothingIsLost()
    {
        var (svc, world, settings, _, _) = New();
        var old = world.CurrentRecordFolder;
        Make(old, Id(1)); Make(old, Id(2)); Make(old, Id(3));
        var dest = dir.Sub("new");
        svc.AfterRecordForTest = n => { if (n == 1) throw new OperationCanceledException("프로세스가 끝남"); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => svc.RunAsync(MoveKind.New, dest, null, TestContext.Current.CancellationToken));
        // 다음 실행: 설정에 남은 위치·일지로 복구
        var f = settings.Current;
        Assert.NotNull(f.Move);
        var (interrupted, problem) = RecordMoveService.Recover(f, f.Folder!);
        Assert.True(interrupted);
        Assert.Null(problem);
        world.SetRecordFolders(f.Folder, f.Extra);
        Assert.Equal(3, RecordCatalog.List(world.RecordPaths, 10, null).Count);
        // 다시 옮기기: 남은 원본을 지금 위치로
        svc.AfterRecordForTest = null;
        var r = await svc.RunAsync(MoveKind.Retry, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(MoveState.Done, r.State);
        Assert.Empty(settings.Current.Extra);
        Assert.Equal(3, Directory.EnumerateDirectories(dest).Count());
    }

    [Fact]
    public async Task ChangesBetweenPreviewAndLockAreJudgedAfterTheLock()
    {
        var (svc, world, settings, _, _) = New();
        var old = world.CurrentRecordFolder;
        var dest = dir.Sub("new");
        Make(old, Id(1));
        Make(dest, Id(1));                                                          // 같은 내용
        Assert.Null(svc.Preview(MoveKind.New, dest).Reason);
        File.AppendAllText(Path.Combine(old, Id(1), "notes.jsonl"), "{\"memo\":\"새 글\"}\n");   // 확인 대화상자 동안 메모가 저장됨
        var r = await svc.RunAsync(MoveKind.New, dest, null, TestContext.Current.CancellationToken);
        Assert.Equal(MoveState.Conflict, r.State);                                  // 잠근 뒤 해시가 다르다 → 아무것도 바꾸지 않음
        Assert.Empty(settings.Saves);
        Assert.True(File.Exists(Path.Combine(old, Id(1), "notes.jsonl")));
    }

    [Fact]
    public async Task NotWritableSettingsRefuse()
    {
        var (svc, world, settings, _, _) = New();
        settings.Writable = false;
        Make(world.CurrentRecordFolder, Id(1));
        var r = await svc.RunAsync(MoveKind.New, dir.Sub("new"), null, TestContext.Current.CancellationToken);
        Assert.Equal(MoveState.NotPersisted, r.State);
        Assert.Single(Directory.EnumerateDirectories(world.CurrentRecordFolder));
    }

    [Fact]
    public async Task DefaultKindMovesBackIntoTheUserFolder()
    {
        var (svc, world, settings, _, _) = New();
        var custom = dir.Sub("custom");
        Make(custom, Id(1));
        settings.Current = new RecordFolders(custom, [], null);
        world.SetRecordFolders(custom, []);
        var r = await svc.RunAsync(MoveKind.Default, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(MoveState.Done, r.State);
        Assert.Null(settings.Current.Folder);                                       // 기본 위치 = null
        Assert.True(Directory.Exists(Path.Combine(world.DefaultRecordFolder, Id(1))));
    }

    [Fact]
    public void InvalidJournalIsReportedNotActedOn()
    {
        var to = dir.Sub("to");
        var moving = Directory.CreateDirectory(Path.Combine(to, Id(1) + RecordMover.MovingSuffix)).FullName;
        File.WriteAllText(Path.Combine(moving, RecordMover.Marker), "");
        var f = new RecordFolders(to, [], new MoveLog("new", dir.Sub("from"), to, [Id(1)], new Dictionary<string, string>(), []));   // from이 옛 위치에 없음
        var (interrupted, problem) = RecordMoveService.Recover(f, to);
        Assert.True(interrupted);
        Assert.Equal("from", problem);
        Assert.True(Directory.Exists(moving));                                      // 자동 정리하지 않는다
    }
}
