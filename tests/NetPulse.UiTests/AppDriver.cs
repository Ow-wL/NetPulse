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
    private bool closed;

    private AppDriver(Process process, AutomationElement window, string userData, bool ownsUserData)
    {
        this.process = process;
        Window = window;
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

    /// <summary>사용자 데이터 폴더를 지정해 띄운다 (설정 유지 시험). null이면 새 임시 폴더.</summary>
    public static AppDriver LaunchWith(string? userData, params string[] args)
    {
        bool owns = userData is null;
        userData ??= NewUserData();
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.ArgumentList.Add("--user-data");
        info.ArgumentList.Add(userData);
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
            if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); }
            throw new TimeoutException($"창이 나타나지 않았습니다 (종료 여부 {process.HasExited}).");
        }
        return new AppDriver(process, window, userData, owns);
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

    public T WaitDiag<T>(Func<JsonElement, T> read, Func<T, bool> until, string what) =>
        WaitFor(() => { try { return read(Diag()); } catch (JsonException) { return default!; } catch (ElementNotAvailableException) { return default!; } }, until, what);

    public void Select(string automationId) =>
        ((SelectionItemPattern)Find(automationId).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public void Invoke(string automationId) =>
        ((InvokePattern)Find(automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public void Toggle(string automationId) =>
        ((TogglePattern)Find(automationId).GetCurrentPattern(TogglePattern.Pattern)).Toggle();

    public bool IsOn(string automationId) =>
        ((TogglePattern)Find(automationId).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    /// <summary>자체 시험 결과가 기록에 나올 때까지 기다린다.</summary>
    public JsonElement WaitSelfTest(string kind) =>
        WaitFor(() => ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "selftest" && e.GetProperty("data").GetProperty("kind").GetString() == kind),
            e => e.ValueKind == JsonValueKind.Object, "자체 시험 " + kind).GetProperty("data");

    /// <summary>창 닫기 버튼과 같은 경로로 닫고, 끝날 때까지 기다려 종료 코드를 돌려준다.</summary>
    public int Close()
    {
        ((WindowPattern)Window.GetCurrentPattern(WindowPattern.Pattern)).Close();
        if (!process.WaitForExit(15000)) { process.Kill(); process.WaitForExit(10000); throw new TimeoutException("창을 닫았지만 15초 안에 끝나지 않았습니다."); }
        closed = true;
        return process.ExitCode;
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
        if (!closed && !process.HasExited) { process.Kill(); process.WaitForExit(10000); }
        process.Dispose();
        if (ownsUserData) DeleteUserData(UserData);
    }

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
    }
}
