using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using NetPulse.Core.Records;

namespace NetPulse.UiTests;

/// <summary>
/// 상주 실측 (M9 설계 7.3, M10 설계 D4 [1-9]). 이 PC의 실제 네트워크·작업 표시줄을 쓴다: 게이트 밖(<c>Kind=Live</c>), 결과는 공개하지 않는다.
/// 동의 화면부터 시작해 실제 경로를 한 번 지난다 (M10 [2-6]). 풍선은 API 호출 결과만 자동으로 보고, 실제로 보였는지는 사용자가 확인한다.
/// 실행: NETPULSE_LIVE=1, NETPULSE_UITEST_OUT은 저장소 .loop/ 아래.
/// </summary>
[Trait("Kind", "Live")]
public sealed class M9LiveRunTests
{
    private static string Screen(JsonElement d) => M5Fixtures.Str(d, "screen");

    private static string LoopOutput()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NetPulse.slnx"))) root = root.Parent;
        Assert.True(root is not null, "저장소 루트를 찾지 못했습니다");
        string loop = Path.GetFullPath(Path.Combine(root!.FullName, ".loop")) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(AppDriver.OutputDirectory) + Path.DirectorySeparatorChar;
        Assert.True(output.StartsWith(loop, StringComparison.OrdinalIgnoreCase), "실측 시험의 출력(NETPULSE_UITEST_OUT)은 .loop/ 아래여야 합니다");
        return output;
    }

    /// <summary>작업 표시줄 알림 영역에서 이 앱의 실제 아이콘 (이름이 NetPulse로 시작하는 단추). 없으면 null.</summary>
    private static AutomationElement? FindTrayIcon()
    {
        var taskbar = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
        if (taskbar is null) return null;
        foreach (AutomationElement e in taskbar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            try { if (e.Current.Name.StartsWith("NetPulse", StringComparison.Ordinal)) return e; }
            catch (ElementNotAvailableException) { }
        }
        return null;
    }

    [Fact]
    public void ResidentMeasurementKeepsRunningWhileHiddenAndSealsOnTrayExit()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("NETPULSE_LIVE"));
        string output = LoopOutput();
        var userData = AppDriver.NewUserData();   // 격리한 임시 사용자 폴더 (실제 기본 폴더를 건드리지 않는다)
        var report = new Dictionary<string, object?>();
        try
        {
            string sessionId;
            int roundsBeforeHide;
            using (var app = AppDriver.LaunchUnconsented(userData, "--close-to-tray", "on", "--live-balloon-test"))
            try
            {
                app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
                app.Invoke("terms.accept");
                app.FollowNextWindow();
                app.WaitDiag(Screen, s => s == "firstrun", "첫 실행 안내");
                string status = app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "status"), s => s is "Ok" or "NoNetwork" or "Unknown", "실제 탐지");
                Assert.True(status == "Ok", "이 PC 탐지 결과: " + status);
                app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
                app.Invoke("firstrun.start");
                app.WaitDiag(d => M5Fixtures.Str(d, "host"), h => h == "Measuring", "측정 시작");
                app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 5, "5회차");
                sessionId = M5Fixtures.Str(app.Diag(), "session");
                roundsBeforeHide = app.Diag().GetProperty("rounds").GetInt32();      // 진단 값은 창이 보일 때만 읽는다 (숨긴 창의 UIA 트리는 믿을 수 없다)

                app.RequestClose();                                                   // 처음 닫기 → 안내
                app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "처음 숨김 안내");
                app.Invoke("dialog.confirm");                                         // 트레이에서 계속
                AppDriver.WaitFor(() => app.WindowVisible, v => !v, "창 숨김");
                var balloon = AppDriver.WaitFor(() => app.ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "liveBalloon"), e => e.ValueKind == JsonValueKind.Object, "시험 풍선 호출");
                report["balloonCalled"] = balloon.GetProperty("data").GetProperty("called").GetBoolean();

                Thread.Sleep(TimeSpan.FromMinutes(1));                                // 숨긴 채 1분
                // 숨긴 1분 사이에 누가 창을 열었는가 (풍선 클릭 등). 열렸으면 기록만 한다 (측정은 창과 상관없이 이어져야 한다)
                report["reopenedWhileHidden"] = app.ReadLog().Count(e => e.GetProperty("kind").GetString() == "resident" && e.GetProperty("data").GetProperty("what").GetString() == "reopened");
                report["roundsBeforeHide"] = roundsBeforeHide;

                var icon = FindTrayIcon();                                            // 실제 아이콘을 UIA로
                report["trayIconFound"] = icon is not null;
                if (icon is not null && icon.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) ((InvokePattern)inv).Invoke();
                else app.PostTray(AppDriver.NinSelect);                               // 못 찾으면 기록하고 같은 경로로
                AppDriver.WaitFor(() => app.WindowVisible, v => v, "다시 보임");
                var shown = app.WaitDiag(d => d, d => !M9ResidentTests.HiddenNow(d), "다시 연 진단");
                int roundsHidden = shown.GetProperty("rounds").GetInt32();
                report["roundsAfterMinute"] = roundsHidden;
                Assert.True(roundsHidden >= roundsBeforeHide + 30, $"숨긴 동안 회차 {roundsBeforeHide} → {roundsHidden}");
                Assert.Equal(sessionId, M5Fixtures.Str(shown, "session"));           // 같은 측정
                app.CaptureClient("live-m9-reopened");

                app.RequestClose();                                                   // 다시 숨김 (안내는 이미 봄)
                AppDriver.WaitFor(() => app.WindowVisible, v => !v, "다시 숨김");
                app.PostTray(AppDriver.WmContextMenu);
                AppDriver.WaitFor(() => app.TrayMenuItems(), i => i.Count >= 3, "숨긴 채 메뉴");   // 메뉴가 다 뜬 뒤 고른다 (M9ResidentTests와 같게)
                app.PickTrayMenu("종료");
                // 사용자가 다른 앱을 쓰는 중이면 메뉴 주인 창이 전경을 못 얻어 UIA 고르기가 무시될 수 있다 (M10 실측 1회차).
                // 그때는 기록하고 메뉴를 닫은 뒤 같은 종료 경로(--exit → BeginClose)로 끝낸다. 메뉴 [종료] 자체는 게이트 시험(M9ResidentTests)이 본다.
                var until = DateTime.UtcNow.AddSeconds(15);
                while (app.Alive && DateTime.UtcNow < until) Thread.Sleep(200);
                report["trayMenuExit"] = !app.Alive;
                if (app.Alive)
                {
                    app.CancelTrayMenu();
                    report["exitCode"] = app.Exit().First;
                }
                else report["exitCode"] = app.WaitExit(TimeSpan.FromSeconds(5));
            }
            finally
            {
                File.WriteAllLines(Path.Combine(output, "live-m9-log.jsonl"), app.ReadLog().Select(e => e.GetRawText()));   // 실패해도 남긴다 (.loop/, 공개하지 않음)
            }
            Assert.Equal(0, report["exitCode"]);
            var record = RecordReader.Read(RecordReader.FindSegments(sessionId, M5Fixtures.RecordRoots(userData)));
            report["sealed"] = record.Summary is not null;
            report["recordRounds"] = record.Rounds.Count;
            Assert.NotNull(record.Summary);                                           // 저장 후 끝냄 (봉인)
            Assert.True(record.Rounds.Count > roundsBeforeHide, $"기록 회차 {record.Rounds.Count}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "live-m9-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            AppDriver.DeleteUserData(userData);
        }
    }
}
