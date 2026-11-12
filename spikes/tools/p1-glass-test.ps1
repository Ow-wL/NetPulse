<#
  P1 확인: 틴트를 낮춘 유리 패널 뒤로 그래프 선이 흐려져 보이는지 캡처한다.
  PrintWindow 캡처와 화면 복사 캡처를 둘 다 남긴다 (창이 앞에 있어야 하는 재질이 있을 수 있어서).
#>
param([Parameter(Mandatory)] [string] $Exe, [Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Fg {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
}
"@
$p = Start-Process $Exe -ArgumentList '--glass-test', '--run-seconds', '42' -PassThru
Start-Sleep -Seconds 34
$p.Refresh()
[void][Fg]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 800
& (Join-Path $PSScriptRoot 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir "p1-$Name-printwindow.png") | Out-Null
$r = New-Object Fg+RECT
[void][Fg]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$r, 16)
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
$bmp.Save((Join-Path $OutDir "p1-$Name-screen.png")); $bmp.Dispose()
[void]$p.WaitForExit(20000)
"captured $Name"
