using NetPulse.Design.Settings;

namespace NetPulse.Design.Tests;

/// <summary>설정 파일 2판과 저장 경계 (M2 설계 5.1, M9 설계 B1).</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "netpulse-settings-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string FilePath => Path.Combine(folder, SettingsFormat.FileName);

    private void WriteFile(string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, content);
    }

    [Fact]
    public void FirstRunIsDefaults()
    {
        var load = SettingsFormat.Load(folder);
        Assert.Equal(SettingsLoadStatus.Missing, load.Status);
        Assert.Equal(SettingsFile.Default, load.Settings);
        Assert.Equal(ThemePreference.Dark, load.Settings.Look.Theme);
        Assert.True(load.Settings.Resident.CloseToTray);
        Assert.True(load.Settings.Resident.NotifyDropout);
        Assert.False(load.Settings.Resident.NotifyRecovered);
        Assert.True(load.Settings.Resident.NotifyFinished);
        Assert.False(load.Settings.Resident.TrayHintShown);
        Assert.Null(load.Settings.Records.Folder);
    }

    [Fact]
    public void SavedSettingsComeBack()
    {
        var store = new SettingsStore(folder, SettingsFormat.Load(folder));
        var extra = Path.Combine(Path.GetTempPath(), "old-records");
        var result = store.Update(f => f with
        {
            Look = new AppSettings(ThemePreference.System, true, true, false),
            Resident = f.Resident with { NotifyRecovered = true, TrayHintShown = true },
            Records = new RecordsSettings(Path.Combine(Path.GetTempPath(), "rec"), [extra], null),
            Diagnosis = new DiagnosisPrefs(["192.0.2.1", "198.51.100.1"], ["example.com", "example.net"]),
        });
        Assert.Equal(SettingsSaveResult.Saved, result);
        var load = SettingsFormat.Load(folder);
        Assert.Equal(SettingsLoadStatus.Loaded, load.Status);
        Assert.Empty(load.Repaired);
        Assert.Equal(store.Current.Look, load.Settings.Look);
        Assert.Equal(store.Current.Resident, load.Settings.Resident);
        Assert.Equal(store.Current.Records.Folder, load.Settings.Records.Folder);
        Assert.Equal([extra], load.Settings.Records.ExtraFolders);
        Assert.Equal(["192.0.2.1", "198.51.100.1"], load.Settings.Diagnosis.TcpTargets);
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Contains("\"version\": 2", File.ReadAllText(FilePath));
    }

    [Fact]
    public void VersionOneMovesToLook()
    {
        WriteFile("{\"version\": 1, \"theme\": \"light\", \"aurora\": true, \"reduceEffects\": false, \"reduceMotion\": true}");
        var load = SettingsFormat.Load(folder);
        Assert.Equal(SettingsLoadStatus.Migrated, load.Status);
        Assert.Equal(new AppSettings(ThemePreference.Light, true, false, true), load.Settings.Look);
        Assert.Equal(ResidentSettings.Default, load.Settings.Resident);
        Assert.Contains("\"version\": 1", File.ReadAllText(FilePath));   // 저장은 사용자가 무엇을 바꿀 때
        Assert.True(new SettingsStore(folder, load).CanPersist);
    }

    [Fact]
    public void BadFieldIsRepairedAlone()
    {
        WriteFile("""
            {"version": 2,
             "look": {"theme": "purple", "aurora": true, "reduceEffects": "yes", "reduceMotion": false},
             "resident": {"closeToTray": false, "notifyDropout": 3},
             "records": {"folder": "relative\\path", "extraFolders": ["C:\\x", 5]},
             "diagnosis": {"tcpTargets": ["192.0.2.1", "192.0.2.1"], "domains": ["example.com", "example.net"]}}
            """);
        var load = SettingsFormat.Load(folder);
        Assert.Equal(SettingsLoadStatus.Loaded, load.Status);
        var s = load.Settings;
        Assert.Equal(ThemePreference.Dark, s.Look.Theme);
        Assert.True(s.Look.Aurora);
        Assert.False(s.Look.ReduceEffects);
        Assert.False(s.Resident.CloseToTray);                 // 맞는 칸은 그대로
        Assert.True(s.Resident.NotifyDropout);                // 틀린 칸만 기본값
        Assert.Null(s.Records.Folder);
        Assert.Equal(["C:\\x"], s.Records.ExtraFolders);
        Assert.Null(s.Diagnosis.TcpTargets);                  // 쌍 단위: 중복이면 쌍 전체 기본값
        Assert.Equal(["example.com", "example.net"], s.Diagnosis.Domains);
        Assert.Contains("look.theme", load.Repaired);
        Assert.Contains("look.reduceEffects", load.Repaired);
        Assert.Contains("resident.notifyDropout", load.Repaired);
        Assert.Contains("records.folder", load.Repaired);
        Assert.Contains("diagnosis.tcpTargets", load.Repaired);
        Assert.DoesNotContain(load.Repaired, r => r.Contains("purple") || r.Contains("192.0.2"));   // 값은 남기지 않는다
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1, 2]")]
    public void CorruptFileFallsBackAndIsKeptOnce(string content)
    {
        WriteFile(content);
        var load = SettingsFormat.Load(folder);
        Assert.Equal(SettingsLoadStatus.Corrupt, load.Status);
        Assert.Equal(SettingsFile.Default, load.Settings);
        Assert.Equal(content, File.ReadAllText(FilePath));                  // 덮어쓰지 않는다
        Assert.Equal(content, File.ReadAllText(FilePath + ".corrupt"));      // 원본 한 번 보관
        var store = new SettingsStore(folder, load);
        Assert.True(store.CanPersist);                                       // 손상은 다음 저장이 새로 쓴다
    }

    [Theory]
    [InlineData("{\"version\": 3, \"look\": {\"theme\": \"light\"}}")]
    [InlineData("{\"theme\": \"light\"}")]
    public void UnknownVersionIsNeverOverwritten(string content)
    {
        WriteFile(content);
        var load = SettingsFormat.Load(folder);
        Assert.Equal(SettingsLoadStatus.UnknownVersion, load.Status);
        Assert.Equal(SettingsFile.Default, load.Settings);
        var store = new SettingsStore(folder, load);
        Assert.False(store.CanPersist);
        Assert.False(store.RecordsWritable);
        var before = File.ReadAllBytes(FilePath);
        Assert.Equal(SettingsSaveResult.NotPersisted, store.Update(f => f with { Look = f.Look with { Theme = ThemePreference.Light } }));
        Assert.Equal(SettingsSaveResult.NotPersisted, store.Update(f => f with { Resident = f.Resident with { TrayHintShown = true } }));
        Assert.Equal(ThemePreference.Light, store.Current.Look.Theme);       // 이번 실행에는 적용
        Assert.True(store.Current.Resident.TrayHintShown);
        Assert.Equal(before, File.ReadAllBytes(FilePath));                   // 파일 바이트 그대로
    }

    [Fact]
    public void BlockedRecordsAreWrittenBackVerbatim()
    {
        const string records = "{\"folder\":null,\"extraFolders\":[],\"move\":{\"kind\":\"what\",\"weird\":1}}";
        WriteFile("{\"version\": 2, \"look\": {\"theme\": \"light\", \"aurora\": false, \"reduceEffects\": false, \"reduceMotion\": false}, \"records\": " + records + "}");
        var load = SettingsFormat.Load(folder);
        Assert.Null(load.Settings.Records.Move);
        Assert.Contains("records.move", load.Repaired);
        var store = new SettingsStore(folder, load);
        store.BlockRecords();
        Assert.False(store.RecordsWritable);
        store.Update(f => f with { Records = new RecordsSettings("C:\\elsewhere", [], null), Look = f.Look with { Aurora = true } });
        Assert.Null(store.Current.Records.Folder);                           // records 바뀜은 버림
        Assert.True(store.Current.Look.Aurora);
        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"weird\": 1", text);                               // 일지 원문 보존
        Assert.Contains("\"kind\": \"what\"", text);
    }

    [Fact]
    public void JournalRoundTrips()
    {
        var store = new SettingsStore(folder, SettingsFormat.Load(folder));
        var journal = new MoveJournal(MoveJournal.KindNew, "C:\\a", "D:\\b", ["20261118T000000Z_abcdef"],
            new Dictionary<string, string> { ["20261118T000000Z_abcdef"] = "ABC" }, MoveJournal.StepMoving, []);
        store.Update(f => f with { Records = f.Records with { Folder = "D:\\b", ExtraFolders = ["C:\\a"], Move = journal } });
        var back = SettingsFormat.Load(folder).Settings.Records.Move;
        Assert.NotNull(back);
        Assert.Equal(journal.Kind, back.Kind);
        Assert.Equal(journal.Ids, back.Ids);
        Assert.Equal("ABC", back.Hashes["20261118T000000Z_abcdef"]);
    }

    [Fact]
    public async Task ConcurrentUpdatesDoNotOverwriteEachOther()
    {
        var store = new SettingsStore(folder, SettingsFormat.Load(folder));
        var ct = TestContext.Current.CancellationToken;
        var a = Task.Run(() => { for (int i = 0; i < 50; i++) store.Update(f => f with { Records = f.Records with { ExtraFolders = [.. f.Records.ExtraFolders, "C:\\r" + i] } }); }, ct);
        var b = Task.Run(() => { for (int i = 0; i < 50; i++) store.Update(f => f with { Look = f.Look with { Aurora = i % 2 == 0 } }); }, ct);
        await Task.WhenAll(a, b);
        Assert.Equal(50, store.Current.Records.ExtraFolders.Count);          // 묶음 단위로 최신 값 위에
        Assert.Equal(50, SettingsFormat.Load(folder).Settings.Records.ExtraFolders.Count);
    }
}
