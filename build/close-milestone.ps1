<#
.SYNOPSIS
  마일스톤 마감 (M1 설계 9절). 아래를 순서대로 하고, 하나라도 실패·미실행·시간 초과면 멈춘다.
    1. 게이트 확인 (G1~G3 산출물, G4 승인 원문)
    2. 승인 코드 X가 HEAD의 조상인지
    3. X 뒤 커밋 하나하나가 docs/loop/만 바꾸는지
    4. 공개 전 검사 (원격에 없는 커밋 하나하나, PR 본문)
    5. 푸시 (강제 푸시 없음), PR 조회·재사용·생성
    6. PR head SHA 확인, 그 SHA의 CI가 성공할 때까지 대기 (상한 15분)
  결과는 .loop/close-<X 앞 10자리>.json. 성공했을 때만 종료 코드 0.

.PARAMETER Approved  G4가 승인한 코드 커밋 X (전체 SHA)
.PARAMETER Base      PR의 base 브랜치 (쌓인 PR이면 앞 마일스톤 브랜치)
.PARAMETER Title     PR 제목
.PARAMETER BodyFile  PR 본문 파일 (UTF-8)
.PARAMETER UiOptional 화면이 없는 마일스톤: ui-test.json 대신 manifest의 테스트 결과만 본다
#>
param(
    [Parameter(Mandatory)] [string] $Approved,
    [Parameter(Mandatory)] [string] $Base,
    [Parameter(Mandatory)] [string] $Title,
    [Parameter(Mandatory)] [string] $BodyFile,
    [switch] $UiOptional,
    [switch] $Screens,
    [int] $CiTimeoutMinutes = 15
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. (Join-Path $PSScriptRoot 'common.ps1')

function Invoke-Git { $out = & git @args; if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') 실패 ($LASTEXITCODE)" }; return $out }
function Invoke-Gh { $out = & gh @args; if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') 실패 ($LASTEXITCODE)" }; return $out }
function Get-GhObject { return ConvertFrom-Json -InputObject ((Invoke-Gh @args) -join "`n") }
function Get-GhList {
    <# PowerShell 5.1의 ConvertFrom-Json은 JSON 배열을 객체 하나로 내보낸다. 원소를 하나씩 꺼내 배열로 돌려준다 (G4 1라운드 지적 6). #>
    $parsed = ConvertFrom-Json -InputObject ((Invoke-Gh @args) -join "`n")
    $items = New-Object System.Collections.Generic.List[object]
    foreach ($item in $parsed) { $items.Add($item) }
    return ,$items.ToArray()
}
function Get-NormalizedText([string] $Text) { return ($Text -replace "`r`n", "`n").Trim() }
$report = [ordered]@{ approved = $Approved; base = $Base; steps = [ordered]@{} }
function Complete-Step([string] $name, $detail) { $report.steps[$name] = $detail; Write-Host "[완료] $name" }

$X = (Invoke-Git rev-parse --verify "$Approved^{commit}").Trim()
if ($X -ne $Approved) { throw "-Approved는 전체 SHA로 줍니다 ($X)" }
$short = $X.Substring(0, 10)
$artifacts = Join-Path $root "artifacts\$short"
$resultPath = Join-Path $root ".loop\close-$short.json"
try {
    # ---- 1. 게이트 확인 ----
    $manifestPath = Join-Path $artifacts 'manifest.json'
    if (-not (Test-Path $manifestPath)) { throw "G1 산출물이 없습니다: artifacts/$short" }
    $manifest = Get-Content $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
    if (-not $manifest.evidence -or $manifest.sourceCommit -ne $X) { throw 'manifest가 X의 게이트 증거가 아닙니다.' }
    $actual = @(Get-FileManifest (Join-Path $artifacts 'app'))
    $expected = @($manifest.files)
    if ($actual.Count -ne $expected.Count) { throw '배포 파일 수가 manifest와 다릅니다.' }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        if ($actual[$i].path -ne $expected[$i].path -or $actual[$i].sha256 -ne $expected[$i].sha256) { throw "배포 파일이 manifest와 다릅니다: $($expected[$i].path)" }
    }
    foreach ($t in $manifest.tests.PSObject.Properties) { if (-not (Test-AllPassed $t.Value)) { throw "G2 실패 (실패·미실행 포함): $($t.Name)" } }
    $manifestHash = Get-Sha256Text $manifestPath
    $gate = [ordered]@{ manifestSha256 = $manifestHash }
    if (-not $UiOptional) {
        $uiPath = Join-Path $artifacts 'ui-test.json'
        if (-not (Test-Path $uiPath)) { throw 'G3 결과(ui-test.json)가 없습니다.' }
        $ui = Get-Content $uiPath -Encoding UTF8 -Raw | ConvertFrom-Json
        if (-not $ui.passed -or -not (Test-AllPassed $ui.tests) -or $ui.sourceCommit -ne $X -or $ui.manifestSha256 -ne $manifestHash) { throw 'G3 결과가 X의 통과 증거가 아닙니다.' }
        $gate.uiTestSha256 = Get-Sha256Text $uiPath
    }
    if ($Screens) {
        # G5 자동 검사 묶음 (M2 설계 7.5). 화면이 있는 마일스톤은 필수
        $g5Path = Join-Path $artifacts 'g5.json'
        if (-not (Test-Path $g5Path)) { throw 'G5 결과(g5.json)가 없습니다.' }
        $g5 = Get-Content $g5Path -Encoding UTF8 -Raw | ConvertFrom-Json
        if (-not $g5.passed -or $g5.sourceCommit -ne $X -or $g5.manifestSha256 -ne $manifestHash) { throw 'G5 결과가 X의 통과 증거가 아닙니다.' }
        # 기록된 캡처가 그대로 있는지 해시로 다시 확인한다 (G4 M2 1라운드 지적 10)
        foreach ($cap in @($g5.captures)) {
            $f = Join-Path $artifacts $cap.file
            if (-not (Test-Path $f)) { throw "G5 캡처가 없습니다: $($cap.file)" }
            if ((Get-Sha256Text $f) -ne $cap.sha256) { throw "G5 캡처가 기록과 다릅니다: $($cap.file)" }
        }
        if (@($g5.captures).Count -eq 0) { throw 'G5 캡처 목록이 비었습니다.' }
        $gate.g5Sha256 = Get-Sha256Text $g5Path
    }
    # G4: 대상이 X인 G4 요청문 중 가장 높은 라운드, 그 짝 답변의 첫 판정 줄
    $requests = Get-ChildItem (Join-Path $root '.loop\reviews') -Filter '*-request.md' -ErrorAction SilentlyContinue | ForEach-Object {
        $head = (Get-Content $_.FullName -Encoding UTF8 -TotalCount 8) -join "`n"
        if ($head -match '(?m)^검토:\s*G4\s*$' -and $head -match "(?m)^대상:\s*$X\s*$" -and $head -match '(?m)^라운드:\s*(\d+)\s*$') {
            [pscustomobject]@{ File = $_; Round = [int]$Matches[1] }
        }
    } | Sort-Object Round -Descending
    if (-not $requests) { throw "대상이 X인 G4 요청문이 없습니다." }
    $req = @($requests)[0]
    $answerPath = $req.File.FullName -replace '-request\.md$', '-answer.md'
    if (-not (Test-Path $answerPath)) { throw "G4 답변이 없습니다: $(Split-Path $answerPath -Leaf)" }
    $verdict = Get-FirstVerdictLine $answerPath
    if (-not $verdict) { throw 'G4 답변에 판정 줄이 없습니다 (미완료).' }
    if ($verdict -notmatch $script:ApprovePattern) { throw "G4가 승인하지 않았습니다: $($verdict.Trim())" }
    $gate.g4 = [ordered]@{ request = $req.File.Name; round = $req.Round; verdict = $verdict.Trim() }
    Complete-Step 'gate' $gate

    # ---- 2. 조상 확인 ----
    $headSha = (Invoke-Git rev-parse HEAD).Trim()
    & git merge-base --is-ancestor $X $headSha
    if ($LASTEXITCODE -ne 0) { throw 'X가 HEAD의 조상이 아닙니다.' }
    if (@(git status --porcelain --untracked-files=no).Count -gt 0) { throw '작업트리에 커밋되지 않은 변경이 있습니다.' }
    Complete-Step 'ancestor' $headSha

    # ---- 3. 기록 전용 확인 (커밋 하나하나) ----
    $after = @(Invoke-Git rev-list --reverse "$X..$headSha")
    # 병합 커밋은 diff-tree가 변경을 보여 주지 않아 검사를 우회할 수 있다. X 뒤에는 병합 커밋을 두지 않는다 (G4 1라운드 지적 1).
    $merges = @(Invoke-Git rev-list --merges "$X..$headSha")
    if ($merges.Count -gt 0) { throw "X 뒤에 병합 커밋이 있습니다: $(($merges | ForEach-Object { $_.Substring(0, 8) }) -join ', ')" }
    foreach ($c in $after) {
        $bad = @(Invoke-Git -c core.quotepath=off diff-tree --no-commit-id --name-only -r --root $c | Where-Object { $_ -and -not $_.StartsWith('docs/loop/') })
        if ($bad.Count -gt 0) { throw "X 뒤 커밋 $($c.Substring(0, 8))이 docs/loop/ 밖을 바꿨습니다: $($bad -join ', ')" }
    }
    Complete-Step 'record-only' @($after)

    # ---- 4. 공개 전 검사 ----
    $branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -eq 'main' -or $branch -eq 'HEAD') { throw "이 브랜치에서는 마감하지 않습니다: $branch" }
    Invoke-Git fetch origin --prune | Out-Null
    & git rev-parse --verify --quiet "refs/remotes/origin/$branch" | Out-Null
    $from = if ($LASTEXITCODE -eq 0) { "origin/$branch" } else { "origin/$Base" }
    & git rev-parse --verify --quiet "refs/remotes/$from" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "기준 원격 브랜치가 없습니다: $from" }
    $body = Get-Content $BodyFile -Encoding UTF8 -Raw
    if ($body -notmatch $X) { throw 'PR 본문에 승인받은 코드 SHA(X 전체)가 없습니다.' }
    & (Join-Path $root 'tools\loop\privacy-scan.ps1') -Range "$from..$headSha" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '공개 전 검사 실패 (커밋)' }
    & (Join-Path $root 'tools\loop\privacy-scan.ps1') -Text "$Title`n$body" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '공개 전 검사 실패 (PR 제목·본문)' }
    Complete-Step 'privacy' "$from..$headSha"

    # ---- 5. 푸시, PR ----
    Invoke-Git push -u origin "${branch}:refs/heads/$branch" | Out-Host   # 강제 푸시 없음
    $prs = Get-GhList pr list --head $branch --base $Base --state all --json 'number,state,url'
    $open = @($prs | Where-Object { $_.state -eq 'OPEN' })
    if ($open.Count -gt 0) {
        $pr = $open[0]
        # 재사용하는 PR도 방금 검사한 제목·본문으로 바꾼다 (이전 승인 SHA가 남지 않게, G4 1라운드 지적 3)
        Invoke-Gh pr edit $pr.number --title $Title --body-file $BodyFile | Out-Host
        Write-Host "기존 PR 재사용: #$($pr.number)"
    }
    elseif ($prs.Count -gt 0) { throw "같은 head·base의 PR이 닫혀 있습니다: #$($prs[0].number). 직접 확인이 필요합니다." }
    else {
        Invoke-Gh pr create --base $Base --head $branch --title $Title --body-file $BodyFile | Out-Host
        $created = Get-GhList pr list --head $branch --base $Base --state open --json 'number,state,url'
        if ($created.Count -ne 1) { throw "만든 PR을 찾지 못했습니다 ($($created.Count)개)" }
        $pr = $created[0]
    }
    # 실제로 공개된 제목·본문이 검사한 것과 같은지 확인한다
    $published = Get-GhObject pr view $pr.number --json 'title,body,baseRefName,headRefName'
    if ($published.title -ne $Title -or (Get-NormalizedText $published.body) -ne (Get-NormalizedText $body)) { throw 'PR의 제목·본문이 검사한 내용과 다릅니다.' }
    if ($published.baseRefName -ne $Base -or $published.headRefName -ne $branch) { throw 'PR의 base·head가 예상과 다릅니다.' }
    Complete-Step 'pr' ([ordered]@{ number = $pr.number; url = $pr.url })

    # ---- 6. CI ----
    $deadline = (Get-Date).AddMinutes($CiTimeoutMinutes)
    do {
        $prHead = (Get-GhObject pr view $pr.number --json headRefOid).headRefOid
        if ($prHead -eq $headSha) { break }
        Start-Sleep -Seconds 10
    } while ((Get-Date) -lt $deadline)
    if ($prHead -ne $headSha) { throw "PR head($prHead)가 푸시한 HEAD와 다릅니다." }
    $run = $null
    do {
        # 그 SHA의 가장 최근 실행 하나로 판단한다 (다시 돌린 실행이 있으면 그것)
        $runs = Get-GhList run list --commit $headSha --event pull_request --workflow ci.yml --json 'databaseId,status,conclusion,url,createdAt'
        $run = $runs | Sort-Object { [datetime]$_.createdAt } -Descending | Select-Object -First 1
        if ($run -and $run.status -eq 'completed') { break }
        Start-Sleep -Seconds 20
    } while ((Get-Date) -lt $deadline)
    if (-not $run) { throw "CI 실행이 $CiTimeoutMinutes 분 안에 시작되지 않았습니다." }
    if ($run.status -ne 'completed') { throw "CI가 $CiTimeoutMinutes 분 안에 끝나지 않았습니다: $($run.url)" }
    if ($run.conclusion -ne 'success') { throw "CI 실패 ($($run.conclusion)): $($run.url)" }
    Complete-Step 'ci' ([ordered]@{ run = $run.databaseId; url = $run.url; headSha = $headSha })

    $report.completed = $true
    Write-Host "마감 완료: PR #$($pr.number), CI 통과"
}
catch {
    $report.completed = $false
    $report.error = $_.Exception.Message
    throw
}
finally {
    New-Item -ItemType Directory -Force (Split-Path $resultPath) | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content $resultPath -Encoding UTF8
}
