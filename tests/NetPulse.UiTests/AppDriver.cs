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
/// </summary>
public sealed class AppDriver : IDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);   // 기획서 4.9 단계당 60초
    private readonly Process process;
    private bool closed;

    private AppDriver(Process process, AutomationElement window, string userData)
    {
        this.process = process;
        Window = window;
        UserData = userData;
    }

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

    public static AppDriver Launch(params string[] args)
    {
        var userData = Path.Combine(Path.GetTempPath(), "netpulse-ui-" + Guid.NewGuid().ToString("N")[..8]);
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.ArgumentList.Add("--user-data");
        info.ArgumentList.Add(userData);
        var process = Process.Start(info) ?? throw new InvalidOperationException("실행하지 못했습니다.");
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
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
        return new AppDriver(process, window, userData);
    }

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
        var hwnd = new IntPtr(Window.Current.NativeWindowHandle);
        Native.SetForegroundWindow(hwnd);
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
        using var client = full.Clone(rect, full.PixelFormat);
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
        return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToArray() : [];
    }

    public void Dispose()
    {
        if (!closed && !process.HasExited) { process.Kill(); process.WaitForExit(10000); }
        process.Dispose();
        try { Directory.Delete(UserData, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    }
}
