<#
.SYNOPSIS
  깨끗한 클론에서 배포 파일 만들기 확인 (M10 설계 B5, 완성 정의 4).

.DESCRIPTION
  저장소를 임시 폴더로 git clone --no-local 한 뒤 주어진 커밋에서 문서화된 한 가지 절차(build/build.ps1 -Gate)를 돌리고,
  같은 배포 검사(Test-Release)를 한다. 결과 요약은 .loop/clean-clone-<sha10>.json (공개 안 함).
  저장소 산출물(artifacts/<sha10>)이 있으면 exe·zip 해시가 같은지도 적는다. 같지 않아도 실패는 아니다 (바이트 재현은 요구하지 않음).
  임시 폴더는 끝나면 지운다.
#>
param([Parameter(Mandatory)] [string] $Commit)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
. (Join-Path $root 'build\common.ps1')

$sha = (git rev-parse --verify "$Commit^{commit}").Trim()
$short = $sha.Substring(0, 10)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('netpulse-clone-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$started = Get-Date
$result = [ordered]@{ commit = $sha; startedAt = $started.ToString('o') }
try {
    & git clone --no-local --quiet $root $tmp
    if ($LASTEXITCODE -ne 0) { throw 'git clone 실패' }
    Push-Location $tmp
    try {
        & git -c advice.detachedHead=false checkout --quiet $sha
        if ($LASTEXITCODE -ne 0) { throw "checkout 실패: $sha" }
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tmp 'build\build.ps1') -Gate
        if ($LASTEXITCODE -ne 0) { throw '깨끗한 클론에서 build.ps1 -Gate 실패' }
        $artifacts = Join-Path $tmp "artifacts\$short"
        $info = Test-Release $artifacts $sha
        $result.version = $info.version
        $result.files = @($info.files | ForEach-Object { [ordered]@{ name = $_.name; size = $_.size; sha256 = $_.sha256 } })
        $result.zipEntries = @($info.zipEntries | ForEach-Object { $_.name })
        $mine = Join-Path $root "artifacts\$short\release\release.json"
        if (Test-Path $mine) {
            $local = Get-Content $mine -Encoding UTF8 -Raw | ConvertFrom-Json
            $result.sameAsRepoBuild = [ordered]@{
                exe = (($local.files | Where-Object { $_.name -like '*.exe' }).sha256 -eq ($info.files | Where-Object { $_.name -like '*.exe' }).sha256)
                zip = (($local.files | Where-Object { $_.name -like '*.zip' }).sha256 -eq ($info.files | Where-Object { $_.name -like '*.zip' }).sha256)
            }
        }
        $result.passed = $true
    } finally { Pop-Location }
}
catch { $result.passed = $false; $result.error = $_.Exception.Message; throw }
finally {
    $result.minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    New-Item -ItemType Directory -Force (Join-Path $root '.loop') | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root ".loop\clean-clone-$short.json") -Encoding UTF8
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}
Write-Host "깨끗한 클론 확인 통과: $($result.version), 커밋 $short"
