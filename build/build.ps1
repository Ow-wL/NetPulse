<#
.SYNOPSIS
  NetPulse 빌드 절차 (M1 설계 4절). 복원 → 빌드 → 로직·통합 테스트 → 단일 파일 배포 → manifest.

.PARAMETER Gate
  게이트 모드. 추적 파일에 커밋되지 않은 변경이 하나라도 있거나(docs/loop 포함) .gitignore 밖의 추적되지 않은 파일이 있으면 실패한다.
  게이트 증거(evidence: true)는 이 모드로만 만든다.

.OUTPUTS
  artifacts/<SHA 앞 10자리>/ 아래에 app/(배포 폴더), tests/(TRX), manifest.json.
  시작할 때 같은 SHA의 이전 산출물을 지우고, 임시 폴더에 만든 뒤 모든 단계가 성공했을 때만 옮긴다.
#>
param([switch] $Gate)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. (Join-Path $PSScriptRoot 'common.ps1')

# ---- 1. 소스 식별과 작업트리 검사 ----
$sha = (git rev-parse HEAD).Trim()
$short = $sha.Substring(0, 10)
$trackedChanges = @(git status --porcelain --untracked-files=no)
$untracked = @(git ls-files --others --exclude-standard)
$clean = ($trackedChanges.Count -eq 0) -and ($untracked.Count -eq 0)
if ($Gate -and -not $clean) {
    $trackedChanges + $untracked | ForEach-Object { Write-Host "  $_" }
    throw '게이트 모드: 작업트리가 깨끗하지 않습니다. 커밋한 뒤 다시 실행하세요.'
}
if (-not $clean) { Write-Warning '작업트리에 커밋되지 않은 변경이 있습니다. 이 산출물은 게이트 증거로 쓸 수 없습니다 (evidence: false).' }

$artifactsRoot = Join-Path $root 'artifacts'
$final = Join-Path $artifactsRoot $short
if (Test-Path $final) { Remove-Item -Recurse -Force $final }   # 이전 증거를 먼저 무효화한다
$work = Join-Path $artifactsRoot (".tmp-$short-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force $work | Out-Null
$sdk = (dotnet --version).Trim()
$commands = @()

try {
    # ---- 2. 복원, 빌드 (경고를 오류로) ----
    $commands += Invoke-Step 'restore' 300 'dotnet' @('restore', 'NetPulse.slnx') $work
    $commands += Invoke-Step 'build' 300 'dotnet' @('build', 'NetPulse.slnx', '-c', 'Release', '--no-restore') $work

    # ---- 3. 로직·통합 테스트 (UI 테스트는 build/ui-test.ps1) ----
    $testDir = Join-Path $work 'tests'
    $tests = [ordered]@{}
    foreach ($project in 'NetPulse.Core.Tests', 'NetPulse.Platform.Tests') {
        $out = Join-Path $testDir $project
        $commands += Invoke-Step "test-$project" 300 'dotnet' @('test', '--project', "tests/$project", '-c', 'Release', '--no-build',
            '--report-xunit-trx', '--results-directory', $out) $work
        $trx = Get-ChildItem $out -Filter *.trx | Select-Object -First 1
        $neutral = Join-Path $testDir "$project.trx"   # 파일 이름에 사용자·컴퓨터 이름이 들어가므로 바꾼다
        Move-Item $trx.FullName $neutral
        Remove-Item -Recurse -Force $out
        $tests[$project] = Read-TrxSummary $neutral
        if ($tests[$project].failed -gt 0 -or $tests[$project].total -eq 0) { throw "테스트 실패: $project" }
    }

    # ---- 4. 단일 파일 배포 ----
    $app = Join-Path $work 'app'
    $commands += Invoke-Step 'publish' 300 'dotnet' @('publish', 'src/NetPulse.App/NetPulse.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:EnableMsixTooling=true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $app) $work
    Get-ChildItem $app -Recurse -Filter *.pdb | Remove-Item
    # 실행 파일 옆에 있어야 하는 파일. 글꼴·데모 시나리오는 실행 파일 안에 묶이고, 앱이 찾는지는 G3가 시작 기록으로 확인한다.
    foreach ($required in 'NetPulse.exe', 'App.xbf', 'NetPulse.pri') {
        if (-not (Test-Path (Join-Path $app $required))) { throw "배포 폴더에 $required 이(가) 없습니다." }
    }

    # ---- 5. manifest ----
    $manifest = [ordered]@{
        sourceCommit = $sha
        evidence     = [bool]($Gate -and $clean)
        worktreeClean = $clean
        sdk          = $sdk
        builtAt      = (Get-Date).ToString('o')
        commands     = $commands
        files        = @(Get-FileManifest $app)
        tests        = $tests
        testFiles    = @(Get-ChildItem $testDir -Filter *.trx | ForEach-Object { [ordered]@{ path = "tests/$($_.Name)"; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } })
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $work 'manifest.json') -Encoding UTF8

    Move-Item $work $final
    Write-Host "빌드 성공: artifacts/$short (evidence: $($manifest.evidence))"
}
catch {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    throw
}
