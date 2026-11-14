using System.Diagnostics;
using NetPulse.Core.Live;

namespace NetPulse.Platform;

/// <summary>
/// 실제 시스템 동작 (M6 설계 3.3). 고정 명령만 실행한다: 사용자 입력이 명령줄에 들어가지 않는다.
/// 시험·캡처는 이 구현을 쓰지 않는다 (데모 세계의 <see cref="DemoSystemActions"/>).
/// </summary>
public sealed class SystemActions : ISystemActions
{
    private static readonly TimeSpan FlushLimit = TimeSpan.FromSeconds(10);

    /// <summary>설정 열기에 쓰는 고정 주소 셋.</summary>
    private static readonly IReadOnlyDictionary<SettingsPage, string> Pages = new Dictionary<SettingsPage, string>
    {
        [SettingsPage.Network] = "ms-settings:network-status",
        [SettingsPage.Wifi] = "ms-settings:network-wifi",
        [SettingsPage.Proxy] = "ms-settings:network-proxy",
    };

    /// <summary>
    /// <c>%SystemRoot%\System32\ipconfig.exe /flushdns</c>: 셸 없이, 창 없이, 관리자 권한을 요청하지 않는다.
    /// 출력은 읽어 버리고 종료 코드만 본다. 10초 넘거나 취소되면 프로세스 트리를 끝내고 기다려 회수한다.
    /// </summary>
    public async Task<SystemActionResult> FlushDnsAsync(CancellationToken ct)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
        if (!File.Exists(exe)) return new(false, "missing-tool");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("/flushdns");
        Process? p;
        try { p = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(false, "start-failed"); }
        if (p is null) return new(false, "start-failed");
        using (p)
        {
            var drainOut = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var drainErr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(FlushLimit);
            try
            {
                await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return new(false, ct.IsCancellationRequested ? "cancelled" : "timeout");
            }
            await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
            return p.ExitCode == 0 ? new(true) : new(false, "exit-" + p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public SystemActionResult OpenSettings(SettingsPage page)
    {
        if (!Pages.TryGetValue(page, out var uri)) return new(false, "not-allowed");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return new(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(false, "start-failed"); }
    }

    public SystemActionResult OpenFolder(string folder, IReadOnlyList<string> allowedRoots)
    {
        if (FolderPolicy.Check(folder, allowedRoots) is { } code) return new(false, code);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        info.ArgumentList.Add(Path.GetFullPath(folder));
        try
        {
            using var _ = Process.Start(info);
            return new(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(false, "start-failed"); }
    }
}
