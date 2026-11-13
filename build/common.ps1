<#
  build/*.ps1 공통 함수. 함수 이름은 PowerShell 기본 별칭(measure, select 등)과 겹치지 않게 Verb-Noun으로 짓는다 (M0 교훈).
#>

function Invoke-Step([string] $Name, [int] $TimeoutSeconds, [string] $File, [string[]] $Arguments, [string] $LogDir) {
    <# 명령을 제한시간 안에 실행한다. 넘기면 프로세스 트리를 끝내고 실패. 기록용 명령 문자열을 돌려준다. #>
    $log = Join-Path $LogDir "$Name.log"
    $quoted = $Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    Write-Host "[$Name] $File $($quoted -join ' ')"
    $p = Start-Process -FilePath $File -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle   # 종료 코드를 읽을 수 있게 핸들을 잡아 둔다
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        & taskkill /PID $p.Id /T /F | Out-Null
        throw "[$Name] 제한시간 $TimeoutSeconds 초를 넘겼습니다."
    }
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) {
        Get-Content $log, "$log.err" -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
        throw "[$Name] 실패 (종료 코드 $($p.ExitCode))"
    }
    # 기록에는 저장소 밖 경로를 남기지 않는다 (산출물 폴더는 <out>, 저장소는 상대 경로)
    $record = "$File $($quoted -join ' ')".Replace($LogDir, '<out>').Replace((Get-Location).Path + '', '')
    return $record
}

function Get-FileManifest([string] $Folder) {
    <# 폴더 안 모든 파일의 상대 경로와 SHA-256 (경로 순 정렬) #>
    $base = (Resolve-Path $Folder).Path.TrimEnd('\') + '\'
    Get-ChildItem $Folder -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($base.Length).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    }
}

function Read-TrxSummary([string] $Path) {
    [xml] $trx = Get-Content $Path -Encoding UTF8 -Raw
    $c = $trx.TestRun.ResultSummary.Counters
    [ordered]@{ total = [int]$c.total; passed = [int]$c.passed; failed = [int]$c.failed; notExecuted = [int]$c.notExecuted; outcome = $trx.TestRun.ResultSummary.outcome }
}

function Get-Sha256Text([string] $Path) { (Get-FileHash $Path -Algorithm SHA256).Hash }

function Test-AllPassed($Summary) {
    <# 하나라도 실패·미실행이면 거짓. 테스트가 0개여도 거짓 (G4 1라운드 지적 8) #>
    return [bool]($Summary -and [int]$Summary.total -gt 0 -and [int]$Summary.passed -eq [int]$Summary.total -and
        [int]$Summary.failed -eq 0 -and [int]$Summary.notExecuted -eq 0)
}

# G4 답변의 판정 줄: 강조(**)만 허용하고 줄 전체가 판정이어야 한다 (G4 1라운드 지적 2).
# 첫 판정 줄은 '판정:'으로 시작하는 첫 줄이다. 그 줄이 정확히 APPROVE여야 승인이다.
$script:VerdictLinePattern = '^\s*\**\s*판정\s*:'
$script:ApprovePattern = '^\s*\**\s*판정\s*:\s*\**\s*APPROVE\s*\**\s*$'

function Get-FirstVerdictLine([string] $AnswerPath) {
    Get-Content $AnswerPath -Encoding UTF8 | Where-Object { $_ -match $script:VerdictLinePattern } | Select-Object -First 1
}

function Write-BenchSummary($Bench, [string] $Path) {
    <# 공개용 벤치 요약 (식별 정보 없음, M2 설계 5.9). 원본 표본은 비공개 산출물에 둔다. #>
    $lines = @('| 구간 | 표본 | 95백분위 (ms) | 최대 (ms) | 33 ms 초과 |', '| --- | --- | --- | --- | --- |')
    foreach ($s in $Bench.segments) { $lines += "| $($s.Segment) | $($s.Samples) | $($s.P95) | $($s.Max) | $($s.Over33) |" }
    $m = $Bench.memory
    $lines += ''
    $lines += "- 기준 간격 T: $([math]::Round($Bench.baselineMs, 2)) ms (CompositionTarget.Rendering 콜백 간격, 실제 표시 프레임과 다를 수 있음)"
    $lines += "- 원형 전환 $($Bench.transitions)회, 대체(페이드) $($Bench.fallbacks)회, 클립 없이 새 색이 그려진 콜백 $($Bench.unclippedNewThemeFrames)회, 마지막 화면 찍기 $([math]::Round($Bench.lastSnapshotMs, 1)) ms"
    $lines += "- 전용 메모리: 전환 2회 뒤 $($m.after2Mb) MB, 20회 뒤 $($m.after20Mb) MB, 60회 뒤 $($m.after60Mb) MB (20→60회 증가 $($m.plateauGrowthMb) MB)"
    $l = $Bench.leftovers
    $lines += "- 남은 것: 찍은 화면 $($l.snapshotLayers), 반복 모션 $($l.repeatsAtStart)→$($l.repeatsAtEnd), 모드 구독자 $($l.subscribersAtStart)→$($l.subscribersAtEnd)"
    $lines | Set-Content $Path -Encoding UTF8
}
