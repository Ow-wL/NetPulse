using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NetPulse.Core.Records;

namespace NetPulse.UiTests;

/// <summary>
/// M9 설계 A (G3): 측정 중 창 닫기 = 트레이 숨김, 처음 안내, 트레이 메뉴(처리기 시험), 단일 실행, <c>--exit</c>, 풍선, 세션 종료.
/// 트레이 콜백은 Shell을 거치지 않고 수신 창에 v4 형식으로 보낸다(처리기 시험, A7-1). 숨긴 동안 UIA로 진단 값을 못 읽어 앱 기록을 읽는다.
/// </summary>
public sealed class M9ResidentTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static int Rounds(JsonElement d) => d.GetProperty("rounds").GetInt32();
    private static string Host(JsonElement d) => M5Fixtures.Str(d, "host");
    private static JsonElement Resident(JsonElement d) => d.GetProperty("resident");

    private static MeasurementRecord ReadRecord(string userData, string sessionId) =>
        RecordReader.Read(RecordReader.FindSegments(sessionId, M5Fixtures.RecordRoots(userData)));

    /// <summary>처음 안내를 이미 본 설정 (안내 대화상자를 건너뛰는 시험).</summary>
    private static void HintAlreadyShown(string userData)
    {
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "settings.json"),
            """{"version": 2, "resident": {"closeToTray": true, "notifyDropout": true, "notifyRecovered": false, "notifyFinished": true, "trayHintShown": true}}""");
    }

    private static IEnumerable<JsonElement> ResidentLog(AppDriver app, string what) =>
        app.ReadLog().Where(e => e.GetProperty("kind").GetString() == "resident" && e.GetProperty("data").TryGetProperty("what", out var w) && w.GetString() == what);

    private static void WaitHidden(AppDriver app)
    {
        AppDriver.WaitFor(() => !app.WindowVisible && app.Alive, b => b, "창 숨김, 프로세스 살아 있음");
        AppDriver.WaitFor(() => ResidentLog(app, "hidden").Count(), n => n >= 1, "숨김 기록");
    }

    private static string Session(AppDriver app) => M5Fixtures.Str(app.Diag(), "session");

    [Fact]
    public void CloseWhileMeasuringShowsHintOnceThenHidesAndReopensTheSameMeasurement()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            // 인수 주입 없이: 설정 파일 기본값(측정 중 닫기 = 트레이)을 그대로 검증 (A7, [1-16])
            using var app = AppDriver.LaunchRaw(userData, "--demo", "steady", "--demo-speed", "20", "--start");
            app.WaitDiag(Rounds, r => r >= 5, "5회차");
            var session = Session(app);
            Assert.Equal("added", M5Fixtures.Str(Resident(app.Diag()), "trayState"));
            app.RequestClose();
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), b => b, "처음 안내 대화상자");
            Assert.True(app.Exists("dialog.alternate"));
            app.Invoke("dialog.confirm");                      // [트레이에서 계속]
            WaitHidden(app);
            Thread.Sleep(2000);
            app.PostTray(AppDriver.NinSelect);                 // 왼쪽 클릭 = 열기
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "다시 보임");
            var d = app.WaitDiag(x => x, x => Rounds(x) >= 5 && !Resident(x).GetProperty("hidden").GetBoolean(), "다시 연 장면");
            Assert.Equal(session, M5Fixtures.Str(d, "session"));            // 같은 측정
            Assert.Equal("Measuring", Host(d));
            Assert.Equal(0, Resident(d).GetProperty("suspended").GetInt32()); // 숨김 계약 풀림
            Assert.True(Resident(d).GetProperty("hintShown").GetBoolean());
            var reopened = Rounds(d);
            // 두 번째 닫기: 안내 없이 바로 숨김
            app.RequestClose();
            WaitHidden(app);
            Assert.Single(ResidentLog(app, "hint"));
            var (second, first) = app.Exit();
            Assert.Equal((0, 0), (second, first));
            var record = ReadRecord(userData, session);
            Assert.Equal(EndReason.Stopped, record.EndReason);
            Assert.True(record.Rounds.Count > reopened, "숨긴 동안에도 회차가 쌓였다");
            Record("flow-resident-hide", new { session, reopened, saved = record.Rounds.Count });
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void HintStopAndExitSavesAndCancelKeepsTheWindow()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchRaw(userData, "--demo", "steady", "--demo-speed", "20", "--start");
            app.WaitDiag(Rounds, r => r >= 3, "3회차");
            app.RequestClose();
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), b => b, "안내");
            app.Invoke("dialog.cancel");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), b => !b, "취소로 닫힘");
            Assert.True(app.WindowVisible);
            Assert.Equal("Measuring", Host(app.Diag()));
            Assert.False(Resident(app.Diag()).GetProperty("hintShown").GetBoolean());   // 취소는 아무것도 바꾸지 않는다
            var session = Session(app);
            app.RequestClose();
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), b => b, "다시 안내");
            app.Invoke("dialog.alternate");                    // [측정 멈추고 종료]
            Assert.Equal(0, app.WaitExit(TimeSpan.FromSeconds(20)));
            Assert.Equal(EndReason.Stopped, ReadRecord(userData, session).EndReason);
            Assert.Contains("\"trayHintShown\": true", File.ReadAllText(Path.Combine(userData, "settings.json")));
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void TrayMenuMarksSymptomAndExitSaves()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        HintAlreadyShown(userData);
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--close-to-tray", "on");
            app.WaitDiag(Rounds, r => r >= 3, "3회차");
            var session = Session(app);
            app.PostTray(AppDriver.WmContextMenu);
            var items = AppDriver.WaitFor(() => app.TrayMenuItems(), i => i.Count >= 3, "메뉴 항목");
            Assert.Equal(["열기", "증상 표시", "종료"], items.Select(i => i.Name).ToArray());
            Assert.All(items, i => Assert.True(i.Enabled));                 // 측정 중: 증상 표시 켜짐
            app.PickTrayMenu("증상 표시");
            app.WaitDiag(d => d.GetProperty("marks").GetArrayLength(), n => n == 1, "트레이에서 증상 표시");
            app.RequestClose();
            WaitHidden(app);
            app.PostTray(AppDriver.WmContextMenu);
            AppDriver.WaitFor(() => app.TrayMenuItems(), i => i.Count >= 3, "숨긴 채 메뉴");
            app.PickTrayMenu("종료");
            Assert.Equal(0, app.WaitExit(TimeSpan.FromSeconds(20)));
            var record = ReadRecord(userData, session);
            Assert.Equal(EndReason.Stopped, record.EndReason);
            Assert.Contains(record.Events, e => (string?)e["type"] == SymptomMarks.MarkType);
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ClosingWhenNotMeasuringOrWithTheSettingOffExits()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using (var idle = AppDriver.LaunchRaw(userData, "--demo", "steady"))
            {
                idle.WaitDiag(Host, h => h == "Idle", "측정하지 않음");
                Assert.Equal(0, idle.Close());
            }
            using var off = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--close-to-tray", "off");
            off.WaitDiag(Rounds, r => r >= 3, "3회차");
            var session = Session(off);
            Assert.Equal(0, off.Close());                                    // M5 동작: 저장 후 종료, 확인 없음
            Assert.Equal(EndReason.Stopped, ReadRecord(userData, session).EndReason);
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void SecondLaunchOpensTheHiddenWindowAndExitWithoutInstanceIsTwo()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        HintAlreadyShown(userData);
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--close-to-tray", "on");
            app.WaitDiag(Rounds, r => r >= 3, "3회차");
            app.RequestClose();
            WaitHidden(app);
            Assert.Equal(0, app.RunSecond("--demo", "steady"));             // 두 번째 실행: 창을 띄우지 않고 넘김
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "두 번째 실행으로 열림");
            Assert.Contains(ResidentLog(app, "reopened"), e => e.GetProperty("data").GetProperty("by").GetString() == "secondInstance");
            Assert.Equal((0, 0), app.Exit());
        }
        finally { AppDriver.DeleteUserData(userData); }
        // 떠 있는 실행이 없을 때 --exit = 2 (창 없음)
        var empty = AppDriver.NewUserData();
        try
        {
            Directory.CreateDirectory(empty);
            var info = new ProcessStartInfo(AppDriver.Executable) { UseShellExecute = false };
            foreach (var a in new[] { "--exit", "--user-data", empty }) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            Assert.True(p.WaitForExit(20000));
            Assert.Equal(2, p.ExitCode);
        }
        finally { AppDriver.DeleteUserData(empty); }
    }

    [Fact]
    public void HiddenDropoutsGiveOneBalloonPerNoticeSegmentAndAFinishedBalloon()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "link-drop", TimeSpan.FromMinutes(3));
        HintAlreadyShown(userData);
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "link-drop", "--demo-speed", "30", "--start", "--close-to-tray", "on");
            app.WaitDiag(Rounds, r => r >= 2, "시작");
            app.RequestClose();
            WaitHidden(app);
            // 측정이 끝날 때까지 (180회차, 30배속 약 6초 + 여유)
            AppDriver.WaitFor(() => ResidentLog(app, "balloon").Any(e => e.GetProperty("data").GetProperty("kind").GetString() == "Finished"), b => b, "측정 끝 풍선", TimeSpan.FromSeconds(60));
            var balloons = ResidentLog(app, "balloon").Select(e => e.GetProperty("data")).ToArray();
            var starts = balloons.Where(b => b.GetProperty("kind").GetString() == "SegmentStart").ToArray();
            Assert.NotEmpty(starts);
            Assert.Single(balloons, b => b.GetProperty("kind").GetString() == "Finished");
            Assert.All(balloons, b => Assert.True(b.GetProperty("called").GetBoolean(), "풍선 호출 성공"));
            // 앱은 트레이에 남는다 → 풍선 클릭 = 결과 화면
            Assert.True(app.Alive);
            app.PostTray(AppDriver.NinBalloonClick);
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "풍선 클릭으로 열림");
            var d = app.WaitDiag(x => x, x => M5Fixtures.Str(x, "screen") == "measure" && x.GetProperty("finished").ValueKind == JsonValueKind.Object, "결과 화면");
            int hiddenStarts = starts.Length;
            Record("flow-resident-balloons", new { segmentStarts = hiddenStarts, total = balloons.Length, finished = M5Fixtures.Str(d, "finished", "end") });
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void SessionEndSavesWithinTheLimitAndCancelKeepsMeasuring()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        HintAlreadyShown(userData);
        try
        {
            using (var cancel = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--close-to-tray", "on"))
            {
                cancel.WaitDiag(Rounds, r => r >= 3, "3회차");
                cancel.SendSessionEnd(ending: false);                     // 질의 → 취소
                var before = Rounds(cancel.Diag());
                cancel.WaitDiag(Rounds, r => r > before + 2, "취소 뒤에도 측정 계속");
                Assert.Equal("Measuring", Host(cancel.Diag()));
                Assert.Equal("added", M5Fixtures.Str(Resident(cancel.Diag()), "trayState"));
                Assert.Equal((0, 0), cancel.Exit());                       // 취소 뒤에도 두 번째 실행 요청을 받는다 (closing 아님)
            }
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--close-to-tray", "on");
            app.WaitDiag(Rounds, r => r >= 3, "3회차");
            var session = Session(app);
            app.RequestClose();
            WaitHidden(app);                                              // 숨긴 채 로그오프
            app.SendSessionEnd(ending: true);
            var flushed = AppDriver.WaitFor(() => app.ReadLog().LastOrDefault(e => e.GetProperty("kind").GetString() == "sessionEnd"
                && e.GetProperty("data").GetProperty("step").GetString() == "flushed"), e => e.ValueKind == JsonValueKind.Object, "세션 종료 플러시");
            Assert.True(flushed.GetProperty("data").GetProperty("done").GetBoolean(), "4초 안에 끝남");
            var record = ReadRecord(userData, session);
            Assert.Equal(EndReason.Stopped, record.EndReason);
            Assert.False(record.Summary is null, "요약까지 봉인");
            app.TerminateAfterSessionEnd();                               // 실제 로그오프에서는 Windows가 프로세스를 끝낸다
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
