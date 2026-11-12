<#
.SYNOPSIS
  공개 전 검사. 커밋하거나 푸시할 변경에 이 PC의 식별 정보가 들어 있는지 찾는다.

.DESCRIPTION
  이 파일에는 특정 PC의 값을 적지 않는다. 검사할 값(사용자 이름, 컴퓨터 이름,
  IP, MAC, 어댑터 이름, DNS 서버, SSID)은 실행할 때 이 PC에서 읽어 온다.

  -Staged          스테이징된 변경의 추가된 줄을 검사한다.
  -Range a..b      범위 안의 커밋을 하나씩 검사한다. 중간 커밋에 넣었다 지운 값도 찾는다.
  -Text "..."      주어진 문자열(PR 본문 등)을 검사한다.

  찾으면 종료 코드 1, 없으면 0.
#>
param(
    [switch] $Staged,
    [string] $Range,
    [string] $Text
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Get-LocalIdentifiers {
    $values = New-Object System.Collections.Generic.List[string]
    foreach ($v in @($env:USERNAME, $env:COMPUTERNAME, $env:USERPROFILE)) { if ($v) { $values.Add($v) } }
    try {
        Get-NetIPAddress -ErrorAction Stop | Where-Object {
            $_.IPAddress -notmatch '^(127\.|::1$|fe80:)' } | ForEach-Object { $values.Add(($_.IPAddress -split '%')[0]) }
    } catch { }
    try {
        Get-NetAdapter -ErrorAction Stop | ForEach-Object {
            if ($_.MacAddress) { $values.Add($_.MacAddress); $values.Add($_.MacAddress.Replace('-', ':')) }
            if ($_.InterfaceDescription) { $values.Add($_.InterfaceDescription) }
        }
    } catch { }
    try {
        Get-DnsClientServerAddress -ErrorAction Stop | ForEach-Object { $_.ServerAddresses } |
            Where-Object { $_ -and $_ -notmatch '^fec0:' } | ForEach-Object { $values.Add($_) }
    } catch { }
    try {
        $wlan = netsh wlan show interfaces 2>$null
        foreach ($line in $wlan) {
            if ($line -match '^\s*SSID\s*:\s*(.+)$') { $values.Add($Matches[1].Trim()) }
        }
    } catch { }
    # 너무 짧거나 흔한 값은 오탐이 많아 뺀다.
    $common = @('Ethernet', 'Wi-Fi', 'WiFi', 'Loopback', '0.0.0.0', '1.1.1.1', '8.8.8.8', '8.8.4.4')
    return $values | Where-Object { $_.Length -ge 4 -and $common -notcontains $_ } | Sort-Object -Unique
}

$patterns = @(
    @{ Name = '사설 IPv4 (192.168)'; Regex = '(?<![\d.])192\.168\.\d{1,3}\.\d{1,3}(?![\d])' },
    @{ Name = '사설 IPv4 (10/8)';    Regex = '(?<![\d.])10\.\d{1,3}\.\d{1,3}\.\d{1,3}(?![\d])' },
    @{ Name = '사설 IPv4 (172.16/12)'; Regex = '(?<![\d.])172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}(?![\d])' },
    @{ Name = 'MAC 주소';            Regex = '(?<![0-9A-Fa-f:-])([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])' },
    @{ Name = '사용자 폴더 경로';     Regex = '(?i)[A-Z]:[\\/]+Users[\\/]+[^\\/\s]+' },
    @{ Name = '이메일 주소';          Regex = '[A-Za-z0-9._%+-]+@(gmail|naver|daum|hanmail|outlook|hotmail|kakao)\.[A-Za-z.]+' }
)
$locals = Get-LocalIdentifiers

function Test-Lines([string[]] $lines, [string] $where) {
    foreach ($line in $lines) {
        foreach ($p in $patterns) {
            if ($line -match $p.Regex) { Write-Output ("[{0}] {1}: {2}" -f $where, $p.Name, $Matches[0]) }
        }
        foreach ($v in $locals) {
            if ($line.IndexOf($v, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Write-Output ("[{0}] 이 PC의 식별 값 (길이 {1})" -f $where, $v.Length)
            }
        }
    }
}

function Get-AddedLines([string[]] $diff) {
    $diff | Where-Object { $_.StartsWith('+') -and -not $_.StartsWith('+++') } | ForEach-Object { $_.Substring(1) }
}

$total = 0
if ($Staged) {
    $diff = git -c core.quotepath=off diff --cached --unified=0 --no-color
    $hits = @(Test-Lines (Get-AddedLines $diff) 'staged')
    $hits | Out-Host
    $total += $hits.Count
}
if ($Range) {
    foreach ($sha in (git rev-list --reverse $Range)) {
        $diff = git -c core.quotepath=off show --unified=0 --no-color --format= $sha
        $hits = @(Test-Lines (Get-AddedLines $diff) $sha.Substring(0, 8))
        $hits | Out-Host
        $total += $hits.Count
    }
    $message = git log --format=%B $Range
    $hits = @(Test-Lines $message 'commit-message')
    $hits | Out-Host
    $total += $hits.Count
}
if ($Text) {
    $hits = @(Test-Lines ($Text -split "`n") 'text')
    $hits | Out-Host
    $total += $hits.Count
}

if ($total -gt 0) { Write-Output "공개 전 검사 실패: $total 건"; exit 1 }
Write-Output "공개 전 검사 통과"
exit 0
