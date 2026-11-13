<#
  흑요석 재현(C, --home)과 시안(HTML)을 같은 크기(1440x900)로 캡처해 비교한다.
  C는 창 전체를 찍은 뒤 클라이언트 영역(1440x900)만 잘라낸다.
#>
param([Parameter(Mandatory)] [string] $Exe, [Parameter(Mandatory)] [string] $OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
if (-not ('ClientRect' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ClientRect {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
function Shot($p, [string] $Name) {
    $p.Refresh(); $h = $p.MainWindowHandle
    [void][ClientRect]::SetForegroundWindow($h)
    $w = New-Object ClientRect+RECT; [void][ClientRect]::GetWindowRect($h, [ref]$w)
    $c = New-Object ClientRect+RECT; [void][ClientRect]::GetClientRect($h, [ref]$c)
    $o = New-Object ClientRect+POINT; [void][ClientRect]::ClientToScreen($h, [ref]$o)
    $bmp = New-Object System.Drawing.Bitmap ($w.R - $w.L), ($w.B - $w.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
    if (-not [ClientRect]::PrintWindow($h, $dc, 2)) { throw 'PrintWindow 실패' }
    $g.ReleaseHdc($dc); $g.Dispose()
    $crop = $bmp.Clone((New-Object System.Drawing.Rectangle ($o.X - $w.L), ($o.Y - $w.T), $c.R, $c.B), $bmp.PixelFormat)
    $crop.Save((Join-Path $OutDir "$Name.png")); Write-Host "$Name $($c.R)x$($c.B)"
    $crop.Dispose(); $bmp.Dispose()
}
function Run([string[]] $SpikeArgs, [string] $Name, [int] $Wait = 4) {
    $p = Start-Process $Exe -ArgumentList (@('--home', '--run-seconds', '30') + $SpikeArgs) -PassThru
    Start-Sleep -Seconds $Wait
    Shot $p $Name
    return $p
}
foreach ($set in @(
    @{ a = @('--chrome', '0'); n = 'c-dark' },
    @{ a = @('--chrome', '0', '--theme', 'light'); n = 'c-light' },
    @{ a = @('--chrome', '0', '--aurora', 'on'); n = 'c-dark-aurora' },
    @{ a = @('--chrome', '0', '--theme', 'light', '--aurora', 'on'); n = 'c-light-aurora' },
    @{ a = @('--chrome', '0', '--state', 'problem'); n = 'c-dark-problem' },
    @{ a = @('--chrome', '0', '--reduce-transparency', '--reduce-motion'); n = 'c-dark-reduced' },
    @{ a = @('--chrome', '0', '--aurora', 'on', '--reduce-transparency'); n = 'c-dark-aurora-reduced' })) {
    $p = Run $set.a $set.n
    Stop-Process -Id $p.Id -Force
}
# 테마 원형 확산: 조작 막대의 라이트 버튼을 눌러 여러 시점을 찍는다
$p = Run @() 'c-transition-000ms' 4
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'theme-light')))
$sw = [Diagnostics.Stopwatch]::StartNew()
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
foreach ($ms in 250, 400, 550, 900) { while ($sw.ElapsedMilliseconds -lt $ms) { Start-Sleep -Milliseconds 5 }; Shot $p ("c-transition-{0:000}ms" -f $ms) }
Start-Sleep -Seconds 2
$aur = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'aurora-toggle')))
$aur.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1200
Shot $p 'c-light-aurora-toggled'
Stop-Process -Id $p.Id -Force
$log = Get-Content (Join-Path $env:LOCALAPPDATA 'NetPulseSpike\C-home\results.jsonl') -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }
($log | Where-Object kind -in 'switch', 'aurora') | ForEach-Object { "$($_.kind) " + ($_ | ConvertTo-Json -Compress) }
