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
# 배포하는 exe 그대로 시험한다 (M10 설계 B2 [1-5]): app/는 정확히 NetPulse.exe 하나
if ($actual.Count -ne 1 -or $actual[0].path -ne 'NetPulse.exe') { throw "배포 폴더는 NetPulse.exe 하나여야 합니다: $(($actual | ForEach-Object { $_.path }) -join ', ')" }
Write-Host "산출물 확인: 파일 $($expected.Count)개, 커밋 $($head.Substring(0, 10))"

# ---- UI 테스트 ----
New-Item -ItemType Directory -Force $uiDir | Out-Null
$env:NETPULSE_EXE = Join-Path $app 'NetPulse.exe'
$env:NETPULSE_UITEST_OUT = Join-Path $uiDir 'captures'
# 단일 파일 추출 위치를 새 빈 폴더로 (M10 설계 B2): 이전 추출 캐시가 묶음 누락을 가리지 않게. 시험이 끝나면 지운다
$extract = Join-Path ([IO.Path]::GetTempPath()) ('netpulse-extract-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $extract | Out-Null
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extract
$commands = @()
$commands += Invoke-Step 'ui-build' 300 'dotnet' @('build', 'tests/NetPulse.UiTests', '-c', 'Release') $uiDir
$trxDir = Join-Path $uiDir 'trx'
$failed = $null
try {
    # M5: 캡처 매트릭스(약 8분)와 측정 화면 bench(약 7분)가 더해져 10분으로는 모자란다.
    # 실측 시나리오(M5·M6 LiveRunTests)는 이 PC 네트워크를 쓰고 결과를 공개하지 않으므로 게이트에서 빼고 따로 돌린다.
    # 클래스 이름 대신 특성으로 뺀다 (M6 설계 7.5): 새 실측 시험이 클래스 목록에서 빠지는 일이 없게
    # README 캡처(Kind=Docs)도 게이트 밖이다 (M10 설계 C [1-10]): build/readme-shots.ps1로 따로 찍는다
    $commands += Invoke-Step 'ui-test' 5400 'dotnet' @('test', '--project', 'tests/NetPulse.UiTests', '-c', 'Release', '--no-build', '--report-xunit-trx', '--results-directory', $trxDir, '--filter-not-trait', 'Kind=Live', '--filter-not-trait', 'Kind=Docs') $uiDir
} catch { $failed = $_.Exception.Message }
finally {
    Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
}
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
    # M6 (설계 7.4·7.5): 진단 장면 × 테마 × 배율, 흐름, bench 진단 구간, 고대비·효과·움직임 줄이기, 전환, 결론 카드 대비
    $m6Ids = @(Get-Content (Join-Path $root 'tests\NetPulse.UiTests\m6-scenes.txt') -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { ($_ -split '\|')[0] })
    foreach ($id in $m6Ids) { foreach ($t in 'dark', 'light') { foreach ($s in '100', '150') { $required += "matrix-m6-$id-$t-$s" } } }
    $required += @('flow-diagnose', 'bench-diagnose', 'contrast-diagnose-dark', 'contrast-diagnose-light')
    $required += @('hc-aquatic', 'hc-desert') | ForEach-Object { "matrix-m6-diagnose-dns-$_"; "matrix-m6-diagnose-outside-$_" }
    $required += @('matrix-m6-diagnose-dns-dark-reduced', 'matrix-m6-diagnose-dns-light-reduced', 'matrix-m6-diagnose-running-dark-reducedmotion', 'matrix-m6-diagnose-running-light-reducedmotion')
    foreach ($p in '0', '0.5', '1') { $required += "matrix-m6-transition-crossfade-$p" }
    # M7 (설계 7.4·7.5): 기록 장면 × 테마 × 배율, 흐름, bench, 고대비·효과·움직임 줄이기, 목록 → 상세 전환, 결론 카드 대비
    $m7Ids = @(Get-Content (Join-Path $root 'tests\NetPulse.UiTests\m7-scenes.txt') -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { ($_ -split '\|')[0] })
    foreach ($id in $m7Ids) { foreach ($t in 'dark', 'light') { foreach ($s in '100', '150') { $required += "matrix-m7-$id-$t-$s" } } }
    $required += @('flow-records', 'bench-records', 'contrast-records-dark', 'contrast-records-light')
    $required += @('list', 'detail') | ForEach-Object { $v = $_; @('hc-aquatic', 'hc-desert') | ForEach-Object { "matrix-m7-records-$v-$_" } }
    $required += @('matrix-m7-records-detail-dark-reduced', 'matrix-m7-records-detail-light-reduced', 'matrix-m7-records-detail-dark-reducedmotion', 'matrix-m7-records-detail-light-reducedmotion')
    foreach ($p in '0', '0.5', '1') { $required += "matrix-m7-transition-crossfade-$p" }
    # M8 (설계 7.4·7.5): 리포트 장면 × 테마 × 배율, 흐름, bench(문서 만들기·미리 보기), Edge 화면·A4(html-render), 고대비·효과·움직임 줄이기, 홈 → 리포트 전환, 종이 면 대비
    $m8Ids = @(Get-Content (Join-Path $root 'tests\NetPulse.UiTests\m8-scenes.txt') -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { ($_ -split '\|')[0] })
    foreach ($id in $m8Ids) { foreach ($t in 'dark', 'light') { foreach ($s in '100', '150') { $required += "matrix-m8-$id-$t-$s" } } }
    $required += @('flow-report', 'flow-report-mask', 'bench-report', 'bench-report-build', 'html-render', 'contrast-report-dark', 'contrast-report-light')
    $required += @('matrix-m8-report-preview-hc-aquatic', 'matrix-m8-report-preview-hc-desert', 'matrix-m8-report-figure-hc-aquatic')
    $required += @('matrix-m8-report-preview-dark-reduced', 'matrix-m8-report-preview-light-reduced', 'matrix-m8-report-preview-dark-reducedmotion', 'matrix-m8-report-preview-light-reducedmotion')
    foreach ($p in '0', '0.5', '1') { $required += "matrix-m8-transition-crossfade-$p" }
    # M9 (설계 7.2·7.4): 설정·처음 숨김 안내·프로필 TCP 장면 × 테마 × 배율, 상주·설정 흐름, 고대비·효과·움직임 줄이기, 트레이 메뉴 창, 아이콘 대비
    $m9Ids = @(Get-Content (Join-Path $root 'tests\NetPulse.UiTests\m9-scenes.txt') -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { ($_ -split '\|')[0] })
    foreach ($id in $m9Ids) { foreach ($t in 'dark', 'light') { foreach ($s in '100', '150') { $required += "matrix-m9-$id-$t-$s" } } }
    $required += @('flow-resident-hide', 'flow-resident-balloons', 'flow-settings-diagnosis', 'm9-tray-menu', 'm9-icon', 'flow-resident-sleepgap', 'flow-resident-roundtrip', 'flow-resident-roundtrip-noipv4')
    $required += @('matrix-m9-settings-normal-hc-aquatic', 'matrix-m9-settings-normal-hc-desert', 'matrix-m9-settings-moving-hc-aquatic', 'matrix-m9-hint-dialog-hc-desert')
    $required += @('matrix-m9-settings-normal-dark-reduced', 'matrix-m9-settings-moving-light-reduced', 'matrix-m9-hint-dialog-dark-reducedmotion', 'matrix-m9-settings-moving-light-reducedmotion')
    # M10 (설계 6절 [1-11]): 사용 조건·정보·라이선스와 고지 장면 × 테마 × 배율, 고대비·효과·움직임 줄이기, 고지 bench
    $m10Ids = @(Get-Content (Join-Path $root 'tests\NetPulse.UiTests\m10-scenes.txt') -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { ($_ -split '\|')[0] })
    foreach ($id in $m10Ids) { foreach ($t in 'dark', 'light') { foreach ($s in '100', '150') { $required += "matrix-m10-$id-$t-$s" } } }
    $required += @('bench-legal')
    $required += @('matrix-m10-terms-hc-aquatic', 'matrix-m10-terms-hc-desert', 'matrix-m10-about-legal-expanded-hc-aquatic', 'matrix-m10-about-legal-expanded-hc-desert')
    $required += @('matrix-m10-terms-dark-reduced', 'matrix-m10-about-legal-expanded-light-reduced', 'matrix-m10-terms-light-reducedmotion', 'matrix-m10-about-legal-expanded-dark-reducedmotion')
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $g5Dir "$_.json")) })
    $matrix = @(Get-ChildItem $g5Dir -Filter 'matrix-*.json' | Sort-Object Name)
    # M2 회귀 매트릭스는 따로 센다: M5 결과가 합쳐져 M2 캡처 누락을 가리지 않게 (G4 M5 1라운드 C3)
    # M6 결과도 M2 수에 섞이지 않게 뺀다
    # M10 결과도 M2 수에 섞이지 않게 뺀다 ([2-8])
    $m2Matrix = @($matrix | Where-Object { -not $_.Name.StartsWith('matrix-m5-') -and -not $_.Name.StartsWith('matrix-m6-') -and -not $_.Name.StartsWith('matrix-m7-') -and -not $_.Name.StartsWith('matrix-m8-') -and -not $_.Name.StartsWith('matrix-m9-') -and -not $_.Name.StartsWith('matrix-m10-') })
    $captures = @(Get-ChildItem $env:NETPULSE_UITEST_OUT -Filter *.png | Sort-Object Name | ForEach-Object { [ordered]@{ file = "ui/captures/$($_.Name)"; sha256 = Get-Sha256Text $_.FullName } })
    $bench = & $read 'bench'
    $g5 = [ordered]@{
        sourceCommit   = $head
        manifestSha256 = Get-Sha256Text $manifestPath
        uiTestsPassed  = $result.passed
        missing        = $missing
        matrixCount    = $matrix.Count
        m2MatrixCount  = $m2Matrix.Count
        passed         = [bool]($result.passed -and $missing.Count -eq 0 -and $m2Matrix.Count -ge $MatrixExpected)
        bench          = $bench
        benchMeasure   = & $read 'bench-measure'
        m5Scenes       = $sceneIds.Count
        m6Scenes       = $m6Ids.Count
        m7Scenes       = $m7Ids.Count
        m8Scenes       = $m8Ids.Count
        m10Scenes      = $m10Ids.Count
        benchLegal     = & $read 'bench-legal' 
        flowReport     = & $read 'flow-report'
        benchReport    = & $read 'bench-report'
        benchReportBuild = & $read 'bench-report-build'
        htmlRender     = & $read 'html-render'
        benchRecords   = & $read 'bench-records'
        flowRecords    = & $read 'flow-records'
        benchDiagnose  = & $read 'bench-diagnose'
        flowDiagnose   = & $read 'flow-diagnose'
        contrastDiagnose = [ordered]@{ dark = & $read 'contrast-diagnose-dark'; light = & $read 'contrast-diagnose-light' }
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
    if (-not $g5.passed) { Write-Warning "G5 미완료: 누락 $($missing -join ', '), M2 매트릭스 $($m2Matrix.Count)/$MatrixExpected" }
}
if (-not $result.passed) { throw "UI 테스트 실패: $failed" }
if ((Test-Path $g5Path) -and -not (Get-Content $g5Path -Encoding UTF8 -Raw | ConvertFrom-Json).passed) { throw 'G5 묶음이 완전하지 않습니다.' }
Write-Host "UI 테스트 통과: $($summary.passed)/$($summary.total)"
