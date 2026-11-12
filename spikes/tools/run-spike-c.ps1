<#
  스파이크 C 측정 전체를 한 번에 돈다 (P2~P9). 빌드 → 그 산출물 실행 → UI Automation 조작 → 값 읽기 → 캡처.
#>
param([Parameter(Mandatory)] [string] $OutDir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$here = $PSScriptRoot
$project = Join-Path $here '..\c-winui\SpikeC.csproj'
$publish = Join-Path $OutDir 'publish-winui'
$logSource = Join-Path $env:LOCALAPPDATA 'NetPulseSpike\C\results.jsonl'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$summary = [ordered]@{ runtime = 'winui' }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:EnableMsixTooling=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $publish *> (Join-Path $OutDir 'build-winui.log')
if ($LASTEXITCODE -ne 0) { throw '빌드 실패 (winui)' }
$summary.buildSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item
$summary.publishMB = [math]::Round((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$summary.publishFiles = (Get-ChildItem $publish -Recurse -File).Count
$exe = Join-Path $publish 'NetPulseSpikeC.exe'

function Start-Spike([string[]] $SpikeArgs) { Start-Process -FilePath $exe -ArgumentList $SpikeArgs -PassThru }
function Wait-Exit($p, [int] $Seconds) { if (-not $p.WaitForExit($Seconds * 1000)) { Stop-Process -Id $p.Id -Force; throw '제한시간 초과' } }
function Read-Log { Get-Content $logSource -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } }
function Get-Window([int] $ProcessId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw '창을 찾지 못했습니다.'
}
function Find([object] $Window, [string] $Id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $e = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "요소를 찾지 못했습니다: $Id"
}
function Invoke-Id($Window, [string] $Id) {
    (Find $Window $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700
}
function Read-Id($Window, [string] $Id) { (Find $Window $Id).Current.Name }

foreach ($i in 1, 2) {
    $p = Start-Spike @('--run-seconds', '6'); Wait-Exit $p 40
    $ready = Read-Log | Where-Object kind -eq 'ready' | Select-Object -First 1
    $summary["startupMs_run$i"] = $ready.msSinceProcessStart
}

$p = Start-Spike @('--bench', '--run-seconds', '48')
$w = Get-Window $p.Id
Start-Sleep -Seconds 5
$res = & (Join-Path $here 'measure-process.ps1') -ProcessId $p.Id -Seconds 10
$summary.cpuPercent = $res.cpuPercent; $summary.memoryMB = $res.memoryMB; $summary.privateMB = $res.privateMB; $summary.processes = $res.processes
Start-Sleep -Seconds 16
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir 'c-winui-dark-window.png') | Out-Null
$summary.roundTextBefore = Read-Id $w 'round'
Invoke-Id $w 'theme-toggle'
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir 'c-winui-light-window.png') | Out-Null
Invoke-Id $w 'nav-diagnose'
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir 'c-winui-light-diagnose.png') | Out-Null
Invoke-Id $w 'nav-monitor'
$memo = Find $w 'memo'
$memo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('화상회의 중 끊김')
$summary.koreanInput = $memo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Wait-Exit $p 60
Copy-Item $logSource (Join-Path $OutDir 'bench-winui.jsonl')
$log = Read-Log
$summary.frames = @($log | Where-Object kind -eq 'frames' | ForEach-Object { [ordered]@{ label = $_.label; avgFps = $_.avgFps; p95Ms = $_.p95Ms; count = $_.count } })
$end = $log | Where-Object kind -eq 'end'
$summary.benchRounds = $end.rounds; $summary.benchMissed = $end.missed; $summary.benchMaxLateMs = $end.maxLateMs
$summary.probes = ($log | Where-Object kind -eq 'probes').values
$summary.windowsSettings = ($log | Where-Object kind -eq 'windowsSettings').values

$p = Start-Spike @('--hide-test', '--run-seconds', '50')
Start-Sleep -Seconds 43
$shown = Read-Log | Where-Object kind -eq 'show'
$summary.hideRoundsWhileHidden = $shown.roundsWhileHidden; $summary.hideMissed = $shown.missedWhileHidden
$summary.hideLatestIndexAtShow = $shown.latestIndex
$w = Get-Window $p.Id
$summary.hideRoundTextAfterShow = Read-Id $w 'round'
Wait-Exit $p 30

$p = Start-Spike @('--reduce-transparency', '--reduce-motion', '--run-seconds', '12')
Start-Sleep -Seconds 8
& (Join-Path $here 'capture-window.ps1') -ProcessId $p.Id -Out (Join-Path $OutDir 'c-winui-reduced.png') | Out-Null
Wait-Exit $p 30

$ro = Join-Path $OutDir 'readonly-winui'
if (Test-Path $ro) { icacls $ro /reset /T | Out-Null; Remove-Item -Recurse -Force $ro }
Copy-Item $publish $ro -Recurse
icacls $ro /deny "${env:USERNAME}:(OI)(CI)(WD,AD)" | Out-Null
try {
    try { Set-Content (Join-Path $ro 'write-test.txt') 'x' -ErrorAction Stop; $summary.readOnlyFolderWritable = $true } catch { $summary.readOnlyFolderWritable = $false }
    $p = Start-Process -FilePath (Join-Path $ro 'NetPulseSpikeC.exe') -ArgumentList @('--run-seconds', '8') -PassThru
    Wait-Exit $p 40
    $summary.readOnlyStart = [bool](Read-Log | Where-Object kind -eq 'ready')
} finally {
    icacls $ro /reset /T | Out-Null
    Remove-Item -Recurse -Force $ro
}

$summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'summary-winui.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 5
