<#
.SYNOPSIS
  제3자 고지 만들기·검사 (M10 설계 "고지 수집" [1-2][2-5]).

.DESCRIPTION
  출발점은 단일 파일 묶음에 실제로 들어가는 파일 목록(앱 csproj의 WriteBundleInputs가 publish 때 쓰는 bundle-inputs.tsv)이다.
  파일마다 출처를 정한다:
    - NuGet 패키지 캐시 아래 파일 → 그 패키지 (id, 버전)
    - 묶음의 실행 파일(NetPulse.exe = 단일 파일 호스트) → .NET 런타임 팩
    - 글꼴 → Pretendard (assets/fonts/OFL-Pretendard.txt)
    - 앱 자체 파일(NetPulse 어셈블리·설정·리소스·데모 시나리오) → NetPulse (LICENSE, 고지 목록에는 넣지 않음)
  출처를 못 정한 파일, 원문이 없는 출처가 하나라도 있으면 실패한다 (조용히 건너뛰지 않는다).
  패키지에 원문이 없으면 build/notices/<패키지 id>.txt (사람이 확인해 커밋한 원문)를 쓴다.
  Windows App SDK 구성요소가 있으면 같은 버전 Base 패키지의 NOTICE.txt(구성요소 전체의 제3자 고지)도 넣는다.

  -Write : src/NetPulse.App/Legal/THIRD-PARTY-NOTICES.txt를 새로 쓴다.
  -Check : 새로 만든 결과가 커밋된 파일과 같은지 본다 (다르면 종료 코드 1).
  결과 글에는 경로를 넣지 않는다 (패키지 이름·버전·원문 파일 이름만).
#>
param(
    [string] $BundleList = '',
    [switch] $Write,
    [switch] $Check
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$slash = [string][char]92
function Norm([string] $p) { $p.Replace($slash, '/') }

if (-not $BundleList) { $BundleList = Join-Path $root 'src/NetPulse.App/obj/Release/net10.0-windows10.0.22621.0/win-x64/bundle-inputs.tsv' }
if (-not (Test-Path $BundleList)) { throw "묶음 파일 목록이 없습니다. 먼저 Release 단일 파일 publish를 하세요: $BundleList" }
$target = Join-Path $root 'src/NetPulse.App/Legal/THIRD-PARTY-NOTICES.txt'

$nugetRoot = $env:NUGET_PACKAGES
if (-not $nugetRoot) {
    $line = (& dotnet nuget locals global-packages --list) | Select-Object -First 1
    $nugetRoot = ($line -replace '^\s*global-packages:\s*', '').Trim()
}
$nugetRoot = (Norm ([IO.Path]::GetFullPath($nugetRoot))).TrimEnd('/') + '/'
$repoRoot = (Norm $root).TrimEnd('/') + '/'

# 앱 자체 파일 (묶음 안 상대 경로, 대소문자 무시)
$ownPatterns = @('^NetPulse(\.(Core|Design|Platform))?\.dll$', '^NetPulse\.(deps|runtimeconfig)\.json$', '^NetPulse\.pri$', '^App\.xaml$', '^app\.manifest$',
    '^Assets/NetPulse\.ico$', '^demo/scenarios/[A-Za-z0-9-]+\.json$')

$components = [ordered]@{}   # 키 → @{ Name; Version; License; Texts = list of @{ File; Text } }
$unmapped = New-Object System.Collections.Generic.List[string]
$packages = @{}
$hasHost = $false
$hasFont = $false
foreach ($line in Get-Content $BundleList -Encoding UTF8) {
    if (-not $line.Trim()) { continue }
    $cols = $line.Split("`t")
    $rel = Norm $cols[0]
    $full = Norm ([IO.Path]::GetFullPath($cols[1]))
    if ($full.StartsWith($nugetRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $parts = $full.Substring($nugetRoot.Length).Split('/')
        $packages["$($parts[0].ToLowerInvariant())/$($parts[1])"] = $true
        continue
    }
    if ($rel -ieq 'NetPulse.exe') { $hasHost = $true; continue }
    if ($rel -match '^Fonts/(Pretendard-[A-Za-z]+\.otf|OFL-Pretendard\.txt)$') { $hasFont = $true; continue }
    $own = $false
    foreach ($pat in $ownPatterns) { if ($rel -imatch $pat) { $own = $true; break } }
    if ($own) { continue }
    $unmapped.Add($rel)
}
if ($unmapped.Count -gt 0) { throw "출처를 정하지 못한 묶음 파일이 있습니다: $($unmapped -join ', ')" }

function Read-Text([string] $path) { ([IO.File]::ReadAllText($path)).Replace("`r`n", "`n").Replace("`r", "`n").Trim() }
function License-Name([string] $nuspecPath, [string] $firstText) {
    [xml] $x = Get-Content $nuspecPath -Encoding UTF8 -Raw
    $meta = $x.package.metadata
    $lic = $meta.license
    if ($lic -and $lic.type -eq 'expression') { return [string]$lic.'#text' }
    if ($firstText -match '^\s*MICROSOFT SOFTWARE LICENSE TERMS') { return 'Microsoft Software License Terms' }
    if ($firstText -match '^\s*(The )?MIT License') { return 'MIT' }
    if ($firstText -match 'Redistribution and use in source and binary forms') { return 'BSD 스타일 (원문 참조)' }
    if ($firstText -match 'Microsoft Windows SDK 사용 조건') { return 'Microsoft Software License Terms (Windows SDK), C#/WinRT는 MIT' }
    return '원문 참조'
}

$runtimeVersion = $null
foreach ($key in ($packages.Keys | Sort-Object)) {
    $id, $ver = $key.Split('/')
    $dir = Join-Path $nugetRoot "$id/$ver"
    $nuspec = Get-ChildItem $dir -Filter *.nuspec | Select-Object -First 1
    [xml] $x = Get-Content $nuspec.FullName -Encoding UTF8 -Raw
    $name = [string]$x.package.metadata.id
    $texts = New-Object System.Collections.Generic.List[object]
    foreach ($f in (Get-ChildItem $dir -File | Where-Object { $_.Name -imatch '^(license|notice|third-party-notices)(\.[a-z]+)?$' } | Sort-Object Name)) {
        $texts.Add(@{ File = $f.Name; Text = (Read-Text $f.FullName) })
    }
    $override = Join-Path $root "build/notices/$id.txt"
    if (Test-Path $override) { $texts.Add(@{ File = "build/notices/$id.txt"; Text = (Read-Text $override) }) }
    if ($texts.Count -eq 0) { throw "원문이 없는 출처입니다: $name $ver (build/notices/$id.txt에 확인한 원문을 두세요)" }
    $components[$key] = @{ Name = $name; Version = $ver; License = (License-Name $nuspec.FullName $texts[0].Text); Texts = $texts }
    if ($id -eq 'microsoft.netcore.app.runtime.win-x64') { $runtimeVersion = $ver }
}
if ($hasHost -and -not $runtimeVersion) { throw '단일 파일 호스트가 있는데 .NET 런타임 팩이 묶음 목록에 없습니다.' }

# Windows App SDK 구성요소 전체의 제3자 고지 (Base의 NOTICE.txt)
$wasdk = @($components.Values | Where-Object { $_.Name -like 'Microsoft.WindowsAppSDK.*' } | Select-Object -First 1)
if ($wasdk.Count -gt 0) {
    $ver = $wasdk[0].Version
    $baseNotice = Join-Path $nugetRoot "microsoft.windowsappsdk.base/$ver/NOTICE.txt"
    if (-not (Test-Path $baseNotice)) { throw "Windows App SDK Base $ver의 NOTICE.txt가 없습니다." }
    $components['microsoft.windowsappsdk.base/' + $ver] = @{ Name = 'Microsoft.WindowsAppSDK (구성요소 전체의 제3자 고지)'; Version = $ver; License = '제3자 고지';
        Texts = @(@{ File = 'NOTICE.txt (Microsoft.WindowsAppSDK.Base)'; Text = (Read-Text $baseNotice) }) }
}
if ($hasFont) {
    $components['pretendard'] = @{ Name = 'Pretendard'; Version = '1.3.9'; License = 'SIL Open Font License 1.1';
        Texts = @(@{ File = 'OFL-Pretendard.txt'; Text = (Read-Text (Join-Path $root 'assets/fonts/OFL-Pretendard.txt')) }) }
}

# ---- 글 만들기 (결정적: 이름 순, LF, 같은 원문은 한 번만) ----
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append("NetPulse 제3자 고지 (THIRD-PARTY NOTICES)`n`n")
[void]$sb.Append("NetPulse 자체는 MIT 라이선스입니다(LICENSE). 이 배포 파일에는 아래 구성요소가 들어 있고, 각 구성요소에는 그 원문의 조건이 적용됩니다.`n")
[void]$sb.Append("이 파일은 build/notices.ps1이 단일 파일 묶음 목록에서 만듭니다.`n`n")
$ordered = @($components.Values | Sort-Object { $_.Name })
foreach ($c in $ordered) { [void]$sb.Append("- $($c.Name) $($c.Version) — $($c.License)`n") }
$seen = @{}
foreach ($c in $ordered) {
    [void]$sb.Append("`n=== 구성요소: $($c.Name) | $($c.Version) | $($c.License) ===`n")
    foreach ($t in $c.Texts) {
        $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($t.Text)))
        [void]$sb.Append("`n--- 원문: $($t.File) ---`n")
        if ($seen.ContainsKey($hash)) { [void]$sb.Append("(앞의 '$($seen[$hash])' 원문과 같습니다)`n") }
        else { $seen[$hash] = "$($c.Name) $($t.File)"; [void]$sb.Append($t.Text + "`n") }
    }
}
$text = $sb.ToString()
foreach ($bad in @($repoRoot, $nugetRoot, (Norm $env:USERPROFILE))) {
    if ($bad -and $text.IndexOf($bad.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw '고지 글에 로컬 경로가 들어갔습니다.' }
}

$utf8 = New-Object Text.UTF8Encoding($false)
if ($Write) {
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    [IO.File]::WriteAllText($target, $text, $utf8)
    Write-Host "고지: 구성요소 $($ordered.Count)개, $($text.Length)자 → src/NetPulse.App/Legal/THIRD-PARTY-NOTICES.txt"
}
if ($Check) {
    if (-not (Test-Path $target)) { Write-Host '고지 파일이 없습니다.'; exit 1 }
    $committed = ([IO.File]::ReadAllText($target)).Replace("`r`n", "`n")
    if ($committed -ne $text) { Write-Host '고지 파일이 묶음 목록과 맞지 않습니다. build/notices.ps1 -Write로 다시 만들고 검토해 커밋하세요.'; exit 1 }
    Write-Host "고지 일치: 구성요소 $($ordered.Count)개"
}
if (-not $Write -and -not $Check) { $ordered | ForEach-Object { "$($_.Name) $($_.Version) — $($_.License)" } }
