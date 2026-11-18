using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace NetPulse.UiTests;

/// <summary>
/// 실제 키 입력 시험 (M2 설계 7.1). 키는 전경 창으로 가므로, 키를 하나 보낼 때마다 직전에 전경 창이 대상 프로세스인지 확인한다.
/// 아니면 즉시 멈추고 "미완료(전경 아님)"로 실패한다 (통과로 세지 않는다. 다른 앱에 키가 들어가지 않게).
/// 사용자가 PC를 쓰는 중이면 이 시험은 돌지 않는다. 그때는 잠시 PC를 비워 달라고 요청한다.
/// </summary>
public sealed class KeyboardTests
{
    private const ushort Tab = 0x09, Enter = 0x0D, Escape = 0x1B, Space = 0x20, Up = 0x26, Down = 0x28, Shift = 0x10;

    /// <summary>Shift+Tab (뒤로). 보낼 때마다 전경 확인은 Press와 같다.</summary>
    private static void PressShiftTab(AppDriver app)
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != app.ProcessId) throw new InvalidOperationException("미완료(전경 아님): 키를 보내지 않았습니다.");
        var inputs = new[] { Native.Key(Shift, false), Native.Key(Tab, false), Native.Key(Tab, true), Native.Key(Shift, true) };
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) != inputs.Length) throw new InvalidOperationException("키 입력을 보내지 못했습니다.");
        Thread.Sleep(120);
    }

    private static void Press(AppDriver app, ushort key)
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != app.ProcessId)
            throw new InvalidOperationException("미완료(전경 아님): 앱 창이 전경이 아니어서 키를 보내지 않았습니다. 사용자가 PC를 쓰는 중이면 잠시 비워 달라고 요청하세요.");
        var inputs = new[] { Native.Key(key, false), Native.Key(key, true) };
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) != inputs.Length)
            throw new InvalidOperationException("키 입력을 보내지 못했습니다.");
        Thread.Sleep(120);
    }

    private const ushort Control = 0x11, KeyM = 0x4D;

    /// <summary>Ctrl+M (증상 표시 단축키, M5). 보낼 때마다 전경 확인은 Press와 같다.</summary>
    private static void PressCtrlM(AppDriver app)
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != app.ProcessId) throw new InvalidOperationException("미완료(전경 아님): 키를 보내지 않았습니다.");
        var inputs = new[] { Native.Key(Control, false), Native.Key(KeyM, false), Native.Key(KeyM, true), Native.Key(Control, true) };
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) != inputs.Length) throw new InvalidOperationException("키 입력을 보내지 못했습니다.");
        Thread.Sleep(200);
    }

    private static string Focused(AppDriver app) =>
        app.WaitDiag(d => d.GetProperty("focused").ToString(), _ => true, "초점");

    private static AppDriver Launch(params string[] extra) => LaunchAny(null, ["--showcase", "--freeze-motion", .. extra]);

    private static AppDriver LaunchAny(string? userData, string[] args)
    {
        var app = AppDriver.LaunchWith(userData, args);
        try
        {
            // 새로 띄운 창은 보통 전경을 받는다. 받을 때까지 잠시 기다리되 빼앗지는 않는다.
            AppDriver.WaitFor(() => { Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid); return pid; },
                pid => pid == app.ProcessId, "미완료(전경 아님): 앱 창이 전경이 되지 않음. 사용자가 다른 창을 쓰는 중", TimeSpan.FromSeconds(10));
            return app;
        }
        catch
        {
            app.Dispose();   // 띄운 창을 남기지 않는다
            throw;
        }
    }

    [Fact]
    public void TabEntersTheRailOnceArrowsMoveAndSpaceSelects()
    {
        using var app = Launch();
        app.WaitDiag(d => d.GetProperty("section").ToString(), s => s == "overview", "개요");
        Press(app, Tab);
        app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "nav.overview", "탭 → 선택된 탐색 항목");
        Press(app, Down);
        app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "nav.colors", "아래 화살표 → 다음 항목");
        Assert.Equal("overview", app.Diag().GetProperty("section").ToString());   // 화살표만으로는 고르지 않는다
        Press(app, Space);
        app.WaitDiag(d => d.GetProperty("section").ToString(), s => s == "colors", "Space → 고름");
        Press(app, Tab);
        app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f.StartsWith("toolbar.theme.", StringComparison.Ordinal), "탭 → 탐색을 벗어나 도구 막대");
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void FocusRingIsDrawnOnGlass()
    {
        using var app = Launch("--section", "buttons");
        app.WaitDiag(d => d.GetProperty("section").ToString(), s => s == "buttons", "버튼 절");
        var button = app.Find("btn.secondary.normal");
        var r = button.Current.BoundingRectangle;
        var win = app.Window.Current.BoundingRectangle;
        using var before = app.CaptureBitmap();
        // 초점 표시는 키보드로 옮긴 초점에만 그려진다. 대상 버튼에 닿을 때까지 Tab을 누른다 (최대 30번).
        for (int i = 0; i < 30 && Focused(app) != "btn.secondary.normal"; i++) Press(app, Tab);
        Assert.Equal("btn.secondary.normal", Focused(app));
        Thread.Sleep(200);
        using var after = app.CaptureBitmap();
        app.Save(after, "keyboard-focus-ring");
        // 버튼 바깥 4 px 띠에서 차이가 있어야 한다 (두 겹 초점 표시)
        var band = Rectangle.Round(new RectangleF((float)(r.X - win.X) - 6, (float)(r.Y - win.Y) - 6, (float)r.Width + 12, (float)r.Height + 12));
        var diff = ImageCompare.Diff(before, after, Rectangle.Intersect(band, new Rectangle(Point.Empty, before.Size)));
        Assert.True(diff.P99 >= 40, $"초점 표시가 보이지 않는다: {diff}");
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void KeyboardAloneStartsMarksWithAMemoAndStops()
    {
        // M5 설계 4.9: 첫 측정 시작·증상 표시(Ctrl+M)·메모 저장(Enter)·중지를 키보드만으로
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = LaunchAny(userData, ["--demo", "steady", "--demo-speed", "20", "--freeze-motion"]);
            app.WaitDiag(d => d.GetProperty("screenDiag").ValueKind == System.Text.Json.JsonValueKind.Object && d.GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
            for (int i = 0; i < 40 && Focused(app) != "firstrun.start"; i++) Press(app, Tab);
            Assert.Equal("firstrun.start", Focused(app));
            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("host").GetString(), s => s == "Measuring", "측정 시작");
            app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 5, "5회차");
            PressCtrlM(app);
            app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("memoOpen").GetBoolean(), o => o, "메모 패널 (Ctrl+M)");
            Assert.Equal("measure.memo.text", Focused(app));
            foreach (var ch in "keyboard") Press(app, (ushort)char.ToUpperInvariant(ch));
            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("marks").EnumerateArray().Any(m => (m.GetProperty("Memo").GetString() ?? "").Length > 0), b => b, "메모 저장 (Enter)");
            Assert.Equal("measure.mark", Focused(app));   // 저장 뒤 초점은 증상 표시 버튼으로
            for (int i = 0; i < 10 && Focused(app) != "measure.stop"; i++) PressShiftTab(app);
            Assert.Equal("measure.stop", Focused(app));
            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("finished").ValueKind == System.Text.Json.JsonValueKind.Object, f => f, "중지");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void DialogOpensWithEnterKeepsTabInsideAndClosesWithEscape()
    {
        using var app = Launch("--section", "dialog");
        app.WaitDiag(d => d.GetProperty("section").ToString(), s => s == "dialog", "대화상자 절");
        app.Find("dialog.open").SetFocus();
        Press(app, Enter);
        app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "Enter → 열림");
        for (int i = 0; i < 5; i++)
        {
            Press(app, Tab);
            var f = Focused(app);
            Assert.True(f is "dialog.confirm" or "dialog.cancel", "탭이 대화상자 밖으로 나갔다: " + f);
        }
        for (int i = 0; i < 5; i++)
        {
            PressShiftTab(app);   // 뒤로도 갇혀 있어야 한다 (G4 M2 1라운드)
            var f = Focused(app);
            Assert.True(f is "dialog.confirm" or "dialog.cancel", "Shift+Tab이 대화상자 밖으로 나갔다: " + f);
        }
        Press(app, Escape);
        app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => !o, "Esc → 닫힘");
        app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "dialog.open", "연 버튼으로 초점 복귀");
        Assert.Equal(0, app.Close());
    }

    private const ushort Period = 0xBE;

    private static System.Text.Json.JsonElement Sd(System.Text.Json.JsonElement d) => d.GetProperty("screenDiag");

    /// <summary>키 하나씩 글자를 친다 (영문 소문자·마침표만).</summary>
    private static void TypeText(AppDriver app, string text)
    {
        foreach (var ch in text) Press(app, ch == '.' ? Period : (ushort)char.ToUpperInvariant(ch));
    }

    private static void TabTo(AppDriver app, string id, int limit = 60)
    {
        for (int i = 0; i < limit && Focused(app) != id; i++) Press(app, Tab);
        Assert.Equal(id, Focused(app));
    }

    [Fact]
    public void KeyboardAloneDiagnosesExpandsChecksASiteAndFlushes()
    {
        // M6 설계 7.2-9·4.5: 시작 Enter, 검사 카드 Space로 펼침, 사이트 점검 입력·Enter, DNS 대화상자 초점은 취소·닫으면 누른 단추로
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "profile");
        try
        {
            using var app = LaunchAny(userData, [.. M5Fixtures.Args("--demo tests:diag-dns --screen diagnose"), "--freeze-motion"]);
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            TabTo(app, "diagnose.start");
            Press(app, Enter);
            app.WaitDiag(d => (Sd(d).GetProperty("operation").GetString(), Sd(d).GetProperty("conclusion").ValueKind), x => x == ("None", System.Text.Json.JsonValueKind.Number), "진단 끝 (Enter)");
            Assert.Equal(3, Sd(app.Diag()).GetProperty("conclusion").GetInt32());
            Assert.Equal("diagnose.start", Focused(app));   // 실행 중 꺼졌던 시작 단추로 초점이 돌아온다

            TabTo(app, "diagnose.check.dns.toggle");
            Press(app, Space);
            app.WaitDiag(d => Sd(d).GetProperty("expanded").EnumerateArray().Select(e => e.GetString()).ToArray(), e => e.Contains("Dns"), "DNS 카드 펼침 (Space)");
            Press(app, Space);
            app.WaitDiag(d => Sd(d).GetProperty("expanded").GetArrayLength(), n => n == 0, "다시 접힘");

            TabTo(app, "diagnose.site.input");
            TypeText(app, "shop.example");
            Press(app, Enter);
            app.WaitDiag(d => M5Fixtures.Str(Sd(d), "site", "dns"), s => s.Length > 0, "사이트 점검 (Enter)");
            Assert.Equal("Normal", M5Fixtures.Str(Sd(app.Diag()), "site", "https"));

            TabTo(app, "diagnose.flush");
            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "대화상자 (Enter)");
            Assert.Equal("dialog.cancel", Focused(app));   // 시스템을 바꾸는 확인이라 초점은 취소
            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => !o, "취소");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "diagnose.flush", "누른 단추로 초점 복귀");
            Assert.Equal(0, Sd(app.Diag()).GetProperty("actions").EnumerateArray().Count(a => a.GetString() == "flushDns"));

            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "다시 열림");
            PressShiftTab(app);
            Assert.Equal("dialog.confirm", Focused(app));
            Press(app, Enter);
            app.WaitDiag(d => M5Fixtures.Str(Sd(d), "flushResult"), s => s == "ok", "비우고 다시 진단");
            Assert.Equal(1, Sd(app.Diag()).GetProperty("actions").EnumerateArray().Count(a => a.GetString() == "flushDns"));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void KeyboardAloneOpensARecordGoesBackAndCompares()
    {
        // M7 설계 4.4·7.2-10: 줄 Enter로 열기 → 초점은 [목록으로] → Esc로 목록, 연 줄로 초점 복귀 → [비교] → 줄 둘 Space → 비교
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "rec-compare");
        try
        {
            using var app = LaunchAny(userData, [.. M5Fixtures.Args("--demo steady --screen records"), "--freeze-motion"]);
            app.WaitDiag(d => Sd(d).ValueKind == System.Text.Json.JsonValueKind.Object && Sd(d).TryGetProperty("listed", out var l) && l.GetBoolean()
                && Sd(d).GetProperty("analyzing").GetInt32() == 0, b => b, "기록 목록");
            TabTo(app, "records.row.0");
            Press(app, Enter);
            app.WaitDiag(d => Sd(d).TryGetProperty("detail", out var x) && x.ValueKind == System.Text.Json.JsonValueKind.Object, b => b, "상세 (Enter)");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.back", "초점은 목록으로 단추");
            Press(app, Escape);
            app.WaitDiag(d => Sd(d).GetProperty("mode").GetString(), m => m == "List", "Esc → 목록");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.row.0", "연 줄로 초점 복귀");

            TabTo(app, "records.compare");
            Press(app, Enter);
            app.WaitDiag(d => Sd(d).GetProperty("picking").GetBoolean(), b => b, "비교 고르기");
            TabTo(app, "records.row.0");
            Press(app, Space);
            app.WaitDiag(d => Sd(d).GetProperty("picked").GetInt32(), n => n == 1, "하나 고름 (Space)");
            TabTo(app, "records.row.1");
            Press(app, Space);
            app.WaitDiag(d => Sd(d).TryGetProperty("comparison", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Object, b => b, "둘 고르면 비교");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void KeyboardAloneSetsReportOptionsAndExports()
    {
        // M8 설계 4.3·7.2-7: 리포트 화면 → 진단 넣기 끄기(Space) → 끊김 상세(화살표·Space) → 내보내기(Enter) → 초점은 내보내기 단추.
        // 가린 문서(기본)에서는 메모 넣기 스위치가 막혀 Tab이 건너뛴다 (가린 리포트에는 메모를 넣지 않음)
        var userData = AppDriver.NewUserData();
        var outDir = Path.Combine(AppDriver.OutputDirectory, "m8-out", "keyboard");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        M5Fixtures.Prepare(userData, "rec-memo");
        try
        {
            using var app = LaunchAny(userData, [.. M5Fixtures.Args("--demo steady --screen report"), "--report-out", outDir, "--freeze-motion"]);
            bool Ready(System.Text.Json.JsonElement d) => Sd(d).ValueKind == System.Text.Json.JsonValueKind.Object && Sd(d).TryGetProperty("previewReady", out var p) && p.GetBoolean()
                && Sd(d).GetProperty("state").GetString() == "Ready";
            app.WaitDiag(d => Ready(d), b => b, "미리 보기");
            TabTo(app, "report.diagnoses");
            Press(app, Space);
            app.WaitDiag(d => !Sd(d).GetProperty("diagnoses").GetBoolean() && Ready(d), b => b, "진단 넣기 끄기 (Space)");
            // 단추 묶음은 Tab 한 번으로 들어간다 (들어가는 단추는 묶음이 정함): 들어간 뒤 오른쪽 화살표로 "10개"까지
            for (int i = 0; i < 20 && !Focused(app).StartsWith("report.details.", StringComparison.Ordinal); i++) Press(app, Tab);
            Assert.StartsWith("report.details.", Focused(app));
            for (int i = 0; i < 4 && Focused(app) != "report.details.10"; i++) Press(app, 0x27);
            Assert.Equal("report.details.10", Focused(app));
            Press(app, Space);
            app.WaitDiag(d => Sd(d).GetProperty("details").GetInt32() == 10 && Ready(d), b => b, "끊김 상세 10 (화살표·Space)");
            TabTo(app, "report.export");
            Press(app, Enter);
            app.WaitDiag(d => Sd(d).TryGetProperty("export", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Object && e.GetProperty("Ok").GetBoolean(), b => b, "내보내기 (Enter)");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "report.export", "초점은 내보내기 단추");
            Assert.Single(Directory.GetFiles(outDir));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    private const ushort Left = 0x25, Right = 0x27;

    private static void TabToPrefix(AppDriver app, string prefix, int limit = 80)
    {
        for (int i = 0; i < limit && !Focused(app).StartsWith(prefix, StringComparison.Ordinal); i++) Press(app, Tab);
        Assert.StartsWith(prefix, Focused(app));
    }

    /// <summary>
    /// M8 이월 3: 가린 문서에서 Tab은 막힌 메모 넣기 스위치를 건너뛴다. 가리기 해제(화살표·Space) → 메모 켜기(Space) → 다시 가리기를 키보드로 하면
    /// 메모 넣기는 꺼지고 막힌다 (UIA IsEnabled·ToggleState).
    /// </summary>
    [Fact]
    public void TabSkipsTheBlockedMemoSwitchAndTheKeyboardUnmasksAndMasksAgain()
    {
        var userData = AppDriver.NewUserData();
        var outDir = Path.Combine(AppDriver.OutputDirectory, "m8-out", "keyboard-mask");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        M5Fixtures.Prepare(userData, "rec-memo");
        try
        {
            using var app = LaunchAny(userData, [.. M5Fixtures.Args("--demo steady --screen report"), "--report-out", outDir, "--freeze-motion"]);
            bool Ready(System.Text.Json.JsonElement d, bool masked) => Sd(d).ValueKind == System.Text.Json.JsonValueKind.Object && Sd(d).TryGetProperty("previewReady", out var p) && p.GetBoolean()
                && Sd(d).GetProperty("state").GetString() == "Ready" && Sd(d).GetProperty("published") is { ValueKind: System.Text.Json.JsonValueKind.Object } pub && pub.GetProperty("masked").GetBoolean() == masked;
            app.WaitDiag(d => Ready(d, true), b => b, "가린 미리 보기");
            var visited = new List<string>();
            for (int i = 0; i < 60 && Focused(app) != "report.export"; i++) { Press(app, Tab); visited.Add(Focused(app)); }
            Assert.Equal("report.export", Focused(app));
            Assert.DoesNotContain("report.memos", visited);                  // 막힌 스위치는 건너뜀

            TabToPrefix(app, "report.mask.");
            for (int i = 0; i < 3 && Focused(app) != "report.mask.unmasked"; i++) Press(app, Right);
            Assert.Equal("report.mask.unmasked", Focused(app));
            Press(app, Space);
            app.WaitDiag(d => Ready(d, false), b => b, "가리기 해제 (Space)");
            TabTo(app, "report.memos");
            Press(app, Space);
            app.WaitDiag(d => Sd(d).GetProperty("memos").GetBoolean(), b => b, "메모 넣기 켬 (Space)");

            TabToPrefix(app, "report.mask.");
            for (int i = 0; i < 3 && Focused(app) != "report.mask.masked"; i++) Press(app, Left);
            Assert.Equal("report.mask.masked", Focused(app));
            Press(app, Space);
            app.WaitDiag(d => Ready(d, true) && !Sd(d).GetProperty("memos").GetBoolean(), b => b, "다시 가림: 메모 넣기 꺼짐");
            var memoSwitch = app.Find("report.memos");
            Assert.False(memoSwitch.Current.IsEnabled);
            Assert.Equal(ToggleState.Off, ((TogglePattern)memoSwitch.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState);
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>M9 C1 키보드: 설정 화면 Tab 순서로 상주 알림 토글(Space), 기록 옮기기 확인 대화상자(Enter·Tab 갇힘·Esc·다시 Enter).</summary>
    [Fact]
    public void KeyboardAloneTogglesANoticeAndConfirmsARecordMove()
    {
        var userData = AppDriver.NewUserData();
        var dest = userData + "-dest";
        M5Fixtures.Prepare(userData, "rec-move");
        try
        {
            using var app = LaunchAny(userData, ["--demo", "steady", "--screen", "settings", "--pick-folder", dest, "--freeze-motion"]);
            app.WaitDiag(d => Sd(d).ValueKind == System.Text.Json.JsonValueKind.Object && Sd(d).TryGetProperty("records", out var r) && r.GetProperty("changeEnabled").GetBoolean(), b => b, "설정 화면");
            Assert.False(Sd(app.Diag()).GetProperty("resident").GetProperty("notifyRecovered").GetBoolean());
            TabTo(app, "settings.notifyRecovered");
            Press(app, Space);
            app.WaitDiag(d => Sd(d).GetProperty("resident").GetProperty("notifyRecovered").GetBoolean(), b => b, "알림 토글 (Space)");
            Assert.Contains("\"notifyRecovered\": true", File.ReadAllText(Path.Combine(userData, "settings.json")));

            TabTo(app, "settings.records.change");
            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "옮기기 확인 (Enter)");
            for (int i = 0; i < 4; i++)
            {
                Press(app, Tab);
                var f = Focused(app);
                Assert.True(f is "dialog.confirm" or "dialog.cancel", "탭이 대화상자 밖으로 나갔다: " + f);
            }
            Press(app, Escape);
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => !o, "Esc → 닫힘");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "settings.records.change", "누른 단추로 초점 복귀");
            Assert.Equal("Normal", M9SettingsTests.RecState(app.Diag()));         // 취소는 아무것도 옮기지 않는다

            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "다시 열림");
            TabTo(app, "dialog.confirm", 4);
            Press(app, Enter);
            app.WaitDiag(M9SettingsTests.RecState, s => s is "Done" or "Partial" or "Failed", "옮기기 끝");
            Assert.Equal("Done", M9SettingsTests.RecState(app.Diag()));
            Assert.Equal(0, app.Close());
        }
        finally
        {
            AppDriver.DeleteUserData(userData);
            AppDriver.DeleteUserData(dest);
        }
    }

    /// <summary>M9 C1 키보드: 처음 숨김 안내 — Tab은 세 단추 안에 갇히고, Esc는 취소(측정 계속), [측정 멈추고 종료]는 Enter로.</summary>
    [Fact]
    public void HintDialogKeepsTabInsideAndTheKeyboardStopsAndExits()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
        try
        {
            using var app = LaunchAny(userData, ["--demo", "steady", "--start", "--demo-hold", "5", "--close-to-tray", "on", "--freeze-motion"]);
            app.WaitDiag(d => d.GetProperty("host").GetString(), h => h == "Measuring", "측정 중");
            app.RequestClose();
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "처음 안내");
            for (int i = 0; i < 6; i++)
            {
                Press(app, Tab);
                var f = Focused(app);
                Assert.True(f is "dialog.confirm" or "dialog.alternate" or "dialog.cancel", "탭이 대화상자 밖으로 나갔다: " + f);
            }
            Press(app, Escape);
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => !o, "Esc → 취소");
            Assert.True(app.WindowVisible);
            Assert.Equal("Measuring", app.Diag().GetProperty("host").GetString());
            app.RequestClose();
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "다시 안내");
            TabTo(app, "dialog.alternate", 6);
            Press(app, Enter);
            Assert.Equal(0, app.WaitExit(TimeSpan.FromSeconds(20)));
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>M9 C1 키보드: 프로필 편집기 대상 방식 묶음(Tab으로 들어가 화살표·Space) → 포트 칸이 다음 Tab, 저장(Enter) → 구성에 TCP 443.</summary>
    [Fact]
    public void KeyboardAloneSwitchesATargetToTcp()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using (var app = LaunchAny(userData, ["--demo", "steady", "--screen", "profile", "--freeze-motion"]))
            {
                app.WaitDiag(d => d.GetProperty("screen").GetString(), s => s == "profile", "프로필 편집기");
                TabToPrefix(app, "profile.target.1.method.");
                for (int i = 0; i < 3 && Focused(app) != "profile.target.1.method.tcp"; i++) Press(app, Right);
                Assert.Equal("profile.target.1.method.tcp", Focused(app));
                Press(app, Space);
                AppDriver.WaitFor(() => app.Exists("profile.target.1.port"), b => b, "포트 칸");
                Press(app, Tab);
                Assert.Equal("profile.target.1.port", Focused(app));
                app.WaitDiag(d => Sd(d).GetProperty("saveEnabled").GetBoolean(), b => b, "저장 켜짐");
                TabTo(app, "profile.save");
                Press(app, Enter);
                app.WaitDiag(d => Sd(d).GetProperty("dirty").GetBoolean(), b => !b, "저장됨 (Enter)");
                Assert.Equal(0, app.Close());
            }
            var set = NetPulse.Core.Targets.ProfileStore.Load(userData, DateTimeOffset.Now).Set!;
            var target = set.Profiles.Single().Targets[1];
            Assert.Equal(NetPulse.Core.Targets.ProbeMethodKind.Tcp, target.Method.Kind);
            Assert.Equal(443, target.Method.Port);
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>M9 C1 키보드: 트레이 메뉴(Win32 팝업)는 Esc로 닫히고, 화살표로 "증상 표시"에 가서 Enter로 고른다.</summary>
    [Fact]
    public void TrayMenuWorksWithTheKeyboard()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "settings.json"), """{"version": 2, "resident": {"closeToTray": true, "trayHintShown": true}}""");
        try
        {
            using var app = LaunchAny(userData, ["--demo", "steady", "--demo-speed", "20", "--start", "--close-to-tray", "on", "--freeze-motion"]);
            app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 3, "3회차");
            app.PostTray(AppDriver.WmContextMenu);
            AppDriver.WaitFor(() => app.TrayMenuItems(), i => i.Count >= 3, "트레이 메뉴");
            Press(app, Escape);
            AppDriver.WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, app.ProcessId), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu))) is null, b => b, "Esc → 메뉴 닫힘");
            Assert.Equal(0, app.Diag().GetProperty("marks").GetArrayLength());

            app.PostTray(AppDriver.WmContextMenu);
            AppDriver.WaitFor(() => app.TrayMenuItems(), i => i.Count >= 3, "다시 메뉴");
            string Highlighted() { try { return AutomationElement.FocusedElement?.Current.Name ?? ""; } catch (ElementNotAvailableException) { return ""; } }
            for (int i = 0; i < 5 && Highlighted() != "증상 표시"; i++) Press(app, Down);
            Assert.Equal("증상 표시", Highlighted());
            Press(app, Enter);
            app.WaitDiag(d => d.GetProperty("marks").GetArrayLength(), n => n == 1, "증상 표시 (Enter)");
            Assert.Equal((0, 0), app.Exit());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ArrowsMoveThroughRecordRowsAndFocusComesBackPastTheFirstRows()
    {
        // 목록 위·아래 화살표로 줄 이동(설계 4.4), 9번째 줄 뒤(다음 프레임에 생기는 줄)를 열고 돌아와도 그 줄로 초점 (G4 M7 1라운드 b6·b10)
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "rec-many");
        try
        {
            using var app = LaunchAny(userData, [.. M5Fixtures.Args("--demo steady --screen records"), "--freeze-motion"]);
            app.WaitDiag(d => Sd(d).ValueKind == System.Text.Json.JsonValueKind.Object && Sd(d).TryGetProperty("listed", out var l) && l.GetBoolean()
                && Sd(d).GetProperty("analyzing").GetInt32() == 0, b => b, "기록 목록");
            TabTo(app, "records.row.0");
            for (int i = 0; i < 10; i++) Press(app, Down);
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.row.10", "화살표로 11번째 줄");
            Press(app, Up);
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.row.9", "위 화살표");
            Press(app, Enter);
            app.WaitDiag(d => Sd(d).TryGetProperty("detailComplete", out var c) && c.GetBoolean(), b => b, "상세");
            Press(app, Escape);
            app.WaitDiag(d => Sd(d).GetProperty("mode").GetString(), m => m == "List", "Esc → 목록");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.row.9", "연 줄(10번째)로 초점 복귀");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void EpisodeRowKeepsFocusWhenItOpensAndCloses()
    {
        // 끊김 줄 Enter로 펼치고 다시 Enter로 접어도 초점은 그 줄 (줄은 아래 카드와 함께 다음 프레임에 다시 생긴다, G4 M7 1라운드 b6)
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "rec-mixed");
        try
        {
            using var app = LaunchAny(userData, [.. M5Fixtures.Args("--demo steady --screen records"), "--freeze-motion"]);
            app.WaitDiag(d => Sd(d).ValueKind == System.Text.Json.JsonValueKind.Object && Sd(d).TryGetProperty("listed", out var l) && l.GetBoolean()
                && Sd(d).GetProperty("analyzing").GetInt32() == 0, b => b, "기록 목록");
            TabTo(app, "records.row.0");
            Press(app, Enter);
            app.WaitDiag(d => Sd(d).TryGetProperty("detailComplete", out var c) && c.GetBoolean(), b => b, "상세");
            TabTo(app, "records.episode.0", 80);
            Press(app, Enter);
            app.WaitDiag(d => Sd(d).GetProperty("detail").GetProperty("expanded").GetInt32(), n => n == 0, "펼침");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.episode.0", "펼친 뒤 초점");
            Press(app, Enter);
            app.WaitDiag(d => Sd(d).GetProperty("detail").GetProperty("expanded").GetInt32(), n => n == -1, "접음");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.episode.0", "접은 뒤 초점");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ClickingAnOutageOnTheTimelineOpensThatEpisodeRow()
    {
        // 타임라인의 끊김 자리를 누르면 그 끊김 줄이 펼쳐지고 초점이 간다 (설계 4.1, G4 M7 1라운드 b10). 실제 마우스 입력이므로 전경 확인
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "rec-mixed");
        try
        {
            using var app = LaunchAny(userData, [.. M5Fixtures.Args("--demo steady --screen records"), "--freeze-motion"]);
            app.WaitDiag(d => Sd(d).ValueKind == System.Text.Json.JsonValueKind.Object && Sd(d).TryGetProperty("listed", out var l) && l.GetBoolean()
                && Sd(d).GetProperty("analyzing").GetInt32() == 0, b => b, "기록 목록");
            app.Invoke("records.row.0");
            var d = app.WaitDiag(x => x, x => Sd(x).TryGetProperty("detailComplete", out var c) && c.GetBoolean(), "상세");
            string id = Sd(d).GetProperty("detail").GetProperty("Id").GetString()!;
            var record = NetPulse.Core.Records.RecordReader.Read(NetPulse.Core.Records.RecordReader.FindSegments(id, M5Fixtures.RecordRoots(userData)));
            var analysis = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(record);
            var e = analysis.Episodes[0];
            long start = record.Rounds.First(r => r.Round >= e.FirstRound).AtMs, end = record.Rounds.Last(r => r.Round <= e.LastRound).AtMs;
            var parts = Sd(d).GetProperty("detail").GetProperty("window").GetString()!.Trim('(', ')').Split(',')
                .Select(p => long.Parse(p.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            double fraction = ((start + end) / 2.0 - parts[0]) / (parts[1] - parts[0]);
            ClickAt(app, "records.timeline", fraction, 0.6);
            app.WaitDiag(x => Sd(x).GetProperty("detail").GetProperty("expanded").GetInt32(), n => n == 0, "누른 끊김 펼침");
            app.WaitDiag(x => x.GetProperty("focused").ToString(), f => f == "records.episode.0", "그 끊김 줄로 초점");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>요소 안 (가로·세로 비율) 자리를 실제 마우스로 누른다. 보내기 직전 전경이 대상 앱인지 확인한다.</summary>
    private static void ClickAt(AppDriver app, string automationId, double fx, double fy)
    {
        var r = app.Find(automationId).Current.BoundingRectangle;
        int x = (int)Math.Round(r.Left + r.Width * fx), y = (int)Math.Round(r.Top + r.Height * fy);
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != app.ProcessId) throw new InvalidOperationException("미완료(전경 아님): 마우스 입력을 보내지 않았습니다.");
        int sw = Native.GetSystemMetrics(78), sh = Native.GetSystemMetrics(79), sx = Native.GetSystemMetrics(76), sy = Native.GetSystemMetrics(77);   // 가상 화면
        int ax = (int)((x - sx) * 65535.0 / Math.Max(1, sw - 1)), ay = (int)((y - sy) * 65535.0 / Math.Max(1, sh - 1));
        const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, Absolute = 0x8000, VirtualDesk = 0x4000;
        Native.INPUT M(uint flags) => new() { type = 0, u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags | Absolute | VirtualDesk } } };
        var inputs = new[] { M(MouseMove), M(LeftDown), M(LeftUp) };
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) != inputs.Length) throw new InvalidOperationException("마우스 입력을 보내지 못했습니다.");
        Thread.Sleep(200);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion u; }

        public static INPUT Key(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } } };

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    }
}
