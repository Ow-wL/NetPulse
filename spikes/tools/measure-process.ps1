<#
  P3: 프로세스와 그 자식 전체의 CPU(전체 코어 기준 %)와 메모리를 1초마다 표본으로 잰다.
  - 매 표본마다 프로세스 트리를 다시 조회해 나중에 생긴 자식도 포함한다.
  - CPU: 표본마다 트리 전체의 누적 CPU 시간을 읽어 이웃 표본 사이 차이를 합산한다.
    측정 구간 안에서 생성된 프로세스는 처음 보일 때 누적 CPU 전체를 센다 (생성부터 첫 표본까지 포함).
    측정 시작 전부터 있던 프로세스는 첫 표본을 기준으로 센다.
    **한계**: 두 표본 사이에 끝난 프로세스의 마지막 구간 CPU는 빠진다.
  - 메모리: 표본마다 작업 집합 합계와 전용 작업 집합 합계. 평균·최대를 낸다.
    작업 집합 합계는 프로세스 사이 공유 페이지를 여러 번 센다. 전용 작업 집합은 공유분을 빼지만 앱 전체 비용과 같지 않다.
  - 구간: 첫 표본부터 최소 $Seconds 초를 채운다. 표본 원본(시각, PID별 누적 CPU와 작업 집합, 합계)을 함께 돌려준다.
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
$windowStart = Get-Date
$prevCpu = @{}; $cpuMs = 0.0; $samples = New-Object System.Collections.Generic.List[object]
$clock = [Diagnostics.Stopwatch]::StartNew(); $firstT = $null; $lastT = 0.0
$i = 0
while ($true) {
    $ids = Get-Tree $ProcessId
    $t = $clock.Elapsed.TotalMilliseconds
    $procs = @(Get-Process -Id ([int[]]@($ids)) -ErrorAction SilentlyContinue)
    $ws = 0; $detail = @()
    foreach ($p in $procs) {
        $now = $p.TotalProcessorTime.TotalMilliseconds
        if ($prevCpu.ContainsKey($p.Id)) { $cpuMs += $now - $prevCpu[$p.Id] }
        elseif ($null -ne $firstT) {
            # 측정 도중 처음 보인 프로세스: 측정 시작 뒤에 생겼다면 누적 CPU 전체가 구간 안이다.
            $started = $null; try { $started = $p.StartTime } catch { }
            if ($started -and $started -ge $windowStart) { $cpuMs += $now }
        }
        $prevCpu[$p.Id] = $now
        $ws += $p.WorkingSet64
        $detail += [pscustomobject]@{ pid = $p.Id; cpuMs = [math]::Round($now, 1); wsMB = [math]::Round($p.WorkingSet64 / 1MB, 1) }
    }
    if ($null -eq $firstT) { $firstT = $t }
    $lastT = $t
    $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Property IDProcess, WorkingSetPrivate |
        Where-Object { $ids.Contains([int]$_.IDProcess) }
    $private = ($perf | Measure-Object WorkingSetPrivate -Sum).Sum
    $samples.Add([pscustomobject]@{ tMs = [math]::Round($t - $firstT, 1); wsMB = [math]::Round($ws / 1MB, 1); privateMB = [math]::Round($private / 1MB, 1)
        processes = $procs.Count; detail = $detail })
    if ($t - $firstT -ge $Seconds * 1000) { break }
    $i++
    $next = $firstT + $i * 1000
    Start-Sleep -Milliseconds ([Math]::Max(0, $next - $clock.Elapsed.TotalMilliseconds))
}
$wallMs = $lastT - $firstT
$pct = 100 * $cpuMs / ($wallMs * [Environment]::ProcessorCount)
$wsStat = $samples | Measure-Object wsMB -Average -Maximum
$pvStat = $samples | Measure-Object privateMB -Average -Maximum
[pscustomobject]@{
    seconds = [math]::Round($wallMs / 1000, 2); samples = $samples.Count; processesSeen = $prevCpu.Count
    cpuPercent = [math]::Round($pct, 2)
    memoryMBAvg = [math]::Round($wsStat.Average, 1); memoryMBMax = [math]::Round($wsStat.Maximum, 1)
    privateMBAvg = [math]::Round($pvStat.Average, 1); privateMBMax = [math]::Round($pvStat.Maximum, 1)
    cores = [Environment]::ProcessorCount
    note = '두 표본 사이에 끝난 프로세스의 마지막 구간 CPU는 빠짐'
    sampleData = $samples
}
