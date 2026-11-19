using System.IO;
using System.Text.Json;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.UiTests;

/// <summary>
/// M9 설계 B (G3): 기록 저장 위치 옮기기(다 옮김·일부·끊김 복구·측정 중 막힘), 진단 기본값이 검사기까지 전달됨, TCP 대상 편집.
/// </summary>
public sealed class M9SettingsTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");
    internal static string RecState(JsonElement d) => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty("records", out var r) ? r.GetProperty("state").GetString()! : "";

    private static string[] Ids(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Where(n => n!.Length > 16 && !n.Contains('.')).Select(n => n!).Order().ToArray() : [];

    private static (string UserData, string Dest, string[] Ids) Prepare(int records)
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        var ids = Enumerable.Range(0, records).Select(i => M5Fixtures.MakeRecord(userData, "steady", new DateTimeOffset(2026, 11, 10 + i, 20, 0, 0, TimeSpan.FromHours(9)), 2)).Order().ToArray();
        var dest = userData + "-dest";
        Directory.CreateDirectory(dest);
        return (userData, dest, ids);
    }

    private static void Cleanup(string userData, string dest)
    {
        AppDriver.DeleteUserData(userData);
        AppDriver.DeleteUserData(dest);
    }

    private static string SettingsText(string userData) => File.ReadAllText(Path.Combine(userData, "settings.json"));

    internal static void ConfirmMove(AppDriver app, string button)
    {
        app.WaitDiag(d => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).GetProperty("records").GetProperty("changeEnabled").GetBoolean(), b => b, "바꾸기 켜짐");
        app.Invoke(button);
        app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), b => b, "옮기기 확인");
        app.Invoke("dialog.confirm");
    }

    [Fact]
    public void MovedRecordsStayListedAndTheNextMeasurementIsSavedInTheNewFolder()
    {
        var (userData, dest, ids) = Prepare(2);
        var old = Path.Combine(userData, "records");
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "settings", "--pick-folder", dest))
            {
                ConfirmMove(app, "settings.records.change");
                var d = app.WaitDiag(x => x, x => RecState(x) is "Done" or "Failed" or "Partial" or "Invalid", "옮기기 끝");
                Assert.Equal("Done", RecState(d));
                Assert.Equal("custom", Sd(d).GetProperty("records").GetProperty("folder").GetString());
                Assert.Equal(0, Sd(d).GetProperty("records").GetProperty("extra").GetInt32());
                Assert.Equal(ids, Ids(dest));
                Assert.Empty(Ids(old));
                Assert.False(app.Diag().GetProperty("resident").GetProperty("workGate").GetBoolean());
                // 기록 화면에도 그대로 보인다
                app.Select("nav.records");
                app.WaitDiag(x => x.GetProperty("recent").GetProperty("items").GetArrayLength(), n => n == 2, "옮긴 기록 목록");
                Assert.Equal(0, app.Close());
            }
            Assert.Contains(JsonEncodedText.Encode(dest).ToString(), SettingsText(userData));
            using (var again = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "60", "--start"))
            {
                again.WaitDiag(x => x.GetProperty("rounds").GetInt32(), r => r >= 3, "3회차");
                var session = M5Fixtures.Str(again.Diag(), "session");
                again.Invoke("measure.stop");
                again.WaitDiag(x => x.GetProperty("finished").ValueKind == JsonValueKind.Object, b => b, "멈춤");
                Assert.Equal(0, again.Close());
                Assert.Contains(session, Ids(dest));                                         // 새 측정은 새 위치
                Assert.DoesNotContain(session, Ids(old));
            }
        }
        finally { Cleanup(userData, dest); }
    }

    [Fact]
    public void PartialMoveKeepsTheOldLocationListedAndRetryFinishesIt()
    {
        var (userData, dest, ids) = Prepare(2);
        var old = Path.Combine(userData, "records");
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "settings", "--pick-folder", dest, "--move-fail", "copy:" + ids[1]))
            {
                ConfirmMove(app, "settings.records.change");
                var d = app.WaitDiag(x => x, x => RecState(x) is "Done" or "Failed" or "Partial", "옮기기 끝");
                Assert.Equal("Partial", RecState(d));
                Assert.Equal(1, Sd(d).GetProperty("records").GetProperty("extra").GetInt32());
                Assert.True(Sd(d).GetProperty("records").GetProperty("retryVisible").GetBoolean());
                Assert.Equal([ids[0]], Ids(dest));
                Assert.Equal([ids[1]], Ids(old));
                app.Select("nav.records");
                app.WaitDiag(x => x.GetProperty("recent").GetProperty("items").GetArrayLength(), n => n == 2, "옛 위치 기록도 보임");
                Assert.Equal(0, app.Close());
            }
            using (var again = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "settings"))
            {
                ConfirmMove(again, "settings.records.retry");
                again.WaitDiag(RecState, s => s == "Done", "다시 옮기기 끝");
                Assert.Equal(ids, Ids(dest));
                Assert.Equal(0, Sd(again.Diag()).GetProperty("records").GetProperty("extra").GetInt32());
                Assert.Equal(0, again.Close());
            }
        }
        finally { Cleanup(userData, dest); }
    }

    [Fact]
    public void InterruptedMoveIsRecoveredOnTheNextLaunch()
    {
        var (userData, dest, ids) = Prepare(3);
        var old = Path.Combine(userData, "records");
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "settings", "--pick-folder", dest, "--move-fail", "kill-after:1"))
            {
                ConfirmMove(app, "settings.records.change");
                app.WaitKilled(TimeSpan.FromSeconds(30));                                   // 기록 하나를 옮긴 뒤 프로세스가 끝남
            }
            Assert.Equal(ids, Ids(dest).Concat(Ids(old)).Order().ToArray());                // 하나도 잃지 않았다
            using (var again = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "settings"))
            {
                var d = again.WaitDiag(x => x, x => RecState(x) == "Interrupted", "끊김 안내");
                Assert.True(Sd(d).GetProperty("records").GetProperty("retryVisible").GetBoolean());
                again.Select("nav.records");
                again.WaitDiag(x => x.GetProperty("recent").GetProperty("items").GetArrayLength(), n => n == 3, "두 위치의 기록이 모두 보임");
                again.Select("nav.settings");
                ConfirmMove(again, "settings.records.retry");
                again.WaitDiag(RecState, s => s == "Done", "다시 옮기기 끝");
                Assert.Equal(ids, Ids(dest));
                Assert.Empty(Directory.EnumerateDirectories(dest, "*" + RecordMover.MovingSuffix));
                Assert.Equal(0, again.Close());
            }
        }
        finally { Cleanup(userData, dest); }
    }

    /// <summary>
    /// G4 M9 1라운드 b1: 옮기는 중 세션 종료 확정 → 옮기기 정리(되돌림·독점 놓기)까지 한도 안에서 합류한다. 기록은 하나도 잃지 않는다.
    /// </summary>
    [Fact]
    public void SessionEndWhileMovingJoinsTheMoveCleanup()
    {
        var (userData, dest, ids) = Prepare(3);
        var old = Path.Combine(userData, "records");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "settings", "--pick-folder", dest, "--move-fail", "hold");
            ConfirmMove(app, "settings.records.change");
            app.WaitDiag(RecState, s => s == "Moving", "옮기는 중");
            app.SendSessionEnd(ending: true);
            var flushed = AppDriver.WaitFor(() => app.ReadLog().LastOrDefault(e => e.GetProperty("kind").GetString() == "sessionEnd"
                && e.GetProperty("data").GetProperty("step").GetString() == "flushed"), e => e.ValueKind == JsonValueKind.Object, "세션 종료 플러시");
            var data = flushed.GetProperty("data");
            Assert.True(data.GetProperty("done").GetBoolean(), "4초 안에 끝남");
            Assert.True(data.GetProperty("moveDone").GetBoolean(), "옮기기 정리까지 합류");
            app.TerminateAfterSessionEnd();
            Assert.Equal(ids, Ids(dest).Concat(Ids(old)).Order().ToArray());                // 하나도 잃지 않았다
            Assert.Empty(Directory.EnumerateDirectories(dest, "*" + RecordMover.MovingSuffix));
            Record("flow-settings-sessionend-move", new { done = true, ms = data.GetProperty("ms").GetInt64(), moved = Ids(dest).Length });
        }
        finally { Cleanup(userData, dest); }
    }

    [Fact]
    public void FolderCannotBeChangedWhileMeasuring()
    {
        var (userData, dest, _) = Prepare(1);
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--screen", "settings", "--pick-folder", dest);
            var d = app.WaitDiag(x => x, x => x.GetProperty("host").GetString() == "Measuring" && Sd(x).ValueKind == JsonValueKind.Object, "측정 중 설정");
            Assert.False(Sd(d).GetProperty("records").GetProperty("changeEnabled").GetBoolean());
            Assert.Contains("측정 중에는 바꿀 수 없습니다", app.Text("settings.records.status"));
            Assert.Equal(0, app.Close());
        }
        finally { Cleanup(userData, dest); }
    }

    /// <summary>
    /// C1 저장 위치 대체 (G3): 설정의 사용자 위치가 사용자 폴더 안의 쓰기 거부 폴더 → 측정은 대체 위치에 저장되고
    /// 측정 띠와 설정 카드가 알린다. 쓰기 거부는 시험이 만든 폴더에만 걸고 끝나면 푼다.
    /// </summary>
    [Fact]
    public void UnwritableFolderFallsBack()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        var locked = Path.Combine(userData, "locked-records");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(userData, "settings.json"), new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 2,
            ["records"] = new System.Text.Json.Nodes.JsonObject { ["folder"] = locked, ["extraFolders"] = new System.Text.Json.Nodes.JsonArray() },
        }.ToJsonString());
        var dir = new DirectoryInfo(locked);
        var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories
            | System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(locked, "probe.txt"), "x"));   // 정말 쓸 수 없다
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--screen", "settings");
            var d = app.WaitDiag(x => x, x => x.GetProperty("host").GetString() == "Measuring" && Sd(x).ValueKind == JsonValueKind.Object
                && !Sd(x).GetProperty("records").GetProperty("usable").GetBoolean(), "설정 카드: 쓸 수 없음");
            Assert.Contains("지금 측정은 대체 위치에 저장됩니다", app.Text("settings.records.status"));
            var session = M5Fixtures.Str(d, "session");
            app.Select("nav.measure");
            AppDriver.WaitFor(() => app.Exists("measure.banner.fallback"), b => b, "측정 띠: 대체 위치");
            app.Invoke("measure.stop");
            app.WaitDiag(x => x.GetProperty("finished").ValueKind == JsonValueKind.Object, b => b, "멈춤");
            Assert.Equal(0, app.Close());
            Assert.Contains(session, Ids(Path.Combine(userData, "demo-fallback")));
            Assert.Empty(Ids(locked));
        }
        finally
        {
            acl = dir.GetAccessControl();
            acl.RemoveAccessRuleSpecific(deny);
            dir.SetAccessControl(acl);
            AppDriver.DeleteUserData(userData);
        }
    }

    [Fact]
    public void DiagnosisDefaultsReachTheProbesOnTheNextDiagnosis()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "settings");
            string DiagState(JsonElement x) => Sd(x).GetProperty("diagnosis").GetProperty("state").GetString()!;
            app.SetText("settings.diag.domain.0", "not a domain");
            app.WaitDiag(DiagState, s => s == "dirty", "고침 (저장 켜짐)");          // 글 상자 변경 사건은 한 박자 늦게 온다
            app.Invoke("settings.diag.save");
            app.WaitDiag(DiagState, s => s == "errors", "칸 오류");
            app.SetText("settings.diag.domain.0", "check.example.com");
            app.WaitDiag(DiagState, s => s == "dirty", "다시 고침");
            app.SetText("settings.diag.domain.1", "check.example.net");
            app.Invoke("settings.diag.save");
            app.WaitDiag(x => Sd(x).GetProperty("diagnosis").GetProperty("state").GetString(), s => s == "saved", "저장");
            Assert.Contains("check.example.net", SettingsText(userData));
            app.Select("nav.diagnose");
            app.Invoke("diagnose.start");
            var d = app.WaitDiag(x => x.GetProperty("resident").GetProperty("probed"), p => p.ValueKind == JsonValueKind.Array
                && p.EnumerateArray().Any(e => e.GetString() == "dns:check.example.com"), "검사기에 전달됨");
            Assert.Contains(d.EnumerateArray(), e => e.GetString() == "dns:check.example.net");
            Record("flow-settings-diagnosis", new { probed = d.EnumerateArray().Select(e => e.GetString()) });
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void TcpTargetIsSavedMeasuredAndShown()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "profile"))
            {
                app.Select("profile.target.1.method.tcp");
                app.SetText("profile.target.1.port", "0");
                app.WaitDiag(x => Sd(x).GetProperty("errors").EnumerateArray().Any(e => e.GetString() == "profile.target.1.port"), b => b, "포트 오류");
                app.SetText("profile.target.1.port", "443");
                app.WaitDiag(x => Sd(x).GetProperty("saveEnabled").GetBoolean(), b => b, "저장 켜짐");
                app.Invoke("profile.save");
                app.WaitDiag(x => Sd(x).GetProperty("dirty").GetBoolean(), b => !b, "저장됨 (편집기는 그대로)");
                Assert.Equal(0, app.Close());
            }
            using (var again = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "60", "--start"))
            {
                again.WaitDiag(x => x.GetProperty("rounds").GetInt32(), r => r >= 3, "3회차");
                var session = M5Fixtures.Str(again.Diag(), "session");
                again.Invoke("measure.stop");
                again.WaitDiag(x => x.GetProperty("finished").ValueKind == JsonValueKind.Object, b => b, "멈춤");
                Assert.Equal(0, again.Close());
                var record = RecordReader.Read(RecordReader.FindSegments(session, M5Fixtures.RecordRoots(userData)));
                var tcp = Assert.Single(record.Header!.Targets, t => t.Method.Kind == ProbeMethodKind.Tcp);
                Assert.Equal(443, tcp.Method.Port);
                Assert.Equal("TCP 443", NetPulse.Core.Presentation.RecordView.MethodText(tcp.Method));
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
