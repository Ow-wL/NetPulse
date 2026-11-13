<#
.SYNOPSIS
  G3: 게이트 빌드가 만든 바로 그 배포 폴더로 UI 테스트를 돌린다 (M1 설계 5절).

  돌리기 전에 모두 확인한다. 하나라도 어긋나면 실패.
  - manifest의 evidence가 true
  - 현재 HEAD가 manifest의 sourceCommit과 같고 작업트리가 깨끗함 (UI 테스트 코드도 같은 커밋)
  - 배포 폴더의 파일 목록과 각 SHA-256이 manifest와 정확히 같음

  결과는 artifacts/<SHA>/ui-test.json. 시작할 때 이전 결과를 지운다 (재사용하지 않는다).
#>
param([Parameter(Mandatory)] [string] $Artifacts)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. (Join-Path $PSScriptRoot 'common.ps1')

$Artifacts = (Resolve-Path $Artifacts).Path
$manifestPath = Join-Path $Artifacts 'manifest.json'
$resultPath = Join-Path $Artifacts 'ui-test.json'
$uiDir = Join-Path $Artifacts 'ui'
if (Test-Path $resultPath) { Remove-Item $resultPath }
if (Test-Path $uiDir) { Remove-Item -Recurse -Force $uiDir }

# ---- 확인 ----
$manifest = Get-Content $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
if (-not $manifest.evidence) { throw 'manifest의 evidence가 false입니다. build/build.ps1 -Gate로 만든 산출물만 씁니다.' }
$head = (git rev-parse HEAD).Trim()
if ($head -ne $manifest.sourceCommit) { throw "HEAD($head)가 산출물의 커밋($($manifest.sourceCommit))과 다릅니다." }
if (@(git status --porcelain --untracked-files=no).Count -gt 0 -or @(git ls-files --others --exclude-standard).Count -gt 0) { throw '작업트리가 깨끗하지 않습니다.' }
$app = Join-Path $Artifacts 'app'
$actual = @(Get-FileManifest $app)
$expected = @($manifest.files)
if ($actual.Count -ne $expected.Count) { throw "배포 파일 수가 다릅니다: manifest $($expected.Count), 실제 $($actual.Count)" }
for ($i = 0; $i -lt $expected.Count; $i++) {
    if ($actual[$i].path -ne $expected[$i].path -or $actual[$i].sha256 -ne $expected[$i].sha256) { throw "배포 파일이 manifest와 다릅니다: $($expected[$i].path)" }
}
Write-Host "산출물 확인: 파일 $($expected.Count)개, 커밋 $($head.Substring(0, 10))"

# ---- UI 테스트 ----
New-Item -ItemType Directory -Force $uiDir | Out-Null
$env:NETPULSE_EXE = Join-Path $app 'NetPulse.exe'
$env:NETPULSE_UITEST_OUT = Join-Path $uiDir 'captures'
$commands = @()
$commands += Invoke-Step 'ui-build' 300 'dotnet' @('build', 'tests/NetPulse.UiTests', '-c', 'Release') $uiDir
$trxDir = Join-Path $uiDir 'trx'
$failed = $null
try {
    $commands += Invoke-Step 'ui-test' 600 'dotnet' @('test', '--project', 'tests/NetPulse.UiTests', '-c', 'Release', '--no-build', '--report-xunit-trx', '--results-directory', $trxDir) $uiDir
} catch { $failed = $_.Exception.Message }
$trx = Get-ChildItem $trxDir -Filter *.trx -ErrorAction SilentlyContinue | Select-Object -First 1
$summary = if ($trx) { $neutral = Join-Path $uiDir 'ui-tests.trx'; Move-Item $trx.FullName $neutral; Read-TrxSummary $neutral } else { $null }

# 데모 시나리오는 실행 파일 안에 묶인다. 묶인 원본(이 커밋의 파일)의 해시를 적는다.
$scenarios = @(Get-ChildItem (Join-Path $root 'demo\scenarios') -Filter *.json | Sort-Object Name | ForEach-Object { [ordered]@{ path = "demo/scenarios/$($_.Name)"; sha256 = Get-Sha256Text $_.FullName } })
$result = [ordered]@{
    sourceCommit   = $head
    manifestSha256 = Get-Sha256Text $manifestPath
    scenarios      = $scenarios
    executable     = 'app/NetPulse.exe'
    commands       = $commands
    tests          = $summary
    captures       = @(Get-ChildItem $env:NETPULSE_UITEST_OUT -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object { "ui/captures/$($_.Name)" })
    passed         = [bool]($null -eq $failed -and $summary -and $summary.failed -eq 0 -and $summary.total -gt 0)
    error          = $failed
    at             = (Get-Date).ToString('o')
}
$result | ConvertTo-Json -Depth 6 | Set-Content $resultPath -Encoding UTF8
if (-not $result.passed) { throw "UI 테스트 실패: $failed" }
Write-Host "UI 테스트 통과: $($summary.passed)/$($summary.total)"
