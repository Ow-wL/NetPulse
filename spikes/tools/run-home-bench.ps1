<#
  흑요석 홈 재현(C --home)의 성능·자원·측정 영향·전환 중 자동화 격리를 잰다.
  - 벤치 실행: 정지 장면 10초 → 오로라 켜고 끄기 6회 → 원형 전환 20회 → 전환 뒤 정지 장면 10초. 측정 루프(1초 루프백 Ping)를 함께 돌린다.
  - 자원: 전환 전 정지 장면, 전환 도중(두 장면이 겹치는 구간 포함), 전환 뒤 정지 장면에서 각각 10초 표본. 표본 원본을 보존한다.
  - 자동화 격리: 비교 막대로 전환을 일으키고 전환 도중 UI Automation에서 같은 AutomationId가 몇 개 보이는지 센다.
  - 빠른 연속 조작: 전환·오로라 버튼을 짧은 간격으로 여러 번 눌러 상태가 하나로 남는지 본다.
  - 오로라와 효과 줄이기: 오로라를 켠 채 효과 줄이기를 걸면 오로라 층이 그려지지 않는지 앱 기록으로 확인한다.
#>
param([Parameter(Mandatory)] [string] $Exe, [Parameter(Mandatory)] [string] $OutDir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process NetPulseSpike* -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); [void]$_.WaitForExit(5000) }
$logFile = Join-Path $env:LOCALAPPDATA 'NetPulseSpike\C-home\results.jsonl'
$here = $PSScriptRoot
$summary = [ordered]@{ gitHead = (git -C $here rev-parse --short HEAD); worktreeClean = [string]::IsNullOrWhiteSpace((git -C $here status --porcelain -- ':!docs/loop' | Out-String)) }
function Read-Log { Get-Content $logFile -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } }
function Wait-Mark([string] $Label, [int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 200; $hit = Read-Log | Where-Object { $_.kind -eq 'mark' -and $_.label -eq $Label } } while (-not $hit -and (Get-Date) -lt $deadline)
    if (-not $hit) { throw "표시 $Label 가 나오지 않았습니다." }
}
function Get-Resources([int] $ProcessId, [string] $Name) {
    $r = & "$here\measure-process.ps1" -ProcessId $ProcessId -Seconds 10
    $r.sampleData | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir "resources-$Name.json") -Encoding UTF8
    return $r | Select-Object * -ExcludeProperty sampleData
}

# ---- 벤치 ----
if (Test-Path $logFile) { Remove-Item $logFile }
$p = Start-Process $Exe -ArgumentList '--home', '--bench', '--aurora', 'on', '--chrome', '0', '--run-seconds', '58' -PassThru
Start-Sleep -Seconds 4
$summary.resourcesBefore = Get-Resources $p.Id 'before'
Wait-Mark 'before-switches' 20
$summary.resourcesDuring = Get-Resources $p.Id 'during-switches'
Wait-Mark 'after-switches' 25
Start-Sleep -Milliseconds 500
$summary.resourcesAfter = Get-Resources $p.Id 'after'
if (-not $p.WaitForExit(30000)) { Stop-Process -Id $p.Id -Force; throw '제한시간 초과' }
if ($p.ExitCode -ne 0) { throw "비정상 종료 (코드 $($p.ExitCode))" }
Copy-Item $logFile (Join-Path $OutDir 'home-bench.jsonl') -Force
$log = Read-Log
$summary.windowsSettings = ($log | Where-Object kind -eq 'windowsSettings' | Select-Object values, highContrast, reduceMotion, reduceTransparency)
$summary.frames = @($log | Where-Object kind -eq 'frames' | ForEach-Object { [ordered]@{ label = $_.label; avgFps = $_.avgFps; p95Ms = $_.p95Ms; maxMs = $_.maxMs; count = $_.count } })
$summary.scenes = @($log | Where-Object kind -eq 'mark' | Where-Object { $_.label -ne 'bench-done' } | ForEach-Object { [ordered]@{ label = $_.label; scenes = $_.scenes; plots = $_.plots; storyboardScenes = $_.storyboardScenes; managedMB = $_.managedMB } })
$sw = @($log | Where-Object kind -eq 'switch' | ForEach-Object { [double]$_.ms })
$ss = @($log | Where-Object kind -eq 'switchStart' | ForEach-Object { [double]$_.msToAnimation })
$summary.switches = [ordered]@{ count = $sw.Count; totalMsMin = ($sw | Measure-Object -Minimum).Minimum; totalMsMax = ($sw | Measure-Object -Maximum).Maximum
    startMsMin = ($ss | Measure-Object -Minimum).Minimum; startMsMax = ($ss | Measure-Object -Maximum).Maximum }
$rounds = @($log | Where-Object kind -eq 'round')
$idx = @($rounds | ForEach-Object { [long]$_.index }); $unique = @($idx | Sort-Object -Unique)
$last = ($idx | Measure-Object -Maximum).Maximum
$summary.rounds = [ordered]@{ count = $rounds.Count; firstIndex = ($idx | Measure-Object -Minimum).Minimum; lastIndex = $last
    duplicates = $rounds.Count - $unique.Count; missing = ($last + 1) - $unique.Count
    maxLateMs = [math]::Round(($rounds | ForEach-Object { [double]$_.actualMs - [double]$_.scheduledMs } | Measure-Object -Maximum).Maximum, 1) }
$summary.end = $log | Where-Object kind -eq 'end' | Select-Object rounds, missed, maxLateMs, scenes, plots

# ---- 전환 중 자동화 격리, 빠른 연속 조작 ----
if (Test-Path $logFile) { Remove-Item $logFile }
$p = Start-Process $Exe -ArgumentList '--home', '--run-seconds', '25' -PassThru
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$deadline = (Get-Date).AddSeconds(15)
do { $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond); Start-Sleep -Milliseconds 200 } while (-not $win -and (Get-Date) -lt $deadline)
Start-Sleep -Seconds 3
function Find-Id([string] $Id) { $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))) }
function Count-Id([string] $Id, [bool] $ControlView) {
    $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $c = if ($ControlView) { New-Object System.Windows.Automation.AndCondition($idCond, [System.Windows.Automation.Automation]::ControlViewCondition) } else { $idCond }
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c).Count
}
function Snapshot { [ordered]@{ verdictControl = (Count-Id 'verdict' $true); verdictAll = (Count-Id 'verdict' $false); buttonControl = (Count-Id 'theme-light' $true) } }
function Press([string] $Id) { (Find-Id $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
$summary.uiaBefore = Snapshot
Press 'theme-light'
Start-Sleep -Milliseconds 150
$summary.uiaDuring = Snapshot
Start-Sleep -Milliseconds 1200
$summary.uiaAfter = Snapshot
$focused = [System.Windows.Automation.AutomationElement]::FocusedElement
$summary.focusAfter = if ($focused) { "$($focused.Current.AutomationId) / pid $($focused.Current.ProcessId)" } else { 'none' }

# 빠른 연속 조작: 전환 진행 중에 다른 전환을 누르고, 오로라를 짧은 간격으로 세 번 누른다
Press 'theme-dark'; Start-Sleep -Milliseconds 60; Press 'theme-light'; Start-Sleep -Milliseconds 60; Press 'problem-toggle'
foreach ($i in 1..3) { Press 'aurora-toggle'; Start-Sleep -Milliseconds 80 }
Start-Sleep -Milliseconds 1500
$quick = Read-Log
$summary.quickToggle = [ordered]@{ alive = -not $p.HasExited; uia = (Snapshot)
    switchesDone = @($quick | Where-Object kind -eq 'switch').Count; switchesIgnored = @($quick | Where-Object kind -eq 'switchIgnored').Count
    auroraEvents = @($quick | Where-Object kind -eq 'aurora' | ForEach-Object { $_.on }) -join ',' }
$p.Kill(); [void]$p.WaitForExit(5000)   # 끝날 때까지 기다려야 로그 파일을 놓는다
Copy-Item $logFile (Join-Path $OutDir 'home-quick.jsonl') -Force

# ---- 오로라 + 효과 줄이기 ----
foreach ($case in @(@{ n = 'aurora'; a = @('--aurora', 'on') }, @{ n = 'aurora_reduce'; a = @('--aurora', 'on', '--reduce-transparency') })) {
    if (Test-Path $logFile) { Remove-Item $logFile }
    $p = Start-Process $Exe -ArgumentList (@('--home', '--chrome', '0', '--run-seconds', '4') + $case.a) -PassThru
    if (-not $p.WaitForExit(20000)) { $p.Kill(); [void]$p.WaitForExit(5000) }
    $summary["veil_$($case.n)"] = Read-Log | Where-Object kind -eq 'veil' | Select-Object requested, visible, opacity, lightsHidden, acrylic
}

$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'home-bench-summary.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 6
