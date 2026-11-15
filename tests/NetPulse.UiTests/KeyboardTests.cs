using System.Drawing;
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
    private const ushort Tab = 0x09, Enter = 0x0D, Escape = 0x1B, Space = 0x20, Down = 0x28, Shift = 0x10;

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
    }
}
