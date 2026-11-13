<#
  프로세스의 주 창을 PNG로 캡처한다. 창을 복원하고 앞으로 가져온 뒤, PrintWindow(PW_RENDERFULLCONTENT)로
  DirectComposition 내용(WebView2, WinUI)까지 담는다. 창 크기가 비정상이면 실패한다.
#>
param([Parameter(Mandatory)] [int] $ProcessId, [Parameter(Mandatory)] [string] $Out, [int] $MinWidth = 800, [int] $MinHeight = 600)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('WinCap' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@
}
$p = Get-Process -Id $ProcessId
$deadline = (Get-Date).AddSeconds(15)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $p.Refresh() }
if ($p.MainWindowHandle -eq 0) { throw "창을 찾지 못했습니다." }
$h = $p.MainWindowHandle
if ([WinCap]::IsIconic($h)) { [void][WinCap]::ShowWindow($h, 9); Start-Sleep -Milliseconds 600 }
[void][WinCap]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 300
$r = New-Object WinCap+RECT
if (-not [WinCap]::GetWindowRect($h, [ref]$r)) { throw "창 위치를 읽지 못했습니다." }
$w = $r.R - $r.L; $hgt = $r.B - $r.T
if ($w -lt $MinWidth -or $hgt -lt $MinHeight) { throw "창 크기가 비정상입니다: ${w}x${hgt}" }
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($h, $dc, 2)
$g.ReleaseHdc($dc); $g.Dispose()
if (-not $ok) { $bmp.Dispose(); throw "PrintWindow 실패" }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"captured size=${w}x${hgt}"
