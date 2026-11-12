<#
  프로세스와 그 자식 프로세스 전체의 CPU 평균(전체 코어 기준 %)과 메모리 합계(작업 집합, MB)를 잰다 (P3).
#>
param([Parameter(Mandatory)] [int] $ProcessId, [int] $Seconds = 10)
$ErrorActionPreference = 'Stop'
function Get-Tree([int] $root) {
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
    $ids = New-Object System.Collections.Generic.HashSet[int]
    [void]$ids.Add($root)
    do {
        $added = $false
        foreach ($p in $all) { if ($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)) { $added = $true } }
    } while ($added)
    return $ids
}
$ids = Get-Tree $ProcessId
$start = @{}; foreach ($id in $ids) { try { $start[$id] = (Get-Process -Id $id).TotalProcessorTime } catch { } }
$clock = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds
$cpu = [TimeSpan]::Zero; $mem = 0; $private = 0
foreach ($id in $start.Keys) {
    try { $p = Get-Process -Id $id; $cpu += $p.TotalProcessorTime - $start[$id]; $mem += $p.WorkingSet64 } catch { }
    # 보조 지표: 프로세스 사이에 공유되는 페이지를 빼고 센 전용 작업 집합 (합격선 판정에는 쓰지 않음)
    try { $private += (Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$id").WorkingSetPrivate } catch { }
}
$pct = 100 * $cpu.TotalMilliseconds / ($clock.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount)
[pscustomobject]@{ processes = $start.Count; cpuPercent = [math]::Round($pct, 2); memoryMB = [math]::Round($mem / 1MB, 1)
    privateMB = [math]::Round($private / 1MB, 1); cores = [Environment]::ProcessorCount }
