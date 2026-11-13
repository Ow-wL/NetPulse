<#
.SYNOPSIS
  Codex(GPT) 검토 호출 (기획서 4.4, M1 설계 9절).

.DESCRIPTION
  -Request 파일을 .loop/reviews/<Id>-request.md로 복사하고, Codex를 읽기 전용으로 불러
  답변을 .loop/reviews/<Id>-answer.md에 저장한다.

  - 요청문 끝에 "못 읽었으면 추측하지 말고 못 읽었다고 말해줘"가 없으면 붙인다.
  - G4 요청은 머리에 "검토: G4", "대상: <전체 SHA>", "라운드: n" 줄이 있어야 한다 (-Gate 지정 시 검사).
  - 호출 전후 HEAD와 git status를 비교한다. 바뀌었으면 실패 (Codex는 읽기 전용이어야 한다).
  - 제한시간 10분. 실패·시간 초과·빈 답이면 한 번만 다시 시도하고, 그래도 안 되면 종료 코드 2 ("교차 검토 미완료").
#>
param(
    [Parameter(Mandatory)] [string] $Id,
    [Parameter(Mandatory)] [string] $Request,
    [string[]] $Image = @(),
    [switch] $Gate,
    [int] $TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

if ($Id -notmatch '^[a-z0-9][a-z0-9-]*$') { throw "식별자는 소문자·숫자·하이픈만 씁니다: $Id" }
$reviews = Join-Path $root '.loop\reviews'
New-Item -ItemType Directory -Force $reviews | Out-Null
$requestPath = Join-Path $reviews "$Id-request.md"
$answerPath = Join-Path $reviews "$Id-answer.md"
if ((Test-Path $requestPath) -or (Test-Path $answerPath)) { throw "이미 있는 식별자입니다: $Id (라운드마다 새 식별자를 씁니다)" }

$text = Get-Content $Request -Encoding UTF8 -Raw
$tail = '못 읽었으면 추측하지 말고 못 읽었다고 말해줘'
if (-not $text.TrimEnd().EndsWith($tail)) { $text = $text.TrimEnd() + "`n`n$tail`n" }
if ($Gate) {
    $head = ($text -split "`n" | Select-Object -First 8) -join "`n"
    if ($head -notmatch '(?m)^검토:\s*G4\s*$') { throw '요청문 머리에 "검토: G4"가 없습니다.' }
    if ($head -notmatch '(?m)^대상:\s*([0-9a-f]{40})\s*$') { throw '요청문 머리에 "대상: <전체 SHA>"가 없습니다.' }
    $target = $Matches[1]
    git cat-file -e "$target^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw "대상 커밋이 없습니다: $target" }
    if ($head -notmatch '(?m)^라운드:\s*[1-3]\s*$') { throw '요청문 머리에 "라운드: 1~3"이 없습니다.' }
}
# 요청문 자체도 공개 기록이 될 수 있으므로 검사한다.
& (Join-Path $PSScriptRoot 'privacy-scan.ps1') -Text $text
if ($LASTEXITCODE -ne 0) { throw '요청문에 이 PC의 식별 정보가 있습니다.' }
[IO.File]::WriteAllText($requestPath, $text, (New-Object Text.UTF8Encoding($false)))

function Get-RepoFingerprint { (git rev-parse HEAD) + "`n" + ((git status --porcelain --untracked-files=all) -join "`n") }

$codexArgs = @('exec', '--skip-git-repo-check', '--sandbox', 'read-only', '--output-last-message', "`"$answerPath`"")
foreach ($img in $Image) { $codexArgs += @('-i', "`"$((Resolve-Path $img).Path)`"") }
$codexArgs += '-'
$logDir = Join-Path $root '.loop\logs'
New-Item -ItemType Directory -Force $logDir | Out-Null

$before = Get-RepoFingerprint
$ok = $false
for ($attempt = 1; $attempt -le 2 -and -not $ok; $attempt++) {
    if (Test-Path $answerPath) { Remove-Item $answerPath }
    $log = Join-Path $logDir "codex-$Id-$attempt.log"
    Write-Host "Codex 호출 $attempt/2 ($Id)"
    $p = Start-Process -FilePath 'codex' -ArgumentList $codexArgs -NoNewWindow -PassThru `
        -RedirectStandardInput $requestPath -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        & taskkill /PID $p.Id /T /F | Out-Null
        $p.WaitForExit(30000) | Out-Null   # 끝났는지 확인한 뒤에 다시 시도한다
        Write-Warning "시간 초과 ($TimeoutSeconds 초)"
        continue
    }
    $p.WaitForExit()
    $answer = if (Test-Path $answerPath) { (Get-Content $answerPath -Encoding UTF8 -Raw) } else { '' }
    if ($p.ExitCode -eq 0 -and $answer -and $answer.Trim().Length -gt 0) { $ok = $true }
    else { Write-Warning "실패 또는 빈 답 (종료 코드 $($p.ExitCode))" }
}
$after = Get-RepoFingerprint
if ($before -ne $after) { throw 'Codex 호출 중에 저장소가 바뀌었습니다. 읽기 전용 위반 여부를 확인하세요.' }
if (-not $ok) {
    if (Test-Path $answerPath) { Remove-Item $answerPath }
    Write-Error '교차 검토 미완료: 두 번 모두 답을 받지 못했습니다.' -ErrorAction Continue
    exit 2
}
$verdict = (Get-Content $answerPath -Encoding UTF8 | Where-Object { $_ -match '판정:' } | Select-Object -First 1)
Write-Host "답변: .loop/reviews/$Id-answer.md"
if ($verdict) { Write-Host "첫 판정 줄: $($verdict.Trim())" } else { Write-Host '판정 줄 없음' }
