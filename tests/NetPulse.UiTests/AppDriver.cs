using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace NetPulse.UiTests;

/// <summary>
/// 빌드된 NetPulse를 띄우고 조작하는 도구.
/// - 실행 파일은 NETPULSE_EXE로만 받는다 (G1이 만든 바로 그 파일).
/// - 실행마다 새 사용자 데이터 폴더를 준다.
/// - 끝낼 때 프로세스가 완전히 끝날 때까지 기다린다 (M0 교훈: 덜 끝난 프로세스가 파일을 잡는다).
/// - 캡처는 창 전용(PrintWindow)이고 전경 전환을 하지 않는다. 조작은 UI Automation 패턴만 쓴다 (키 입력 없음, M2 설계 7.1).
/// </summary>
public sealed class AppDriver : IDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);   // 기획서 4.9 단계당 60초
    private readonly Process process;
    private readonly bool ownsUserData;
    private readonly DateTime startedUtc;
    private bool closed;
    private readonly IntPtr windowHandle;

    private AppDriver(Process process, AutomationElement window, string userData, bool ownsUserData, DateTime startedUtc)
    {
        this.process = process;
        this.startedUtc = startedUtc;
        Window = window;
        windowHandle = new IntPtr(window.Current.NativeWindowHandle);
        UserData = userData;
        this.ownsUserData = ownsUserData;
    }

    public int ProcessId => process.Id;

    public AutomationElement Window { get; }
    public string UserData { get; }

    public static string Executable =>
        Environment.GetEnvironmentVariable("NETPULSE_EXE") is { Length: > 0 } exe && File.Exists(exe)
            ? exe : throw new InvalidOperationException("NETPULSE_EXE가 없거나 파일이 없습니다. build/ui-test.ps1로 실행하세요.");

    public static string OutputDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("NETPULSE_UITEST_OUT") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "netpulse-uitest");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static AppDriver Launch(params string[] args) => LaunchWith(null, args);

    /// <summary>
    /// 사용자 데이터 폴더를 지정해 띄운다 (설정 유지 시험). null이면 새 임시 폴더.
    /// 측정 중 창 닫기가 지금처럼 저장 후 종료가 되도록 <c>--close-to-tray off</c>를 넣는다 (M9 설계 A7: 기존 시험 보존).
    /// 상주 시험은 인수에 직접 <c>--close-to-tray</c>를 주거나 <see cref="LaunchRaw"/>로 기본값을 쓴다.
    /// </summary>
    public static AppDriver LaunchWith(string? userData, params string[] args) => LaunchCore(userData, inject: !args.Contains("--close-to-tray"), args);

    /// <summary>인수를 넣지 않고 띄운다: 설정 파일의 기본값(측정 중 닫기 = 트레이)을 그대로 검증한다 (M9 설계 A7 [1-16]).</summary>
    public static AppDriver LaunchRaw(string? userData, params string[] args) => LaunchCore(userData, inject: false, args);

    private static AppDriver LaunchCore(string? userData, bool inject, string[] args)
    {
        bool owns = userData is null;
        userData ??= NewUserData();
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        if (inject && !args.Contains("--showcase")) { info.ArgumentList.Add("--close-to-tray"); info.ArgumentList.Add("off"); }
        info.ArgumentList.Add("--user-data");
        info.ArgumentList.Add(userData);
        var startedUtc = DateTime.UtcNow;
        var process = Process.Start(info) ?? throw new InvalidOperationException("실행하지 못했습니다.");
        var condition = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        AutomationElement? window = null;
        var deadline = DateTime.UtcNow + StepTimeout;
        while (window is null && DateTime.UtcNow < deadline && !process.HasExited)
        {
            window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
            if (window is null) Thread.Sleep(200);
        }
        if (window is null)
        {
            bool exited = process.HasExited;
            string? evidence = null;
            if (exited) evidence = CrashEvidence.Collect(process, userData, "start", startedUtc);
            else { process.Kill(); process.WaitForExit(10000); }
            throw new TimeoutException($"창이 나타나지 않았습니다 (스스로 끝남 {exited}" + (exited ? $", 종료 코드 0x{process.ExitCode:X8}, 증거 {evidence}" : "") + ").");
        }
        return new AppDriver(process, window, userData, owns, startedUtc);
    }

    public static string NewUserData() => Path.Combine(Path.GetTempPath(), "netpulse-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public AutomationElement Find(string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return WaitFor(() => Window.FindFirst(TreeScope.Descendants, condition), e => e is not null, $"요소 {automationId}")!;
    }

    public string Text(string automationId) => Find(automationId).Current.Name;

    /// <summary>값이 조건을 만족할 때까지 기다린다. 단계 제한시간을 넘기면 실패.</summary>
    public static T WaitFor<T>(Func<T> read, Func<T, bool> until, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? StepTimeout);
        T value = read();
        while (!until(value))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"기다리던 상태가 오지 않았습니다: {what} (마지막 값 {value})");
            Thread.Sleep(100);
            value = read();
        }
        return value;
    }

    /// <summary>창의 클라이언트 영역을 캡처해 저장하고 평균 밝기(0~1)를 돌려준다.</summary>
    public double CaptureClient(string name)
    {
        using var client = CaptureBitmap();
        client.Save(Path.Combine(OutputDirectory, name + ".png"), ImageFormat.Png);
        double sum = 0; int n = 0;
        for (int y = 0; y < client.Height; y += 7)
            for (int x = 0; x < client.Width; x += 7)
            {
                var p = client.GetPixel(x, y);
                sum += (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255; n++;
            }
        return sum / n;
    }

    /// <summary>창의 클라이언트 영역 (창 전용 캡처, 전경 전환 없음). 가려져 있어도 창 자신의 그림을 얻는다.</summary>
    public Bitmap CaptureBitmap()
    {
        var hwnd = new IntPtr(Window.Current.NativeWindowHandle);
        Native.GetWindowRect(hwnd, out var w);
        Native.GetClientRect(hwnd, out var c);
        var origin = new Native.POINT();
        Native.ClientToScreen(hwnd, ref origin);
        using var full = new Bitmap(w.Right - w.Left, w.Bottom - w.Top);
        using (var g = Graphics.FromImage(full))
        {
            var dc = g.GetHdc();
            bool ok = Native.PrintWindow(hwnd, dc, 2);
            g.ReleaseHdc(dc);
            if (!ok) throw new InvalidOperationException("PrintWindow 실패");
        }
        var rect = new Rectangle(origin.X - w.Left, origin.Y - w.Top, c.Right, c.Bottom);
        if (rect.Width < 800 || rect.Height < 600) throw new InvalidOperationException($"클라이언트 영역이 비정상입니다: {rect.Width}x{rect.Height}");
        return full.Clone(rect, PixelFormat.Format32bppArgb);
    }

    /// <summary>앱 프로세스의 다른 창 하나(트레이 팝업 메뉴 등)를 그 창만 캡처한다 (PrintWindow, 화면 영역 캡처 아님).</summary>
    public static Bitmap CaptureWindow(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var w);
        var bmp = new Bitmap(Math.Max(1, w.Right - w.Left), Math.Max(1, w.Bottom - w.Top));
        using var g = Graphics.FromImage(bmp);
        var dc = g.GetHdc();
        bool ok = Native.PrintWindow(hwnd, dc, 2);
        g.ReleaseHdc(dc);
        if (!ok) throw new InvalidOperationException("PrintWindow 실패");
        return bmp;
    }

    /// <summary>화면 좌표(UI Automation 경계)를 클라이언트 캡처 좌표로 옮긴다.</summary>
    public Rectangle ToClient(System.Windows.Rect screen)
    {
        var hwnd = new IntPtr(Window.Current.NativeWindowHandle);
        var origin = new Native.POINT();
        Native.ClientToScreen(hwnd, ref origin);
        return new Rectangle((int)Math.Round(screen.X - origin.X), (int)Math.Round(screen.Y - origin.Y), (int)Math.Round(screen.Width), (int)Math.Round(screen.Height));
    }

    /// <summary>캡처를 저장한다.</summary>
    public string Save(Bitmap bitmap, string name)
    {
        var path = Path.Combine(OutputDirectory, name + ".png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    // ---------- 진단과 조작 (UI Automation 패턴만) ----------

    /// <summary>앱이 내보낸 진단 값 (diag.state의 이름, JSON).</summary>
    public JsonElement Diag() => JsonDocument.Parse(Text("diag.state")).RootElement.Clone();

    /// <summary>
    /// 진단 값이 조건을 만족할 때까지. 읽는 순간의 중간 상태(빈 글, 아직 없는 칸, 다른 형식의 값)는 "아직"으로 보고 다시 읽는다
    /// (M8 게이트 1회차: 측정 시작 실패 장면의 빈 값 → NRE, 흐름 시험의 GetBoolean 형식 예외, M9 설계 C2).
    /// </summary>
    public T WaitDiag<T>(Func<JsonElement, T> read, Func<T, bool> until, string what) =>
        WaitFor(() =>
        {
            try { return read(Diag()); }
            catch (Exception ex) when (ex is JsonException or ElementNotAvailableException or KeyNotFoundException or InvalidOperationException
                or NullReferenceException or FormatException or IndexOutOfRangeException) { return default!; }
        }, until, what);

    public void Select(string automationId) =>
        ((SelectionItemPattern)Find(automationId).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public void Invoke(string automationId) =>
        ((InvokePattern)Find(automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>스크롤 영역을 세로 <paramref name="percent"/>%로 옮긴다 (스크롤할 것이 없으면 아무것도 하지 않음).</summary>
    public void ScrollTo(string automationId, double percent)
    {
        var pattern = (ScrollPattern)Find(automationId).GetCurrentPattern(ScrollPattern.Pattern);
        if (pattern.Current.VerticallyScrollable) pattern.SetScrollPercent(ScrollPattern.NoScroll, percent);
    }

    /// <summary>글 상자에 값을 넣는다 (값 패턴, 키 입력 없음).</summary>
    public void SetText(string automationId, string text) =>
        ((ValuePattern)Find(automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    /// <summary>요소가 지금 있는지 (기다리지 않음).</summary>
    /// <summary>
    /// 요소에 초점을 주고 실제 Enter 키를 보낸다. 키는 앞 창으로 가므로, 앞 창이 이 앱 창일 때만 보내고 아니면 실패한다
    /// (다른 프로그램에 키를 보내지 않는다).
    /// </summary>
    public void PressEnter(string automationId)
    {
        Find(automationId).SetFocus();
        var hwnd = new IntPtr(Window.Current.NativeWindowHandle);
        WaitFor(() => Native.GetForegroundWindow() == hwnd, f => f, "앱 창이 앞에 옴");
        // 누름·뗌을 한 번에 보낸다 (두 호출 사이에 전경이 바뀌는 틈을 없앤다). 보낸 개수도 확인한다.
        // 동시 사용자 입력과 완전히 떼어 놓는 것은 격리 세션만 할 수 있다. 앱이 받았는지는 시험이 앱 쪽 기록으로 확인한다.
        const ushort VkReturn = 0x0D;
        var inputs = new[] { Native.Key(VkReturn, false), Native.Key(VkReturn, true) };
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Enter를 보내지 못했습니다.");
    }

    public bool Exists(string automationId) =>
        Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)) is not null;

    /// <summary>열린 알림을 모두 닫는다 (캡처에서 알림이 화면을 가리지 않게).</summary>
    public void CloseToasts()
    {
        // 닫는 사이 새 알림이 올 수 있어 몇 번 되풀이한다
        for (int attempt = 0; attempt < 5; attempt++)
        {
            AutomationElementCollection all;
            // 훑는 사이 화면이 바뀌면(초점이 가며 지우기 단추가 생김 등) 요소가 사라져 던진다: 다시 훑는다 (M7 UI 시험 2차)
            try { all = Window.FindAll(TreeScope.Descendants, Condition.TrueCondition); }
            catch (ElementNotAvailableException) when (attempt < 4) { Thread.Sleep(100); continue; }
            foreach (AutomationElement e in all)
            {
                string id;
                try { id = e.Current.AutomationId; } catch (ElementNotAvailableException) { continue; }
                if (id.StartsWith("toast.", StringComparison.Ordinal) && id.EndsWith(".close", StringComparison.Ordinal))
                    try { ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch (ElementNotAvailableException) { }
            }
            try
            {
                WaitFor(() => Diag().GetProperty("toasts").GetInt32(), n => n == 0, "알림 닫힘", TimeSpan.FromSeconds(3));
                return;
            }
            catch (TimeoutException) when (attempt < 4) { }
        }
    }

    /// <summary>지금 화면의 배치 검사를 남기고 그 결과를 읽는다 (시험 전용 단추 diag.layout).</summary>
    public JsonElement LayoutNow()
    {
        int before = ReadLog().Count(e => e.GetProperty("kind").GetString() == "selftest");
        Invoke("diag.layout");
        return WaitFor(() => ReadLog().Where(e => e.GetProperty("kind").GetString() == "selftest").Skip(before).FirstOrDefault(),
            e => e.ValueKind == JsonValueKind.Object, "배치 검사").GetProperty("data");
    }

    public void Toggle(string automationId) =>
        ((TogglePattern)Find(automationId).GetCurrentPattern(TogglePattern.Pattern)).Toggle();

    public bool IsOn(string automationId) =>
        ((TogglePattern)Find(automationId).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    /// <summary>자체 시험 결과가 기록에 나올 때까지 기다린다.</summary>
    public JsonElement WaitSelfTest(string kind) =>
        WaitFor(() => ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "selftest" && e.GetProperty("data").GetProperty("kind").GetString() == kind),
            e => e.ValueKind == JsonValueKind.Object, "자체 시험 " + kind).GetProperty("data");

    /// <summary>앱이 스스로 닫힐 때까지 기다린다 (벤치처럼 끝나면 닫히는 실행).</summary>
    public int WaitExit(TimeSpan timeout)
    {
        if (!process.WaitForExit((int)timeout.TotalMilliseconds)) throw new TimeoutException("앱이 스스로 끝나지 않았습니다.");
        closed = true;
        FailIfAbnormal("waitexit");
        return process.ExitCode;
    }

    // ---------------- 상주 (M9) ----------------

    /// <summary>프로세스가 살아 있는가.</summary>
    public bool Alive => !process.HasExited;

    /// <summary>창 닫기 요청만 보낸다 (숨김이면 프로세스는 남는다).</summary>
    public void RequestClose() => ((WindowPattern)Window.GetCurrentPattern(WindowPattern.Pattern)).Close();

    /// <summary>같은 사용자 폴더로 실행 파일을 하나 더 띄워 끝날 때까지 기다리고 그 종료 코드 (두 번째 실행, A5).</summary>
    public int RunSecond(params string[] args)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.ArgumentList.Add("--user-data");
        info.ArgumentList.Add(UserData);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("두 번째 실행을 띄우지 못했습니다.");
        if (!p.WaitForExit(20000)) { p.Kill(); throw new TimeoutException("두 번째 실행이 20초 안에 끝나지 않았습니다."); }
        return p.ExitCode;
    }

    /// <summary>
    /// <c>--exit</c>로 종료시킨다 (트레이 "종료"와 같은 경로): 두 번째 실행의 종료 코드와 함께 <b>이 프로세스가 끝났는지·종료 코드</b>까지 확인한다 [2-4].
    /// </summary>
    public (int Second, int First) Exit()
    {
        int second = RunSecond("--exit");
        if (!process.WaitForExit(15000))
        {
            process.Kill(); process.WaitForExit(10000);
            closed = true;
            string evidence = CrashEvidence.Collect(process, UserData, "exit-timeout", startedUtc);
            throw new TimeoutException($"--exit 뒤 15초 안에 끝나지 않았습니다 (두 번째 실행 {second}, 증거 {evidence}).");
        }
        closed = true;
        FailIfAbnormal("exit");
        return (second, process.ExitCode);
    }

    /// <summary>시험 주입으로 앱이 스스로 강제 종료되기를 기다린다 (<c>--move-fail kill-after</c>, 종료 코드는 보지 않는다).</summary>
    public void WaitKilled(TimeSpan timeout)
    {
        if (!process.WaitForExit((int)timeout.TotalMilliseconds)) throw new TimeoutException("앱이 강제 종료되지 않았습니다.");
        closed = true;
    }

    /// <summary>세션 종료 확정 뒤 Windows가 하는 일을 흉내: 프로세스를 끝낸다 (종료 코드는 보지 않는다).</summary>
    public void TerminateAfterSessionEnd()
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); }
        closed = true;
    }

    /// <summary>트레이 수신 창 (숨은 최상위 창, A3).</summary>
    public IntPtr TrayWindow() => WaitFor(() => Native.FindWindowW("NetPulse.Tray", "NetPulse.Tray." + process.Id), h => h != IntPtr.Zero, "트레이 수신 창");

    /// <summary>
    /// 트레이 콜백을 v4 형식으로 보낸다 (<c>WM_APP+1</c>, lParam = MAKELPARAM(사건, 1), wParam = 좌표).
    /// Shell을 거치지 않은 <b>처리기 시험</b>이다 (A7-1). 메뉴는 모달이라 비동기로 보낸다.
    /// </summary>
    public void PostTray(uint ev, int x = 200, int y = 200) =>
        Native.PostMessageW(TrayWindow(), 0x8000 + 1, (IntPtr)((y << 16) | (x & 0xFFFF)), (IntPtr)((1 << 16) | (int)ev));

    public const uint NinSelect = 0x0400, NinKeySelect = 0x0401, NinBalloonClick = 0x0405, WmContextMenu = 0x007B;

    /// <summary>세션 종료 메시지 (A6): 질의 → 확정/취소. SendMessage라 처리가 끝날 때까지 기다린다.</summary>
    public void SendSessionEnd(bool ending)
    {
        var h = TrayWindow();
        Native.SendMessageTimeoutW(h, 0x0011, IntPtr.Zero, IntPtr.Zero, 0x2, 10000, out _);
        Native.SendMessageTimeoutW(h, 0x0016, (IntPtr)(ending ? 1 : 0), IntPtr.Zero, 0x2, 10000, out _);
    }

    /// <summary>앱 프로세스의 트레이 팝업 메뉴 (데스크톱 UIA, 프로세스 Id 확인, A7-1).</summary>
    public AutomationElement TrayMenu()
    {
        var condition = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
        return WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, condition), e => e is not null, "트레이 메뉴")!;
    }

    /// <summary>트레이 메뉴 항목 (이름, 켜짐).</summary>
    public IReadOnlyList<(string Name, bool Enabled, AutomationElement Element)> TrayMenuItems()
    {
        var items = TrayMenu().FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
        return items.Cast<AutomationElement>().Select(e => (e.Current.Name, e.Current.IsEnabled, e)).ToArray();
    }

    /// <summary>트레이 메뉴 항목 고르기 (Invoke).</summary>
    public void PickTrayMenu(string name)
    {
        var item = TrayMenuItems().First(i => i.Name == name).Element;
        ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    /// <summary>창이 화면에 보이는가 (Win32 기준, 숨김이면 거짓).</summary>
    public bool WindowVisible => Native.IsWindowVisible(windowHandle);

    /// <summary>창 닫기 버튼과 같은 경로로 닫고, 끝날 때까지 기다려 종료 코드를 돌려준다.</summary>
    public int Close()
    {
        try { ((WindowPattern)Window.GetCurrentPattern(WindowPattern.Pattern)).Close(); }
        catch (ElementNotAvailableException) when (process.HasExited) { }   // 닫기 전에 이미 끝남 → 아래에서 종료 코드로 판정
        if (!process.WaitForExit(15000))
        {
            process.Kill(); process.WaitForExit(10000);
            closed = true;
            string evidence = CrashEvidence.Collect(process, UserData, "close-timeout", startedUtc);
            throw new TimeoutException($"창을 닫았지만 15초 안에 끝나지 않았습니다 (증거 {evidence}).");
        }
        closed = true;
        FailIfAbnormal("close");
        return process.ExitCode;
    }

    /// <summary>
    /// 비정상 종료(종료 코드 0이 아님)면 증거를 남기고 시험을 실패시킨다 (M6 설계 6.1: 모든 종료 경로).
    /// 결과를 돌려주는 경로(Close·WaitExit)가 0이 아닌 값을 조용히 돌려주지 않게 한다.
    /// </summary>
    private void FailIfAbnormal(string where)
    {
        if (process.ExitCode == 0) return;
        string evidence = CrashEvidence.Collect(process, UserData, where, startedUtc);
        throw new AppCrashedException($"앱이 비정상 종료했습니다 ({where}, 종료 코드 0x{process.ExitCode:X8}). 증거: {evidence}");
    }

    public IReadOnlyList<JsonElement> ReadLog()
    {
        var path = Path.Combine(UserData, "app-log.jsonl");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine.Last()).Where(l => l.Trim().Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
    }

    public void Dispose()
    {
        // 시험이 닫지 않았으면: 살아 있으면 시험 전용 종료 코드로 끝내고, 실제로 회수됐는지 확인한다 (G4 2·3라운드).
        // 정상은 0과 그 시험 전용 코드뿐이다. Kill()은 이미 끝난 프로세스에 예외 없이 돌아올 수 있어 "요청이 받아들여짐"이
        // 시험이 끝냈다는 증거가 못 된다. 종료 코드는 먼저 끝난 쪽의 것이 남으므로, 스스로 -1로 끝났으면 -1이 남아 비정상이 된다
        // (M6 G4 4라운드 이월 3). 회수되지 않으면 증거를 남기고 실패시킨다 (살아 있는 앱을 둔 채 성공처럼 끝내지 않는다).
        // 시험 본문이 이미 실패해 이 예외가 그것을 덮을 수 있지만, 그때도 원인은 대개 이 종료라 증거 경로를 앞에 둔다.
        string? crashed = null;
        bool alive = false;
        if (!closed)
        {
            string? killError = process.HasExited ? null : TerminateForTest(process);
            bool reaped = process.WaitForExit(10000);
            alive = !reaped;
            if (!reaped)
                crashed = $"앱을 끝내지 못했습니다 (강제 종료 {(killError is null ? "요청함" : "실패 " + killError)}, 10초 안에 회수 안 됨). 증거: {CrashEvidence.Collect(process, UserData, "not-reaped", startedUtc)}";
            else if (ExitProblem(process.ExitCode) is { } problem)
                crashed = $"앱이 시험 도중 비정상 종료했습니다 ({problem}). 증거: {CrashEvidence.Collect(process, UserData, "during-test", startedUtc)}";
        }
        process.Dispose();
        if (ownsUserData && !alive) DeleteUserData(UserData);   // 살아 있는 앱의 폴더는 지우지 않는다 (증거·사용 중)
        if (crashed is not null) throw new AppCrashedException(crashed);
    }

    /// <summary>시험이 강제로 끝낸 프로세스만 갖는 종료 코드 (앱은 이 값으로 끝나지 않는다).</summary>
    internal const int TestKillCode = 0x7E57C0DE;

    /// <summary>시험 전용 코드로 강제 종료를 요청한다. 실패면 그 오류 (이미 끝났으면 거부될 수 있고, 그래도 판정은 종료 코드로 한다).</summary>
    internal static string? TerminateForTest(Process p) =>
        Native.TerminateProcess(p.SafeHandle, unchecked((uint)TestKillCode)) ? null : "0x" + Marshal.GetLastWin32Error().ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>회수된 종료 코드 판정: 0(스스로 정상 종료)과 시험 전용 코드(시험이 끝냄)만 정상. 그 밖은 이유 글.</summary>
    internal static string? ExitProblem(int exitCode) =>
        exitCode is 0 or TestKillCode ? null : $"종료 코드 0x{exitCode:X8}, 스스로 끝남";

    public static void DeleteUserData(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string? className, string? title);
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
        public static INPUT Key(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } } };
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle h, uint exitCode);
    }
}
