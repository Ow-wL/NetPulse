<#
  스파이크 측정 전체를 한 번에 돈다 (P1~P9). 빌드 → 그 산출물 실행 → 조작 → 값 읽기 → 캡처.
  -Candidate: a-net10 | a-net48 | c
  출력: -OutDir 아래에 실행별 로그(JSONL), 캡처(PNG), summary-<후보>.json
#>
param([Parameter(Mandatory)] [ValidateSet('a-net10', 'a-net48', 'c')] [string] $Candidate, [Parameter(Mandatory)] [string] $OutDir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$here = $PSScriptRoot
$isA = $Candidate -like 'a-*'
$publish = Join-Path $OutDir "publish-$Candidate"
$logName = @{ 'a-net10' = 'A-net10'; 'a-net48' = 'A-net48'; 'c' = 'C' }[$Candidate]
$logDir = Join-Path $env:LOCALAPPDATA "NetPulseSpike\$logName"
$logFile = Join-Path $logDir 'results.jsonl'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$summary = [ordered]@{ candidate = $Candidate; measuredAt = (Get-Date).ToString('o'); gitHead = (git -C $here rev-parse --short HEAD)
    worktreeClean = [string]::IsNullOrWhiteSpace((git -C $here status --porcelain -- ':!docs/loop' | Out-String)) }
if (-not $summary.worktreeClean) { Write-Warning '작업트리에 커밋되지 않은 변경이 있습니다. 이 측정은 커밋과 연결되지 않습니다.' }

# 이전 실행이 남긴 스파이크 프로세스가 있으면 측정이 오염되므로 먼저 끈다.
Get-Process NetPulseSpike* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# ---- G1: 배포 빌드 ----
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
switch ($Candidate) {
    'a-net10' { $build = @('publish', "$here\..\a-webview\SpikeA.csproj", '-c', 'Release', '-f', 'net10.0-windows', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-o', $publish) }
    'a-net48' { $build = @('publish', "$here\..\a-webview\SpikeA.csproj", '-c', 'Release', '-f', 'net48', '-o', $publish) }
    'c'       { $build = @('publish', "$here\..\c-winui\SpikeC.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:EnableMsixTooling=true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-o', $publish) }
}
$summary.buildCommand = 'dotnet ' + ($build -join ' ').Replace($publish, '<out>').Replace("$here\..", 'spikes')
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet @build *> (Join-Path $OutDir "build-$Candidate.log")
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 ($Candidate)" }
$summary.buildSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
Get-ChildItem $publish -Recurse -Include *.pdb, *.xml | Remove-Item
$summary.publishMB = [math]::Round((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$summary.publishFiles = (Get-ChildItem $publish -Recurse -File).Count
$exe = (Get-ChildItem $publish -Filter 'NetPulseSpike*.exe' | Select-Object -First 1).FullName
$summary.exeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash
$summary.sdk = (dotnet --version)

# ---- 실행 도우미 ----
function Start-Spike([string[]] $SpikeArgs) {
    if (Test-Path $logFile) { Remove-Item $logFile }     # 이전 실행의 로그를 읽지 않도록
    Start-Process -FilePath $exe -ArgumentList $SpikeArgs -PassThru
}
function Wait-Exit($p, [int] $Seconds) {
    if (-not $p.WaitForExit($Seconds * 1000)) { Stop-Process -Id $p.Id -Force; throw "제한시간 초과" }
    if ($p.ExitCode -ne 0) { throw "비정상 종료 (코드 $($p.ExitCode))" }
}
function Read-Log { Get-Content $logFile -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } }
function Wait-LogKind([string] $Kind, [int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $logFile) { $hit = Read-Log | Where-Object kind -eq $Kind | Select-Object -First 1; if ($hit) { return $hit } }
        Start-Sleep -Milliseconds 200
    }
    throw "로그에 $Kind 가 나오지 않았습니다."
}
$script:uiaWindow = $null
function Ui-Attach($p) {
    if ($isA) { node "$here\a-drive.mjs" 9333 'wait-ready' | Out-Null; return }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $deadline = (Get-Date).AddSeconds(20)
    do { $script:uiaWindow = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond); Start-Sleep -Milliseconds 200 }
    while (-not $script:uiaWindow -and (Get-Date) -lt $deadline)
    if (-not $script:uiaWindow) { throw 'UI Automation 창을 찾지 못했습니다.' }
}
function Ui-Find([string] $Id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $e = $script:uiaWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $e) { throw "요소 없음: $Id" }
    return $e
}
function Ui-Click([string] $Id) {
    if ($isA) { node "$here\a-drive.mjs" 9333 'click' "#$Id" | Out-Null; return }
    (Ui-Find $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700
}
function Ui-Read([string] $Id) {
    if ($isA) { return (node "$here\a-drive.mjs" 9333 'read' "#$Id") }
    return (Ui-Find $Id).Current.Name
}
function Ui-Type([string] $Id, [string] $Text) {
    if ($isA) { return (node "$here\a-drive.mjs" 9333 'type' "#${Id}::$Text") }
    $v = (Ui-Find $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $v.SetValue($Text); return $v.Current.Value
}
function Ui-WaitRound([int] $Min, [int] $TimeoutMs) {
    if ($isA) { return (node "$here\a-drive.mjs" 9333 'wait-round' "${Min}:$TimeoutMs" | ConvertFrom-Json) }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $value = -1
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) { $value = [int](Ui-Read 'round'); if ($value -ge $Min) { break }; Start-Sleep -Milliseconds 100 }
    return [pscustomobject]@{ value = $value; elapsedMs = $sw.ElapsedMilliseconds }
}
$cdp = if ($isA) { @('--cdp-port', '9333') } else { @() }
function Capture([int] $ProcessId, [string] $Name) { & "$here\capture-window.ps1" -ProcessId $ProcessId -Out (Join-Path $OutDir "$Candidate-$Name.png") | Out-Null }

# ---- P7: 첫 실행(사용자 데이터 없음)과 두 번째 실행의 시작 시간 ----
if (Test-Path $logDir) { Remove-Item -Recurse -Force $logDir }
foreach ($i in 1, 2) {
    $p = Start-Spike @('--run-seconds', '6'); Wait-Exit $p 40
    $summary["startupMs_run$i"] = (Read-Log | Where-Object kind -eq 'ready' | Select-Object -First 1).msSinceProcessStart
}

# ---- 벤치: P2 프레임, P3 자원, P4 측정 영향(75초), P6, P8 ----
$p = Start-Spike ($cdp + @('--bench', '--run-seconds', '75'))
Ui-Attach $p
Start-Sleep -Seconds 5
$res = & "$here\measure-process.ps1" -ProcessId $p.Id -Seconds 10
$summary.resources = $res
Start-Sleep -Seconds 16
Capture $p.Id 'dark'
$summary.roundTextBefore = Ui-Read 'round'
Ui-Click 'theme-toggle'
Capture $p.Id 'light'
Ui-Click 'nav-diagnose'
Capture $p.Id 'light-diagnose'
Ui-Click 'nav-monitor'
Ui-Click 'theme-toggle'
$summary.koreanInput = Ui-Type 'memo' '화상회의 중 끊김'
Wait-Exit $p 90
Copy-Item $logFile (Join-Path $OutDir "bench-$Candidate.jsonl")
$log = Read-Log
$summary.frames = @($log | Where-Object kind -eq 'frames' | ForEach-Object { [ordered]@{ label = $_.label; avgFps = $_.avgFps; p95Ms = $_.p95Ms; count = $_.count } })
$rounds = @($log | Where-Object kind -eq 'round')
$idx = $rounds | ForEach-Object { [long]$_.index }
$late = $rounds | ForEach-Object { [double]$_.actualMs - [double]$_.scheduledMs }
$summary.p4 = [ordered]@{
    rounds = $rounds.Count; firstIndex = ($idx | Measure-Object -Minimum).Minimum; lastIndex = ($idx | Measure-Object -Maximum).Maximum
    duplicates = $rounds.Count - @($idx | Sort-Object -Unique).Count
    missing = (($idx | Measure-Object -Maximum).Maximum - ($idx | Measure-Object -Minimum).Minimum + 1) - @($idx | Sort-Object -Unique).Count
    maxLateMs = [math]::Round(($late | Measure-Object -Maximum).Maximum, 1)
    coveredSeconds = ($idx | Measure-Object -Maximum).Maximum - ($idx | Measure-Object -Minimum).Minimum + 1
}
$summary.probes = ($log | Where-Object kind -eq 'probes').values
$summary.windowsSettings = ($log | Where-Object kind -eq 'windowsSettings').values
if ($isA) { $summary.webview2 = ($log | Where-Object kind -eq 'webview2').browserVersion }

# ---- P5: 30초 숨김 후 5초 안에 최신 회차 표시 ----
$p = Start-Spike ($cdp + @('--hide-test', '--run-seconds', '55'))
Ui-Attach $p
$show = Wait-LogKind 'show' 50
$wait = Ui-WaitRound ([int]$show.latestIndex) 5000
$summary.p5 = [ordered]@{ roundsWhileHidden = $show.roundsWhileHidden; missedWhileHidden = $show.missedWhileHidden
    latestAtShow = $show.latestIndex; shownValue = $wait.value; elapsedMs = $wait.elapsedMs; passed = ($show.missedWhileHidden -eq 0 -and $wait.value -ge $show.latestIndex) }
Wait-Exit $p 30

# ---- P1: 흑백 줄무늬 위 틴트만 / 블러 / 불투명 ----
$p = Start-Spike ($cdp + @('--glass-test', '--run-seconds', '14'))
Start-Sleep -Seconds 9
Capture $p.Id 'glass-test'
Wait-Exit $p 30

# ---- P9: 대체 동작 (강제 옵션), 이동 없음을 시간 간격 두 캡처로 확인 ----
$p = Start-Spike ($cdp + @('--reduce-transparency', '--reduce-motion', '--run-seconds', '16'))
Start-Sleep -Seconds 8
Capture $p.Id 'reduced-1'
Start-Sleep -Milliseconds 400
Capture $p.Id 'reduced-2'
Wait-Exit $p 30

# ---- P7: 쓰기 금지 폴더에서 시작 ----
$ro = Join-Path $OutDir "readonly-$Candidate"
if (Test-Path $ro) { icacls $ro /reset /T | Out-Null; Remove-Item -Recurse -Force $ro }
Copy-Item $publish $ro -Recurse
icacls $ro /deny "${env:USERNAME}:(OI)(CI)(WD,AD)" | Out-Null
try {
    try { Set-Content (Join-Path $ro 'write-test.txt') 'x' -ErrorAction Stop; $summary.readOnlyFolderWritable = $true } catch { $summary.readOnlyFolderWritable = $false }
    if (Test-Path $logFile) { Remove-Item $logFile }
    $p = Start-Process -FilePath (Join-Path $ro (Split-Path $exe -Leaf)) -ArgumentList @('--run-seconds', '8') -PassThru
    Wait-Exit $p 40
    $summary.readOnlyStart = [bool](Read-Log | Where-Object kind -eq 'ready')
} finally {
    icacls $ro /reset /T | Out-Null
    Remove-Item -Recurse -Force $ro
}

$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir "summary-$Candidate.json") -Encoding UTF8
"done $Candidate"
