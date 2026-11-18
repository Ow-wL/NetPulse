<#
.SYNOPSIS
  앱·트레이 아이콘을 만든다 (M9 설계 2절). 결과 src/NetPulse.App/Assets/NetPulse.ico를 커밋한다.

.DESCRIPTION
  흑요석 바탕(다크 팔레트 Bg #050507)의 둥근 사각형 위에 강조색(#7dd3fc) 맥박 선.
  크기 16·20·24·32·48·64·256을 PNG로 그려 ICO 하나로 묶는다 (Windows Vista 이후 PNG 항목 지원).
  작은 크기는 선을 굵게, 꺾임을 줄여 형태가 뭉개지지 않게 한다. -Preview 폴더를 주면 크기별 PNG를
  밝은·어두운 바탕 위에 그린 견본도 남긴다 (G5 아이콘 검사·사용자 보고용).
  Windows PowerShell 5.1(System.Drawing)에서 돈다. 같은 입력이면 같은 결과.
#>
param(
    [string] $Out = (Join-Path $PSScriptRoot '..\src\NetPulse.App\Assets\NetPulse.ico'),
    [string] $Preview = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 48, 64, 256
$bg = [System.Drawing.Color]::FromArgb(255, 0x05, 0x05, 0x07)
$edge = [System.Drawing.Color]::FromArgb(64, 255, 255, 255)
$accent = [System.Drawing.Color]::FromArgb(255, 0x7d, 0xd3, 0xfc)

function New-RoundRect([single] $x, [single] $y, [single] $w, [single] $h, [single] $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int] $s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $inset = [single]([Math]::Max(0.5, $s * 0.03))
    $rect = New-RoundRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ([single]($s * 0.22))
    $g.FillPath((New-Object System.Drawing.SolidBrush($bg)), $rect)
    $edgePen = New-Object System.Drawing.Pen($edge, [single]([Math]::Max(1.0, $s / 64.0)))
    $g.DrawPath($edgePen, $rect)
    # 맥박 선: 작은 크기는 꺾임 하나(형태 유지), 큰 크기는 두 번
    if ($s -le 24) {
        $pts = @(@(0.16, 0.56), @(0.38, 0.56), @(0.50, 0.26), @(0.62, 0.74), @(0.70, 0.56), @(0.84, 0.56))
        $width = [single]([Math]::Max(1.6, $s * 0.11))
    } else {
        $pts = @(@(0.14, 0.55), @(0.32, 0.55), @(0.41, 0.36), @(0.51, 0.74), @(0.61, 0.24), @(0.70, 0.55), @(0.86, 0.55))
        $width = [single]($s * 0.075)
    }
    $points = foreach ($p in $pts) { New-Object System.Drawing.PointF([single]($p[0] * $s), [single]($p[1] * $s)) }
    $pen = New-Object System.Drawing.Pen($accent, $width)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLines($pen, [System.Drawing.PointF[]]$points)
    $g.Dispose()
    return $bmp
}

$images = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @($s, $ms.ToArray())
    if ($Preview) {
        New-Item -ItemType Directory -Force $Preview | Out-Null
        foreach ($pair in @(@('light', [System.Drawing.Color]::FromArgb(255, 0xf3, 0xf3, 0xf3)), @('dark', [System.Drawing.Color]::FromArgb(255, 0x20, 0x20, 0x20)))) {
            $pad = [Math]::Max(4, [int]($s / 4))
            $card = New-Object System.Drawing.Bitmap(($s + 2 * $pad), ($s + 2 * $pad))
            $cg = [System.Drawing.Graphics]::FromImage($card)
            $cg.Clear($pair[1])
            $cg.DrawImageUnscaled($bmp, $pad, $pad)
            $cg.Dispose()
            $card.Save((Join-Path $Preview "icon-$s-$($pair[0]).png"), [System.Drawing.Imaging.ImageFormat]::Png)
            $card.Dispose()
        }
    }
    $bmp.Dispose()
}

# ICO: 머리(6) + 항목(16 × n) + PNG 데이터
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
$w.Flush()
$dir = Split-Path $Out -Parent
New-Item -ItemType Directory -Force $dir | Out-Null
[System.IO.File]::WriteAllBytes((Resolve-Path $dir).Path + '\' + (Split-Path $Out -Leaf), $ico.ToArray())
"아이콘: $Out ($($images.Count)개 크기, $($ico.Length) bytes)"
