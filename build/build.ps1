<#
.SYNOPSIS
  NetPulse 빌드 절차 (M1 설계 4절, M10 설계 B). 복원 → 빌드 → 로직·통합 테스트 → 단일 파일 배포 → 고지·이진 검사 → manifest → 배포 묶기.
  배포 파일(zip, exe, SHA256SUMS, release.json)을 만드는 문서화된 한 가지 절차다 (완성 정의 4).

.PARAMETER Gate
  게이트 모드. 추적 파일에 커밋되지 않은 변경이 하나라도 있거나(docs/loop 포함) .gitignore 밖의 추적되지 않은 파일이 있으면 실패한다.
  게이트 증거(evidence: true)는 이 모드로만 만든다.

.OUTPUTS
  artifacts/<SHA 앞 10자리>/ 아래에 app/(배포 exe 하나), tests/(TRX), manifest.json, release/(배포 파일 4개).
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
    foreach ($project in 'NetPulse.Core.Tests', 'NetPulse.Design.Tests', 'NetPulse.Platform.Tests') {
        $out = Join-Path $testDir $project
        $commands += Invoke-Step "test-$project" 300 'dotnet' @('test', '--project', "tests/$project", '-c', 'Release', '--no-build',
            '--report-xunit-trx', '--results-directory', $out) $work
        $trx = Get-ChildItem $out -Filter *.trx | Select-Object -First 1
        $neutral = Join-Path $testDir "$project.trx"   # 파일 이름에 사용자·컴퓨터 이름이 들어가므로 바꾼다
        Move-Item $trx.FullName $neutral
        Remove-Item -Recurse -Force $out
        $tests[$project] = Read-TrxSummary $neutral
        if (-not (Test-AllPassed $tests[$project])) { throw "테스트가 모두 통과하지 않았습니다 (실패·미실행 포함): $project" }
    }

    # ---- 4. 단일 파일 배포 ----
    $app = Join-Path $work 'app'
    $bundleList = Join-Path $root 'src/NetPulse.App/obj/Release/net10.0-windows10.0.22621.0/win-x64/bundle-inputs.tsv'
    if (Test-Path $bundleList) { Remove-Item $bundleList }   # 이번 publish가 새로 쓴 목록만 쓴다
    $commands += Invoke-Step 'publish' 300 'dotnet' @('publish', 'src/NetPulse.App/NetPulse.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:EnableMsixTooling=true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $app) $work
    # 배포 폴더는 exe 하나 (M10 설계 B2): App.xbf·.pri·글꼴·데모 시나리오는 모두 exe 안에 묶인다
    $appFiles = @(Get-ChildItem $app -Recurse -File | ForEach-Object Name)
    if ($appFiles.Count -ne 1 -or $appFiles[0] -ne 'NetPulse.exe') { throw "배포 폴더는 NetPulse.exe 하나여야 합니다: $($appFiles -join ', ')" }
    if (-not (Test-Path $bundleList)) { throw '묶음 파일 목록(bundle-inputs.tsv)이 만들어지지 않았습니다.' }

    # ---- 4-1. 고지·디버그 정보·이진 검사 (M10 설계 B4, 고지 수집) ----
    & (Join-Path $PSScriptRoot 'notices.ps1') -Check -BundleList $bundleList
    if ($LASTEXITCODE -ne 0) { throw '제3자 고지가 묶음 목록과 맞지 않습니다.' }
    $inputs = @(Get-Content $bundleList -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { $c = $_.Split("`t"); @{ Rel = $c[0]; Full = $c[1] } })
    Test-OwnAssembliesHaveNoDebugInfo @($inputs | Where-Object { $_.Rel -match '^NetPulse(\.(Core|Design|Platform))?\.dll$' } | ForEach-Object { $_.Full })
    $banned = Get-BannedValues
    Test-BannedScanner $banned
    # 공개 전 검사의 diff 읽기·이메일 예외 자체 시험 (G4 M10 1라운드 a1)
    & (Join-Path $root 'tools/loop/privacy-scan.ps1') -SelfTest | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '공개 전 검사 자체 시험 실패' }
    $scanHits = New-Object System.Collections.Generic.List[string]
    foreach ($f in $inputs) { foreach ($k in (Find-BannedInBytes ([IO.File]::ReadAllBytes($f.Full)) $banned)) { $scanHits.Add("bundle:$k") } }
    foreach ($k in (Find-BannedInBytes ([IO.File]::ReadAllBytes((Join-Path $app 'NetPulse.exe'))) $banned)) { $scanHits.Add("exe:$k") }
    if ($scanHits.Count -gt 0) { throw "배포 파일에 이 PC의 경로·이름이 있습니다 ($($scanHits.Count)건, 종류: $(($scanHits | Sort-Object -Unique) -join ', '))" }

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

    # ---- 6. 배포 묶기 (M10 설계 B1): zip, 같은 바이트의 exe, SHA256SUMS, release.json ----
    $version = Get-ReleaseVersion $root
    $names = Get-ReleaseNames $version
    $releaseDir = Join-Path $work 'release'
    New-Item -ItemType Directory -Force $releaseDir | Out-Null
    $stage = Join-Path $work 'release-stage'
    New-Item -ItemType Directory -Force $stage | Out-Null
    $utf8 = New-Object Text.UTF8Encoding($false)
    $readme = (Get-Content (Join-Path $PSScriptRoot 'release-readme.txt') -Encoding UTF8 -Raw).Replace('{version}', $version).Replace("`r`n", "`n").Replace("`n", "`r`n")
    [IO.File]::WriteAllText((Join-Path $stage 'readme.txt'), $readme, $utf8)
    $entries = @(
        @{ Name = 'NetPulse.exe'; Source = (Join-Path $app 'NetPulse.exe') },
        @{ Name = 'LICENSE.txt'; Source = (Join-Path $root 'LICENSE') },
        @{ Name = 'TERMS.txt'; Source = (Join-Path $root 'src/NetPulse.App/Legal/TERMS.txt') },
        @{ Name = 'THIRD-PARTY-NOTICES.txt'; Source = (Join-Path $root 'src/NetPulse.App/Legal/THIRD-PARTY-NOTICES.txt') },
        @{ Name = '읽어보기.txt'; Source = (Join-Path $stage 'readme.txt') })
    if ((($entries | ForEach-Object { $_.Name }) -join '|') -ne ($script:ReleaseZipEntries -join '|')) { throw 'zip 항목 목록이 정해진 순서와 다릅니다.' }
    $stamp = [DateTimeOffset]::Parse((git show -s --format=%cI HEAD).Trim(), [Globalization.CultureInfo]::InvariantCulture)
    $zipPath = Join-Path $releaseDir $names.zip
    New-DeterministicZip $zipPath $entries $stamp
    $again = Join-Path $stage 'again.zip'
    New-DeterministicZip $again $entries $stamp
    if ((Get-Sha256Text $zipPath) -ne (Get-Sha256Text $again)) { throw '같은 입력으로 만든 zip 두 개가 다릅니다 (결정적 zip).' }
    Copy-Item (Join-Path $app 'NetPulse.exe') (Join-Path $releaseDir $names.exe)
    $sums = @($names.zip, $names.exe | ForEach-Object { "$((Get-Sha256Text (Join-Path $releaseDir $_)).ToLowerInvariant())  $_" })
    [IO.File]::WriteAllText((Join-Path $releaseDir $names.sums), (($sums -join "`n") + "`n"), $utf8)
    foreach ($k in (Find-BannedInZip $zipPath $banned)) { $scanHits.Add("zip:$k") }
    if ($scanHits.Count -gt 0) { throw "배포 zip에 이 PC의 경로·이름이 있습니다 ($($scanHits.Count)건)" }
    $info = [ordered]@{
        version        = $version
        sourceCommit   = $sha
        manifestSha256 = Get-Sha256Text (Join-Path $work 'manifest.json')
        files          = @($names.zip, $names.exe, $names.sums | ForEach-Object { $p = Join-Path $releaseDir $_; [ordered]@{ name = $_; size = (Get-Item $p).Length; sha256 = Get-Sha256Text $p } })
        zipEntries     = @(Get-ZipEntryHashes $zipPath)
        exeSize        = (Get-Item (Join-Path $app 'NetPulse.exe')).Length
        exeSizeBefore  = 244964365   # M9 배포 exe (메타패키지, M10 설계 B3 비교 기준)
        bundleFiles    = $inputs.Count
        scan           = [ordered]@{ checkedKinds = @($banned | ForEach-Object { $_.Kind } | Sort-Object -Unique); hits = 0; selfTest = 'passed' }
        deterministicZip = $true
    }
    $infoText = $info | ConvertTo-Json -Depth 6
    foreach ($k in (Find-BannedInBytes ($utf8.GetBytes($infoText + (($sums -join "`n")))) $banned)) { throw "release.json·SHA256SUMS 글에 이 PC의 값이 있습니다 ($k)" }
    [IO.File]::WriteAllText((Join-Path $releaseDir $names.info), $infoText, $utf8)
    Remove-Item -Recurse -Force $stage
    $null = Test-Release $work $sha
    Write-Host "배포 파일: release/$($names.zip), release/$($names.exe) (exe $([math]::Round($info.exeSize / 1MB, 1)) MB)"

    Move-Item $work $final
    Write-Host "빌드 성공: artifacts/$short (evidence: $($manifest.evidence))"
}
catch {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    throw
}
