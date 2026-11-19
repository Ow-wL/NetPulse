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
        // 숨긴 뒤 프레임 구독 0 (화면·테마·프레임 측정기, 설계 A1, G4 M9 1라운드 b2)
        Assert.Equal(0, ResidentLog(app, "hidden").Last().GetProperty("data").GetProperty("frameHooks").GetInt32());
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

    /// <summary>
    /// G4 M9 1라운드 b4: 기록 상세에서 메모를 고치던 중 숨기고 다시 열면 초점·초안이 그대로 (다시 그려도 같은 칸으로 초점을 돌려준다).
    /// </summary>
    [Fact]
    public void ReopeningKeepsTheMemoBeingEditedAndItsFocus()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "rec-memo");
        M5Fixtures.SaveProfile(userData, "steady");
        HintAlreadyShown(userData);
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--screen", "records", "--close-to-tray", "on");
            var list = app.WaitDiag(x => x, x => x.GetProperty("screenDiag") is { ValueKind: JsonValueKind.Object } sd && sd.TryGetProperty("rows", out var rows)
                && rows.EnumerateArray().Any(r => r.GetProperty("life").GetString() != "Running" && !r.GetProperty("analyzing").GetBoolean()), "끝난 기록 줄");
            var row = list.GetProperty("screenDiag").GetProperty("rows").EnumerateArray().First(r => r.GetProperty("life").GetString() != "Running").GetProperty("n").GetInt32();
            app.Invoke($"records.row.{row}");
            app.WaitDiag(x => x.GetProperty("screenDiag").TryGetProperty("detailComplete", out var c) && c.GetBoolean(), b => b, "상세");
            app.Invoke("records.mark.0.edit");
            app.SetText("records.mark.0.text", "숨기기 전 초안");
            app.Find("records.mark.0.text").SetFocus();
            app.WaitDiag(x => x.GetProperty("focused").ToString(), f => f == "records.mark.0.text", "메모 칸 초점");
            app.RequestClose();
            WaitHidden(app);
            app.PostTray(AppDriver.NinSelect);
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "다시 보임");
            app.WaitDiag(x => !HiddenNow(x) && x.GetProperty("focused").ToString() == "records.mark.0.text", b => b, "다시 연 뒤 같은 칸 초점");
            Assert.Equal("숨기기 전 초안", ((System.Windows.Automation.ValuePattern)app.Find("records.mark.0.text")
                .GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value);
            Assert.Equal((0, 0), app.Exit());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    private static int Gaps(JsonElement d) => d.GetProperty("gaps").GetInt32();
    private static bool HiddenNow(JsonElement d) => Resident(d).GetProperty("hidden").GetBoolean();
    private static JsonElement[] Balloons(AppDriver app, string kind) =>
        ResidentLog(app, "balloon").Select(e => e.GetProperty("data")).Where(b => b.GetProperty("kind").GetString() == kind).ToArray();

    /// <summary>
    /// C1 절전 복귀 (G3): 숨긴 측정 중 데모 절전 공백(측정 경과가 40초 건너뜀) → 끊김 풍선 없음, 다시 열면 같은 측정에 공백 하나,
    /// 숨긴 동안 알림 구간 0이라 요약 알림도 없음 (A4).
    /// </summary>
    [Fact]
    public void HiddenSleepGapIsShownOnReopenWithoutADropoutBalloon()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "gap");
        HintAlreadyShown(userData);
        try
        {
            // 300회차, 60초에 절전 40초: 10배속이면 약 6초에 공백, 26초에 끝
            using var app = AppDriver.LaunchWith(userData, "--demo", "gap", "--demo-speed", "10", "--start", "--close-to-tray", "on");
            app.WaitDiag(Rounds, r => r >= 3, "3회차");
            var session = Session(app);
            Assert.Equal(0, Gaps(app.Diag()));
            app.RequestClose();
            WaitHidden(app);
            Thread.Sleep(9000);                                            // 숨긴 채 공백을 지난다
            app.PostTray(AppDriver.NinSelect);
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "다시 보임");
            var d = app.WaitDiag(x => x, x => !HiddenNow(x) && Gaps(x) >= 1, "다시 연 장면의 공백");
            Assert.Equal(session, M5Fixtures.Str(d, "session"));
            Assert.Equal("Measuring", Host(d));
            Assert.Empty(Balloons(app, "SegmentStart"));                   // 절전은 끊김이 아니다
            var reopened = ResidentLog(app, "reopened").Last().GetProperty("data");
            Assert.Equal(0, reopened.GetProperty("hiddenStarts").GetInt32());
            Assert.Equal((0, 0), app.Exit());
            Assert.NotEmpty(ReadRecord(userData, session).Gaps);
            Record("flow-resident-sleepgap", new { session, gaps = Gaps(d), rounds = Rounds(d) });
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>끝에서 끝까지 시나리오 (C1): 끊김 6회차(60~66초)와 절전 40초(100초)가 있는 200초 데모를 시험 폴더에 둔다.</summary>
    private static string RoundTripScenario(string name, string? detect)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(M5Fixtures.ScenarioPath("resident-roundtrip")))!.AsObject();
        if (detect is not null) o["faults"]!["detect"] = detect;
        var dir = Path.Combine(AppDriver.OutputDirectory, "m9-scenarios");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".json");
        File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    [Fact]
    public void RoundTripFromTheFirstRunThroughTheTrayToAMovedRecordReport() => RoundTrip("roundtrip", null, "Ok");

    /// <summary>C1 IPv4 경로 없음: 첫 실행 탐지가 이 상태여도(외부 대상만 제안) 상주 흐름이 같게 동작한다.</summary>
    [Fact]
    public void RoundTripWorksWhenTheFirstRunFindsNoIpv4Route() => RoundTrip("roundtrip-noipv4", "no-ipv4", "NoIpv4Route");

    /// <summary>
    /// 첫 실행 → 측정 시작 → 창 닫기 → 안내 [트레이에서 계속] → 데모 끊김 → 풍선 1 → 절전 공백 → 두 번째 실행(열기) → 같은 측정·공백 표시
    /// → 트레이 메뉴 증상 표시 → 측정 끝 → 기록에서 열기 → 설정에서 위치 옮기기 → 같은 기록 다시 열기 → 리포트 내보내기 → <c>--exit</c>.
    /// </summary>
    private static void RoundTrip(string name, string? detect, string expectedStatus)
    {
        var userData = AppDriver.NewUserData();
        var dest = userData + "-dest";
        var outDir = Path.Combine(AppDriver.OutputDirectory, "m9-out", name);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dest);
        var scenario = RoundTripScenario(name, detect);
        try
        {
            // 5배속: 끊김 12초, 절전 20초. 150초부터 0.5배속이라 다시 연 뒤 끝나기 전까지 여유 100초
            using var app = AppDriver.LaunchRaw(userData, "--demo", scenario, "--demo-speed", "5", "--demo-speed-after", "150:0.5",
                "--pick-folder", dest, "--report-out", outDir);
            var first = app.WaitDiag(x => x, x => M5Fixtures.Str(x, "screen") == "firstrun" && x.GetProperty("screenDiag").ValueKind == JsonValueKind.Object
                && x.GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean(), "첫 실행");
            Assert.Equal(expectedStatus, M5Fixtures.Str(first, "screenDiag", "status"));
            app.Invoke("firstrun.start");
            app.WaitDiag(x => (M5Fixtures.Str(x, "screen"), Host(x)), s => s == ("measure", "Measuring"), "측정 시작");
            var session = Session(app);

            app.RequestClose();
            app.WaitDiag(x => x.GetProperty("dialogOpen").GetBoolean(), b => b, "처음 안내");
            app.Invoke("dialog.confirm");
            WaitHidden(app);
            AppDriver.WaitFor(() => Balloons(app, "SegmentStart").Length, n => n >= 1, "끊김 풍선", TimeSpan.FromSeconds(60));
            Thread.Sleep(12000);                                           // 숨긴 채 절전 공백(100→140초)을 지나 느린 구간(150초~)으로
            Assert.Single(Balloons(app, "SegmentStart"));

            Assert.Equal(0, app.RunSecond("--demo", scenario));            // 두 번째 실행 = 열기
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "두 번째 실행으로 열림");
            var back = app.WaitDiag(x => x, x => !HiddenNow(x) && Gaps(x) >= 1, "같은 측정, 공백");
            Assert.Equal(session, M5Fixtures.Str(back, "session"));
            Assert.Equal("Measuring", Host(back));
            Assert.Equal(1, ResidentLog(app, "reopened").Last().GetProperty("data").GetProperty("hiddenStarts").GetInt32());

            app.PostTray(AppDriver.WmContextMenu);
            AppDriver.WaitFor(() => app.TrayMenuItems(), i => i.Count >= 3, "트레이 메뉴");
            app.PickTrayMenu("증상 표시");
            app.WaitDiag(x => x.GetProperty("marks").GetArrayLength(), n => n == 1, "트레이에서 증상 표시");

            app.Select("nav.measure");
            app.Invoke("measure.stop");
            app.WaitDiag(x => x.GetProperty("finished").ValueKind == JsonValueKind.Object, b => b, "측정 끝");

            void OpenRecord(string why)
            {
                app.Select("nav.records");
                // 기록 화면은 지난번 상세 보기를 기억한다: 상세면 목록으로
                var now = app.WaitDiag(x => x, x => x.GetProperty("screenDiag") is { ValueKind: JsonValueKind.Object } sd && sd.TryGetProperty("mode", out _), why + ": 기록 화면");
                if (M5Fixtures.Str(now, "screenDiag", "mode") != "List")
                {
                    app.Invoke("records.back");
                    app.WaitDiag(x => M5Fixtures.Str(x, "screenDiag", "mode"), m => m == "List", why + ": 목록으로");
                }
                var list = app.WaitDiag(x => x, x => x.GetProperty("screenDiag") is { ValueKind: JsonValueKind.Object } sd && sd.TryGetProperty("rows", out var rows)
                    && sd.GetProperty("analyzing").GetInt32() == 0 && rows.EnumerateArray().Any(r => r.GetProperty("Id").GetString() == session), why + ": 목록");
                var row = list.GetProperty("screenDiag").GetProperty("rows").EnumerateArray().First(r => r.GetProperty("Id").GetString() == session).GetProperty("n").GetInt32();
                app.Invoke($"records.row.{row}");
                var detail = app.WaitDiag(x => x, x => x.GetProperty("screenDiag").TryGetProperty("detailComplete", out var c) && c.GetBoolean(), why + ": 상세");
                Assert.Equal(session, M5Fixtures.Str(detail, "screenDiag", "detail", "Id"));
            }
            OpenRecord("끝난 측정");

            app.Select("nav.settings");
            M9SettingsTests.ConfirmMove(app, "settings.records.change");
            app.WaitDiag(M9SettingsTests.RecState, s => s is "Done" or "Partial" or "Failed", "옮기기 끝");
            Assert.Equal("Done", M9SettingsTests.RecState(app.Diag()));
            Assert.True(Directory.Exists(Path.Combine(dest, session)), "새 위치로 옮김");

            // 옮기기 전에 열어 둔 상세는 같은 측정의 새 위치로 이어진다: 그 자리에서 메모를 저장하면 새 위치에 남는다 (G4 M9 1라운드 b3)
            app.Select("nav.records");
            var still = app.WaitDiag(x => x, x => M5Fixtures.Str(x, "screenDiag", "mode") == "Detail"
                && x.GetProperty("screenDiag").TryGetProperty("detailComplete", out var c) && c.GetBoolean(), "옮긴 뒤 열려 있던 상세");
            Assert.Equal(session, M5Fixtures.Str(still, "screenDiag", "detail", "Id"));
            app.Invoke("records.mark.0.edit");
            app.SetText("records.mark.0.text", "옮긴 뒤 적은 메모");
            app.Invoke("records.mark.0.save");
            app.WaitDiag(x => x.GetProperty("screenDiag").GetProperty("detail").GetProperty("memos").EnumerateArray().Select(m => m.GetString()).ToArray(),
                m => m.Contains("옮긴 뒤 적은 메모"), "옮긴 뒤 저장한 메모");
            // 노트 파일은 한글을 JSON 이스케이프로 적는다: 줄마다 읽어 메모 값을 비교
            Assert.Contains(File.ReadAllLines(Path.Combine(dest, session, "notes.jsonl")),
                l => JsonSerializer.Serialize(JsonDocument.Parse(l).RootElement, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                    .Contains("옮긴 뒤 적은 메모", StringComparison.Ordinal));

            OpenRecord("옮긴 기록");
            app.Invoke("records.report");
            app.WaitDiag(x => M5Fixtures.Str(x, "screen"), s => s == "report", "리포트 화면");
            M8FlowTests.WaitReady(app);
            app.Invoke("report.export");
            var done = M8FlowTests.WaitExport(app);
            Assert.True(done.GetProperty("screenDiag").GetProperty("export").GetProperty("Ok").GetBoolean());
            Assert.Single(Directory.GetFiles(outDir));
            Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
            Assert.Equal((0, 0), app.Exit());

            var record = RecordReader.Read(RecordReader.FindSegments(session, [dest, .. M5Fixtures.RecordRoots(userData)]));
            Assert.Equal(EndReason.Stopped, record.EndReason);
            Assert.NotEmpty(record.Gaps);
            Assert.Contains(record.Events, e => (string?)e["type"] == SymptomMarks.MarkType);
            Assert.Equal(detect is null, record.Header!.Targets.Any(t => t.Role == NetPulse.Core.Targets.TargetRole.Local));
            Record("flow-resident-" + name, new { status = expectedStatus, balloons = 1, gaps = record.Gaps.Count, rounds = record.Rounds.Count, moved = true });
        }
        finally
        {
            AppDriver.DeleteUserData(userData);
            AppDriver.DeleteUserData(dest);
        }
    }
}
