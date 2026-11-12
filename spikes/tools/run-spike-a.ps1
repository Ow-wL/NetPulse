<#
  스파이크 A 측정 전체를 한 번에 돈다 (P2~P9). 빌드 → 그 산출물 실행 → 조작 → 값 읽기 → 캡처.
  출력: -OutDir 아래에 로그(JSONL), 캡처(PNG), summary.json
#>
param([Parameter(Mandatory)] [string] $OutDir, [ValidateSet('net10', 'net48')] [string] $Runtime = 'net10')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$here = $PSScriptRoot
$project = Join-Path $here '..\a-webview\SpikeA.csproj'
$publish = Join-Path $OutDir "publish-$Runtime"
$logSource = Join-Path $env:LOCALAPPDATA "NetPulseSpike\A-$Runtime\results.jsonl"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$summary = [ordered]@{ runtime = $Runtime }

# ---- G1: 배포 빌드 ----
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
if ($Runtime -eq 'net10') {
    $buildArgs = @('publish', $project, '-c', 'Release', '-f', 'net10.0-windows', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-o', $publish)
} else {
    $buildArgs = @('publish', $project, '-c', 'Release', '-f', 'net48', '-o', $publish)
}
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet @buildArgs *> (Join-Path $OutDir "build-$Runtime.log")
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 ($Runtime)" }
$summary.buildSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item
$summary.publishMB = [math]::Round((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$summary.publishFiles = (Get-ChildItem $publish -Recurse -File).Count
$exe = Join-Path $publish 'NetPulseSpikeA.exe'

function Start-Spike([string[]] $SpikeArgs) {
    $p = Start-Process -FilePath $exe -ArgumentList $SpikeArgs -PassThru
    return $p
}
function Wait-Exit($p, [int] $Seconds) { if (-not $p.WaitForExit($Seconds * 1000)) { Stop-Process -Id $p.Id -Force; throw "제한시간 초과" } }
function Read-Log { Get-Content $logSource -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } }
function Drive([string] $Action, [string] $Arg) { node (Join-Path $here 'a-drive.mjs') 9333 $Action $Arg }

# ---- 첫 실행(캐시 생성) 후 두 번째 실행의 시작 시간 (P7) ----
foreach ($i in 1, 2) {
    $p = Start-Spike @('--run-seconds', '6'); Wait-Exit $p 30
    $ready = Read-Log | Where-Object kind -eq 'ready' | Select-Object -First 1
    $summary["startupMs_run$i"] = $ready.msSinceProcessStart
}

# ---- 벤치 실행: 프레임, 자원, 측정 영향, 캡처, 조작 (P1~P4, P6, P8) ----
$p = Start-Spike @('--cdp-port', '9333', '--bench', '--run-seconds', '48')
Drive 'wait-ready' | Out-Null
Start-Sleep -Seconds 5
$res = & (Join-Path $here 'measure-process.ps1') -ProcessId $p.Id -Seconds 10
$summary.cpuPercent = $res.cpuPercent; $summary.memoryMB = $res.memoryMB; $summary.privateMB = $res.privateMB; $summary.processes = $res.processes
Start-Sleep -Seconds 14
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir "a-$Runtime-dark-window.png") | Out-Null
Drive 'screenshot' (Join-Path $OutDir "a-$Runtime-dark-dom.png") | Out-Null
$summary.roundTextBefore = Drive 'read' '#round'
Drive 'click' '#theme-toggle' | Out-Null
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir "a-$Runtime-light-window.png") | Out-Null
Drive 'click' '#nav-diagnose' | Out-Null
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir "a-$Runtime-light-diagnose.png") | Out-Null
Drive 'click' '#nav-monitor' | Out-Null
$summary.koreanInput = Drive 'type' '#memo::화상회의 중 끊김'
Wait-Exit $p 60
Copy-Item $logSource (Join-Path $OutDir "bench-$Runtime.jsonl")
$log = Read-Log
$summary.frames = @($log | Where-Object kind -eq 'frames' | ForEach-Object { [ordered]@{ label = $_.label; avgFps = $_.avgFps; p95Ms = $_.p95Ms; count = $_.count } })
$end = $log | Where-Object kind -eq 'end'
$summary.benchRounds = $end.rounds; $summary.benchMissed = $end.missed; $summary.benchMaxLateMs = $end.maxLateMs
$summary.probes = ($log | Where-Object kind -eq 'probes').values
$summary.windowsSettings = ($log | Where-Object kind -eq 'windowsSettings').values
$summary.dwm = $log | Where-Object kind -eq 'dwm' | Select-Object darkMode, backdrop, extend
$summary.webview2 = ($log | Where-Object kind -eq 'webview2').browserVersion

# ---- 숨김 시험 (P5) ----
$p = Start-Spike @('--cdp-port', '9333', '--hide-test', '--run-seconds', '50')
Drive 'wait-ready' | Out-Null
Start-Sleep -Seconds 42
$shown = Read-Log | Where-Object kind -eq 'show'
$summary.hideRoundsWhileHidden = $shown.roundsWhileHidden; $summary.hideMissed = $shown.missedWhileHidden
$summary.hideLatestIndexAtShow = $shown.latestIndex
$summary.hideRoundTextAfterShow = Drive 'read' '#round'
Wait-Exit $p 30

# ---- 대체 동작 (P9) ----
$p = Start-Spike @('--reduce-transparency', '--reduce-motion', '--run-seconds', '12')
Start-Sleep -Seconds 8
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir "a-$Runtime-reduced.png") | Out-Null
Wait-Exit $p 30

# ---- 쓰기 금지 폴더에서 시작 (P7) ----
$ro = Join-Path $OutDir "readonly-$Runtime"
if (Test-Path $ro) { icacls $ro /reset /T | Out-Null; Remove-Item -Recurse -Force $ro }
Copy-Item $publish $ro -Recurse
# 파일 만들기와 쓰기만 막는다. 실행과 읽기는 허용한다.
icacls $ro /deny "${env:USERNAME}:(OI)(CI)(WD,AD)" | Out-Null
try {
    try { Set-Content (Join-Path $ro 'write-test.txt') 'x' -ErrorAction Stop; $summary.readOnlyFolderWritable = $true } catch { $summary.readOnlyFolderWritable = $false }
    $p = Start-Process -FilePath (Join-Path $ro 'NetPulseSpikeA.exe') -ArgumentList @('--run-seconds', '8') -PassThru
    Wait-Exit $p 30
    $summary.readOnlyStart = [bool](Read-Log | Where-Object kind -eq 'ready')
} finally {
    icacls $ro /reset /T | Out-Null
    Remove-Item -Recurse -Force $ro
}

$summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir "summary-$Runtime.json") -Encoding UTF8
$summary | ConvertTo-Json -Depth 5
