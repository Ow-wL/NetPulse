<#
  P3: 프로세스와 그 자식 전체의 CPU(전체 코어 기준 %)와 메모리를 1초마다 표본으로 잰다.
  - 매 표본마다 프로세스 트리를 다시 조회해 나중에 생긴 자식도 포함한다.
  - CPU는 표본마다 같은 시각 근처에서 트리 전체의 누적 CPU 시간을 읽고, 이웃 표본 사이 차이를 합산한다.
    처음 보이는 프로세스는 그 표본부터 센다 (그 전에 쓴 CPU는 빠진다).
  - 메모리는 표본마다 작업 집합 합계와 전용 작업 집합 합계를 기록하고 평균·최대를 낸다.
    작업 집합 합계는 프로세스 사이 공유 페이지를 여러 번 센다. 전용 작업 집합은 공유분을 빼지만 앱 전체 비용과 같지 않다.
#>
param([Parameter(Mandatory)] [int] $ProcessId, [int] $Seconds = 10)
$ErrorActionPreference = 'Stop'
function Get-Tree([int] $root) {
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId
    $ids = New-Object System.Collections.Generic.HashSet[int]
    [void]$ids.Add($root)
    do {
        $added = $false
        foreach ($p in $all) { if ($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)) { $added = $true } }
    } while ($added)
    return ,$ids   # 쉼표: 원소가 하나여도 집합 그대로 돌려준다
}
$prevCpu = @{}; $cpuMs = 0.0; $samples = @(); $seen = New-Object System.Collections.Generic.HashSet[int]
$clock = [Diagnostics.Stopwatch]::StartNew(); $lastT = 0.0; $wallMs = 0.0
for ($i = 0; $i -le $Seconds; $i++) {
    $ids = Get-Tree $ProcessId
    $t = $clock.Elapsed.TotalMilliseconds
    $procs = Get-Process -Id ([int[]]@($ids)) -ErrorAction SilentlyContinue
    $ws = 0
    foreach ($p in $procs) {
        [void]$seen.Add($p.Id)
        $now = $p.TotalProcessorTime.TotalMilliseconds
        if ($prevCpu.ContainsKey($p.Id)) { $cpuMs += $now - $prevCpu[$p.Id] }
        $prevCpu[$p.Id] = $now
        $ws += $p.WorkingSet64
    }
    if ($i -gt 0) { $wallMs += $t - $lastT }
    $lastT = $t
    $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Property IDProcess, WorkingSetPrivate |
        Where-Object { $ids.Contains([int]$_.IDProcess) }
    $private = ($perf | Measure-Object WorkingSetPrivate -Sum).Sum
    $samples += [pscustomobject]@{ wsMB = $ws / 1MB; privateMB = $private / 1MB; processes = @($procs).Count }
    if ($i -lt $Seconds) { Start-Sleep -Milliseconds ([Math]::Max(0, ($i + 1) * 1000 - $clock.Elapsed.TotalMilliseconds)) }
}
$pct = 100 * $cpuMs / ($wallMs * [Environment]::ProcessorCount)
$ws = $samples | Measure-Object wsMB -Average -Maximum
$pv = $samples | Measure-Object privateMB -Average -Maximum
[pscustomobject]@{
    seconds = [math]::Round($wallMs / 1000, 1); samples = $samples.Count; processesSeen = $seen.Count
    cpuPercent = [math]::Round($pct, 2)
    memoryMBAvg = [math]::Round($ws.Average, 1); memoryMBMax = [math]::Round($ws.Maximum, 1)
    privateMBAvg = [math]::Round($pv.Average, 1); privateMBMax = [math]::Round($pv.Maximum, 1)
    cores = [Environment]::ProcessorCount
}
