<#
  프로세스의 주 창을 PNG로 캡처한다. PrintWindow(PW_RENDERFULLCONTENT)를 써서
  DirectComposition으로 그려진 내용(WebView2, WinUI)까지 담는다. 창 뒤 재질은 담기지 않을 수 있다.
#>
param([Parameter(Mandatory)] [int] $ProcessId, [Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
}
"@
$p = Get-Process -Id $ProcessId
$deadline = (Get-Date).AddSeconds(15)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $p.Refresh() }
if ($p.MainWindowHandle -eq 0) { throw "창을 찾지 못했습니다." }
$h = $p.MainWindowHandle
$r = New-Object WinCap+RECT
[void][WinCap]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($h, $dc, 2)
$g.ReleaseHdc($dc); $g.Dispose()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"captured=$ok size=${w}x${hgt}"
