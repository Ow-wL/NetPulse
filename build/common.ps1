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
    $lines += "- 전용 메모리: 전환 2회 뒤 $($m.at2Mb) MB, 22회 뒤 $($m.at22Mb) MB, 62회 뒤 $($m.at62Mb) MB (22→62회 증가 $($m.plateauGrowthMb) MB)"
    $l = $Bench.leftovers
    $lines += "- 남은 것: 찍은 화면 $($l.snapshotLayers), 반복 모션 $($l.repeatsAtStart)→$($l.repeatsAtEnd), 모드 구독자 $($l.subscribersAtStart)→$($l.subscribersAtEnd)"
    $lines | Set-Content $Path -Encoding UTF8
}

# ================= 배포 묶기·검사 (M10 설계 B1·B4) =================

$script:ReleaseZipEntries = @('NetPulse.exe', 'LICENSE.txt', 'TERMS.txt', 'THIRD-PARTY-NOTICES.txt', '읽어보기.txt')

function Get-ReleaseVersion([string] $Root) {
    <# 버전의 유일한 출처: Directory.Build.props의 Version (MSBuild가 읽은 값) #>
    $v = (& dotnet msbuild (Join-Path $Root 'src/NetPulse.App/NetPulse.App.csproj') -getProperty:Version -p:Configuration=Release -nologo).Trim()
    if ($LASTEXITCODE -ne 0 -or $v -notmatch '^\d+\.\d+\.\d+$') { throw "버전을 읽지 못했습니다: $v" }
    return $v
}

function Get-ReleaseNames([string] $Version) {
    [ordered]@{ zip = "NetPulse-$Version-win-x64.zip"; exe = "NetPulse-$Version-win-x64.exe"; sums = 'SHA256SUMS.txt'; info = 'release.json' }
}

function New-DeterministicZip([string] $Path, [object[]] $Entries, [DateTimeOffset] $Stamp) {
    <#
      같은 입력이면 같은 zip (B1 [1-12]): 항목 순서는 주어진 순서, 시각은 Stamp(UTC)를 ZIP 2초 정밀도로 내림,
      압축 Optimal, 외부 속성 0, 주석 없음. Entries: @{ Name; Source }.
    #>
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $utc = $Stamp.ToUniversalTime()
    $time = New-Object DateTimeOffset ($utc.Year, $utc.Month, $utc.Day, $utc.Hour, $utc.Minute, ($utc.Second - ($utc.Second % 2)), [TimeSpan]::Zero)
    if (Test-Path $Path) { Remove-Item $Path }
    $fs = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
        try {
            foreach ($e in $Entries) {
                $entry = $zip.CreateEntry($e.Name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $time
                $entry.ExternalAttributes = 0
                $out = $entry.Open()
                try { $in = [IO.File]::OpenRead($e.Source); try { $in.CopyTo($out) } finally { $in.Dispose() } } finally { $out.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $fs.Dispose() }
}

function Get-ZipEntryHashes([string] $Path) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        @($zip.Entries | ForEach-Object {
            $s = $_.Open()
            try { $h = [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-', '') } finally { $s.Dispose() }
            [ordered]@{ name = $_.FullName; size = $_.Length; sha256 = $h }
        })
    } finally { $zip.Dispose() }
}

function Get-BannedValues {
    <#
      이 PC(또는 CI 실행기)의 식별 값 (B4): 사용자 이름, 컴퓨터 이름, 사용자 폴더, 저장소 절대 경로, NuGet 캐시, 임시 폴더.
      값 자체는 산출물·로그에 쓰지 않는다 (종류만).
    #>
    $sep = [string][char]92
    $list = New-Object System.Collections.Generic.List[object]
    $add = { param($kind, $value)
        if (-not $value -or $value.Length -lt 4) { return }
        $v = $value.TrimEnd($sep, '/')
        $list.Add(@{ Kind = $kind; Value = $v })
        if ($v.Contains($sep)) { $list.Add(@{ Kind = $kind; Value = $v.Replace($sep, '/') }) }
    }
    & $add 'user' $env:USERNAME
    & $add 'computer' $env:COMPUTERNAME
    & $add 'profile' $env:USERPROFILE
    & $add 'repo' ((Get-Location).Path)
    & $add 'temp' ([IO.Path]::GetTempPath())
    $nuget = $env:NUGET_PACKAGES
    if (-not $nuget) { $nuget = (((& dotnet nuget locals global-packages --list) | Select-Object -First 1) -replace '^\s*global-packages:\s*', '').Trim() }
    & $add 'nuget' $nuget
    return $list
}

function Find-BannedInBytes([byte[]] $Bytes, [object[]] $Banned) {
    <#
      바이트를 Latin-1 글로 보고 UTF-8·UTF-16LE 표기를 대소문자 무시로 찾는다. 찾은 종류 목록.
      Latin-1로 본 UTF-8 바이트는 ASCII 밖 글자의 대소문자 관계를 잃는다(예: J-ö-rg와 J-Ö-RG의 UTF-8): 값 그대로·전부 대문자·전부 소문자
      세 가지 표기를 따로 찾는다 (G4 M10 1라운드 a5). 비ASCII 글자가 섞인 대소문자 혼합 변형은 찾지 못한다 (ASCII 부분은 대소문자 무시).
    #>
    $latin = [Text.Encoding]::GetEncoding(28591)
    $hay = $latin.GetString($Bytes)
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($b in $Banned) {
        $forms = @($b.Value, $b.Value.ToUpperInvariant(), $b.Value.ToLowerInvariant()) | Select-Object -Unique
        foreach ($enc in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
            foreach ($form in $forms) {
                $needle = $latin.GetString($enc.GetBytes($form))
                if ($hay.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $found.Add("$($b.Kind)/$($enc.WebName)"); break }
            }
        }
    }
    return @($found | Sort-Object -Unique)
}

function Test-BannedScanner([object[]] $Banned) {
    <# 검사기 자체 시험 (B4): 표식 값을 UTF-8·UTF-16LE로 넣은 바이트와 zip 항목에서 모두 찾아야 한다 #>
    $marker = @(@{ Kind = 'marker'; Value = 'C:/np-marker-' + [guid]::NewGuid().ToString('N').Substring(0, 8) })
    foreach ($enc in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $bytes = [byte[]](@(0, 1, 2) + $enc.GetBytes('xx' + $marker[0].Value.ToUpperInvariant() + 'yy') + @(3, 4))
        if ((Find-BannedInBytes $bytes $marker).Count -eq 0) { throw "이진 검사기 자체 시험 실패: $($enc.WebName) 표식을 찾지 못했습니다." }
    }
    # 비ASCII 이름 (o 움라우트): 값과 대문자·소문자 표기 (G4 M10 1라운드 a5)
    $name = 'C:/np-home/J' + [char]0xF6 + 'rg-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
    $wide = @(@{ Kind = 'marker'; Value = $name })
    foreach ($enc in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        foreach ($form in @($name.ToUpperInvariant(), $name.ToLowerInvariant())) {
            $bytes = [byte[]](@(9) + $enc.GetBytes($form) + @(9))
            if ((Find-BannedInBytes $bytes $wide).Count -eq 0) { throw "이진 검사기 자체 시험 실패: 비ASCII 이름의 $($enc.WebName) 대소문자 표기를 찾지 못했습니다." }
        }
    }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('np-scan-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $src = Join-Path $tmp 'a.txt'
        [IO.File]::WriteAllText($src, 'zz ' + $marker[0].Value, (New-Object Text.UTF8Encoding($false)))
        $zipPath = Join-Path $tmp 'a.zip'
        New-DeterministicZip $zipPath @(@{ Name = 'a.txt'; Source = $src }) ([DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero))
        if ((Find-BannedInZip $zipPath $marker).Count -eq 0) { throw '이진 검사기 자체 시험 실패: zip 항목 안 표식을 찾지 못했습니다.' }
    } finally { Remove-Item -Recurse -Force $tmp }
}

function Find-BannedInZip([string] $Path, [object[]] $Banned) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    $found = New-Object System.Collections.Generic.List[string]
    try {
        foreach ($e in $zip.Entries) {
            $ms = New-Object IO.MemoryStream
            $s = $e.Open(); try { $s.CopyTo($ms) } finally { $s.Dispose() }
            foreach ($k in (Find-BannedInBytes $ms.ToArray() $Banned)) { $found.Add("$($e.FullName):$k") }
        }
    } finally { $zip.Dispose() }
    return @($found)
}

function Test-OwnAssembliesHaveNoDebugInfo([string[]] $Paths) {
    <#
      자체 빌드 어셈블리(NetPulse*.dll)에 디버그 정보가 없는지 (B4 [2-3], DebugType=none): CodeView(RSDS)·내장 PDB(MPDB) 표식이 없어야 한다.
      제3자 어셈블리의 CodeView 경로는 허용하고 금지 값 검사만 한다.
    #>
    $latin = [Text.Encoding]::GetEncoding(28591)
    foreach ($p in $Paths) {
        $hay = $latin.GetString([IO.File]::ReadAllBytes($p))
        if ($hay.Contains('RSDS') -or $hay.Contains('MPDB')) { throw "자체 어셈블리에 디버그 정보가 있습니다: $([IO.Path]::GetFileName($p))" }
    }
}

function Get-CommitVersion([string] $Commit) {
    <# 그 커밋의 Directory.Build.props에 적힌 Version (버전의 유일한 출처를 커밋에서 직접 읽는다, G4 M10 1라운드 a3) #>
    $props = (git show "${Commit}:Directory.Build.props") -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "커밋 $Commit 의 Directory.Build.props를 읽지 못했습니다." }
    $m = [regex]::Matches($props, '<Version>\s*([^<\s]+)\s*</Version>')
    if ($m.Count -ne 1 -or $m[0].Groups[1].Value -notmatch '^\d+\.\d+\.\d+$') { throw "커밋 $Commit 의 Version을 하나로 정하지 못했습니다." }
    return $m[0].Groups[1].Value
}

function Test-Release([string] $Artifacts, [string] $Commit) {
    <#
      배포 묶음 검사 (B1 [1-4][2-8]). 빌드와 마감이 함께 쓴다. 하나라도 어긋나면 예외.
      manifest의 app 해시 재계산, release.json의 커밋·manifest 해시, 버전 = 그 커밋의 Directory.Build.props Version = exe 제품 버전(+커밋),
      release 파일 목록(정확히 4개)과 release.json의 파일 기록(정확히 3개, 이름별 크기·해시), SHA256SUMS,
      zip 항목(정해진 5개, 순서)과 release.json의 zip 기록(정확히 5개, 이름별 크기·해시), zip 안 exe = 따로 낸 exe = app/NetPulse.exe.
    #>
    $manifestPath = Join-Path $Artifacts 'manifest.json'
    $manifest = Get-Content $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
    $actual = @(Get-FileManifest (Join-Path $Artifacts 'app'))
    $expected = @($manifest.files)
    if ($actual.Count -ne 1 -or $actual[0].path -ne 'NetPulse.exe') { throw '배포 폴더(app)는 NetPulse.exe 하나여야 합니다.' }
    if ($expected.Count -ne 1 -or $expected[0].sha256 -ne $actual[0].sha256) { throw '배포 exe가 manifest와 다릅니다.' }
    $releaseDir = Join-Path $Artifacts 'release'
    $info = Get-Content (Join-Path $releaseDir 'release.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    if ($info.sourceCommit -ne $Commit) { throw "release.json의 커밋이 다릅니다: $($info.sourceCommit)" }
    if ($info.manifestSha256 -ne (Get-Sha256Text $manifestPath)) { throw 'release.json의 manifest 해시가 실제 manifest와 다릅니다.' }
    $version = Get-CommitVersion $Commit
    if ($info.version -ne $version) { throw "release.json의 버전($($info.version))이 커밋의 Version($version)과 다릅니다." }
    $exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Artifacts 'app/NetPulse.exe'))
    if ($exeVersion.ProductVersion -ne "$version+$Commit") { throw "exe 제품 버전($($exeVersion.ProductVersion))이 $version+<커밋>이 아닙니다." }
    if ($exeVersion.FileVersion -ne "$version.0") { throw "exe 파일 버전($($exeVersion.FileVersion))이 $version.0이 아닙니다." }
    $names = Get-ReleaseNames $version
    $files = @(Get-ChildItem $releaseDir -File | Sort-Object Name | ForEach-Object Name)
    $want = @($names.zip, $names.exe, $names.sums, $names.info | Sort-Object)
    if (($files -join '|') -ne ($want -join '|')) { throw "release 파일 목록이 다릅니다: $($files -join ', ')" }
    $records = @($info.files)
    $recordNames = @($records | ForEach-Object { [string]$_.name })
    $wantRecords = @($names.zip, $names.exe, $names.sums)
    if ($records.Count -ne 3 -or (($recordNames | Sort-Object -Unique) -join '|') -ne (($wantRecords | Sort-Object) -join '|')) { throw "release.json의 파일 기록이 정해진 3개가 아닙니다: $($recordNames -join ', ')" }
    foreach ($name in $wantRecords) {
        $f = @($records | Where-Object { $_.name -eq $name })[0]
        $p = Join-Path $releaseDir $name
        if ((Get-Item $p).Length -ne [long]$f.size -or (Get-Sha256Text $p) -ne $f.sha256) { throw "release 파일이 기록과 다릅니다: $name" }
    }
    $sums = @(Get-Content (Join-Path $releaseDir $names.sums) -Encoding UTF8 | Where-Object { $_.Trim() })
    $wantSums = @($names.zip, $names.exe | ForEach-Object { "$((Get-Sha256Text (Join-Path $releaseDir $_)).ToLowerInvariant())  $_" })
    if (($sums -join '|') -ne ($wantSums -join '|')) { throw 'SHA256SUMS.txt가 파일과 맞지 않습니다.' }
    $entries = @(Get-ZipEntryHashes (Join-Path $releaseDir $names.zip))
    if ((($entries | ForEach-Object { $_.name }) -join '|') -ne ($script:ReleaseZipEntries -join '|')) { throw "zip 항목이 다릅니다: $(($entries | ForEach-Object { $_.name }) -join ', ')" }
    $zipRecords = @($info.zipEntries)
    if ((($zipRecords | ForEach-Object { [string]$_.name }) -join '|') -ne ($script:ReleaseZipEntries -join '|')) { throw "release.json의 zip 기록이 정해진 5개(순서)가 아닙니다." }
    for ($i = 0; $i -lt $entries.Count; $i++) {
        if ($entries[$i].sha256 -ne $zipRecords[$i].sha256 -or [long]$entries[$i].size -ne [long]$zipRecords[$i].size) { throw "zip 항목이 기록과 다릅니다: $($entries[$i].name)" }
    }
    $exeHash = (Get-Sha256Text (Join-Path $releaseDir $names.exe))
    if ($entries[0].sha256 -ne $exeHash -or $exeHash -ne $actual[0].sha256) { throw 'zip 안 exe, 따로 낸 exe, app/NetPulse.exe가 같지 않습니다.' }
    return $info
}
