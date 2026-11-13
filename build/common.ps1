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
