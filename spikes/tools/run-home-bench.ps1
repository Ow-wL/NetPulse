<#
  흑요석 홈 재현(C --home)의 성능·자원·측정 영향·전환 중 자동화 격리를 잰다.
  - 벤치 실행: 정지 장면 10초 → 오로라 켜고 끄기 6회 → 원형 전환 20회 → 전환 뒤 정지 장면 10초. 측정 루프(1초 루프백 Ping)를 함께 돌린다.
  - 자원: 전환 전 정지 장면과 전환 뒤 정지 장면에서 각각 10초 표본 (누적 여부 확인).
  - 자동화 격리: 비교 막대로 전환을 일으키고 전환 도중 UI Automation에서 같은 AutomationId가 몇 개 보이는지 센다.
#>
param([Parameter(Mandatory)] [string] $Exe, [Parameter(Mandatory)] [string] $OutDir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process NetPulseSpike* -ErrorAction SilentlyContinue | Stop-Process -Force
$logFile = Join-Path $env:LOCALAPPDATA 'NetPulseSpike\C-home\results.jsonl'
$here = $PSScriptRoot
$summary = [ordered]@{ gitHead = (git -C $here rev-parse --short HEAD); worktreeClean = [string]::IsNullOrWhiteSpace((git -C $here status --porcelain -- ':!docs/loop' | Out-String)) }

# ---- 벤치 ----
if (Test-Path $logFile) { Remove-Item $logFile }
$p = Start-Process $Exe -ArgumentList '--home', '--bench', '--aurora', 'on', '--chrome', '0', '--run-seconds', '56' -PassThru
Start-Sleep -Seconds 5
$summary.resourcesBefore = & "$here\measure-process.ps1" -ProcessId $p.Id -Seconds 10 | Select-Object * -ExcludeProperty sampleData
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 300; $marks = Get-Content $logFile -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.kind -eq 'mark' -and $_.label -eq 'after-switches' } }
while (-not $marks -and (Get-Date) -lt $deadline)
Start-Sleep -Milliseconds 500
$summary.resourcesAfter = & "$here\measure-process.ps1" -ProcessId $p.Id -Seconds 10 | Select-Object * -ExcludeProperty sampleData
if (-not $p.WaitForExit(30000)) { Stop-Process -Id $p.Id -Force; throw '제한시간 초과' }
if ($p.ExitCode -ne 0) { throw "비정상 종료 (코드 $($p.ExitCode))" }
Copy-Item $logFile (Join-Path $OutDir 'home-bench.jsonl') -Force
$log = Get-Content $logFile -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }
$summary.windowsSettings = ($log | Where-Object kind -eq 'windowsSettings' | Select-Object values, highContrast, reduceMotion, reduceTransparency)
$summary.frames = @($log | Where-Object kind -eq 'frames' | ForEach-Object { [ordered]@{ label = $_.label; avgFps = $_.avgFps; p95Ms = $_.p95Ms; maxMs = $_.maxMs; count = $_.count } })
$summary.scenes = @($log | Where-Object kind -eq 'mark' | Where-Object { $_.label -ne 'bench-done' } | ForEach-Object { [ordered]@{ label = $_.label; scenes = $_.scenes; plots = $_.plots; storyboardScenes = $_.storyboardScenes; managedMB = $_.managedMB } })
$summary.switches = @($log | Where-Object kind -eq 'switch' | ForEach-Object { $_.ms })
$summary.switchStartMs = @($log | Where-Object kind -eq 'switchStart' | ForEach-Object { $_.msToAnimation })
$rounds = @($log | Where-Object kind -eq 'round')
$idx = @($rounds | ForEach-Object { [long]$_.index }); $unique = @($idx | Sort-Object -Unique)
$last = ($idx | Measure-Object -Maximum).Maximum
$summary.rounds = [ordered]@{ count = $rounds.Count; firstIndex = ($idx | Measure-Object -Minimum).Minimum; lastIndex = $last
    duplicates = $rounds.Count - $unique.Count; missing = ($last + 1) - $unique.Count
    maxLateMs = [math]::Round(($rounds | ForEach-Object { [double]$_.actualMs - [double]$_.scheduledMs } | Measure-Object -Maximum).Maximum, 1) }
$summary.end = $log | Where-Object kind -eq 'end' | Select-Object rounds, missed, maxLateMs, scenes, plots

# ---- 전환 중 자동화 격리 ----
$p = Start-Process $Exe -ArgumentList '--home', '--run-seconds', '20' -PassThru
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$deadline = (Get-Date).AddSeconds(15)
do { $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond); Start-Sleep -Milliseconds 200 } while (-not $win -and (Get-Date) -lt $deadline)
Start-Sleep -Seconds 3
function Count-Id([string] $Id, [bool] $ControlView) {
    $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $c = if ($ControlView) { New-Object System.Windows.Automation.AndCondition($idCond, [System.Windows.Automation.Automation]::ControlViewCondition) } else { $idCond }
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c).Count
}
$summary.uiaBefore = [ordered]@{ verdictControl = (Count-Id 'verdict' $true); verdictAll = (Count-Id 'verdict' $false); buttonControl = (Count-Id 'theme-light' $true) }
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'theme-light')))
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 150
$summary.uiaDuring = [ordered]@{ verdictControl = (Count-Id 'verdict' $true); verdictAll = (Count-Id 'verdict' $false); buttonControl = (Count-Id 'theme-light' $true) }
Start-Sleep -Milliseconds 1200
$summary.uiaAfter = [ordered]@{ verdictControl = (Count-Id 'verdict' $true); verdictAll = (Count-Id 'verdict' $false); buttonControl = (Count-Id 'theme-light' $true) }
$focused = [System.Windows.Automation.AutomationElement]::FocusedElement
$summary.focusAfter = if ($focused) { "$($focused.Current.AutomationId) / pid $($focused.Current.ProcessId)" } else { 'none' }
Stop-Process -Id $p.Id -Force

$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'home-bench-summary.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 6
