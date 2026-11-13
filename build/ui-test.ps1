<#
.SYNOPSIS
  G3: 게이트 빌드가 만든 바로 그 배포 폴더로 UI 테스트를 돌린다 (M1 설계 5절).

  돌리기 전에 모두 확인한다. 하나라도 어긋나면 실패.
  - manifest의 evidence가 true
  - 현재 HEAD가 manifest의 sourceCommit과 같고 작업트리가 깨끗함 (UI 테스트 코드도 같은 커밋)
  - 배포 폴더의 파일 목록과 각 SHA-256이 manifest와 정확히 같음

  결과는 artifacts/<SHA>/ui-test.json. 시작할 때 이전 결과를 지운다 (재사용하지 않는다).
#>
param([Parameter(Mandatory)] [string] $Artifacts, [int] $MatrixExpected = 44)
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
    passed         = [bool]($null -eq $failed -and (Test-AllPassed $summary))
    error          = $failed
    at             = (Get-Date).ToString('o')
}
$result | ConvertTo-Json -Depth 6 | Set-Content $resultPath -Encoding UTF8

# ---- G5 묶음 (M2 설계 7.5): 캡처 해시, 실측 대비, 전환 충실도, 효과 줄이기 표면, 벤치 ----
$g5Dir = Join-Path $env:NETPULSE_UITEST_OUT 'g5'
$g5Path = Join-Path $Artifacts 'g5.json'
if (Test-Path $g5Path) { Remove-Item $g5Path }
if (Test-Path $g5Dir) {
    $read = { param($n) $f = Join-Path $g5Dir "$n.json"; if (Test-Path $f) { Get-Content $f -Encoding UTF8 -Raw | ConvertFrom-Json } else { $null } }
    $required = @('bench', 'contrast-dark', 'contrast-light', 'reduced-surface', 'fidelity-dark-overview', 'fidelity-light-overview', 'fidelity-dark-toasts', 'fidelity-light-toasts', 'switch-dark-to-light', 'switch-light-to-dark', 'boundary-dark-to-light', 'boundary-light-to-dark')
    # M5 (설계 7.4): 개수가 아니라 필수 장면 ID × 테마 × 배율의 결과가 모두 있는지 본다. 목록은 시험과 같은 파일
    $required += @('flow-firstrun-to-stop', 'flow-first-measurement-actions', 'flow-theme-close-cleanup', 'bench-measure', 'reducedmotion-home', 'contrast-home-dark', 'contrast-home-light')
    $sceneIds = @(Get-Content (Join-Path $root 'tests\NetPulse.UiTests\m5-scenes.txt') -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { ($_ -split '\|')[0] })
    foreach ($id in $sceneIds) { foreach ($t in 'dark', 'light') { foreach ($s in '100', '150') { $required += "matrix-m5-$id-$t-$s" } } }
    $required += @('hc-aquatic', 'hc-desert') | ForEach-Object { "matrix-m5-home-outage-$_"; "matrix-m5-measure-outage-$_" }
    $required += @('matrix-m5-home-outage-dark-reduced', 'matrix-m5-home-outage-light-reduced', 'matrix-m5-measure-live-dark-reducedmotion', 'matrix-m5-measure-live-light-reducedmotion')
    foreach ($k in 'crossfade', 'sharedmove') { foreach ($p in '0', '0.5', '1') { $required += "matrix-m5-transition-$k-$p" } }
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $g5Dir "$_.json")) })
    $matrix = @(Get-ChildItem $g5Dir -Filter 'matrix-*.json' | Sort-Object Name)
    $captures = @(Get-ChildItem $env:NETPULSE_UITEST_OUT -Filter *.png | Sort-Object Name | ForEach-Object { [ordered]@{ file = "ui/captures/$($_.Name)"; sha256 = Get-Sha256Text $_.FullName } })
    $bench = & $read 'bench'
    $g5 = [ordered]@{
        sourceCommit   = $head
        manifestSha256 = Get-Sha256Text $manifestPath
        uiTestsPassed  = $result.passed
        missing        = $missing
        matrixCount    = $matrix.Count
        passed         = [bool]($result.passed -and $missing.Count -eq 0 -and $matrix.Count -ge $MatrixExpected)
        bench          = $bench
        benchMeasure   = & $read 'bench-measure'
        m5Scenes       = $sceneIds.Count
        flow           = & $read 'flow-firstrun-to-stop'
        contrast       = [ordered]@{ dark = & $read 'contrast-dark'; light = & $read 'contrast-light'; homeDark = & $read 'contrast-home-dark'; homeLight = & $read 'contrast-home-light' }
        reducedSurface = & $read 'reduced-surface'
        fidelity       = @('dark-overview', 'light-overview', 'dark-toasts', 'light-toasts' | ForEach-Object { [ordered]@{ case = $_; result = & $read "fidelity-$_" } })
        runtimeSwitch  = [ordered]@{ darkToLight = & $read 'switch-dark-to-light'; lightToDark = & $read 'switch-light-to-dark' }
        boundary       = [ordered]@{ darkToLight = & $read 'boundary-dark-to-light'; lightToDark = & $read 'boundary-light-to-dark' }
        captures       = $captures
    }
    $g5 | ConvertTo-Json -Depth 8 | Set-Content $g5Path -Encoding UTF8
    if ($bench) { Write-BenchSummary $bench.bench (Join-Path $uiDir 'bench.md') }
    if (-not $g5.passed) { Write-Warning "G5 미완료: 누락 $($missing -join ', '), 매트릭스 $($matrix.Count)/$MatrixExpected" }
}
if (-not $result.passed) { throw "UI 테스트 실패: $failed" }
if ((Test-Path $g5Path) -and -not (Get-Content $g5Path -Encoding UTF8 -Raw | ConvertFrom-Json).passed) { throw 'G5 묶음이 완전하지 않습니다.' }
Write-Host "UI 테스트 통과: $($summary.passed)/$($summary.total)"
