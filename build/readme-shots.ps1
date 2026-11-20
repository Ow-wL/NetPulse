<#
.SYNOPSIS
  README 화면 캡처 (M10 설계 C [1-10]). 게이트 밖이다.

.DESCRIPTION
  게이트 빌드 산출물(-Artifacts)의 배포 exe로 UI 시험의 Kind=Docs(ReadmeShotsTests)만 돌려
  artifacts/readme-shots/<sha10>/ 에 PNG를 찍는다 (git 제외 폴더). 데모 모드·창만 캡처.
  찍은 것 중 사람이 고른 것만 docs/images/로 옮겨 커밋한다 (이 스크립트는 저장소를 바꾸지 않는다).
  시험이 PC 화면을 씁니다: 실행 중에는 PC를 쓰지 마세요.
#>
param([Parameter(Mandatory)] [string] $Artifacts)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. (Join-Path $PSScriptRoot 'common.ps1')

$Artifacts = (Resolve-Path $Artifacts).Path
$manifest = Get-Content (Join-Path $Artifacts 'manifest.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$short = $manifest.sourceCommit.Substring(0, 10)
$out = Join-Path $root "artifacts/readme-shots/$short"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
$env:NETPULSE_EXE = Join-Path $Artifacts 'app/NetPulse.exe'
$env:NETPULSE_UITEST_OUT = Join-Path $out 'work'
$env:NETPULSE_README_OUT = $out
$null = Invoke-Step 'readme-build' 300 'dotnet' @('build', 'tests/NetPulse.UiTests', '-c', 'Release') $out
$null = Invoke-Step 'readme-shots' 900 'dotnet' @('test', '--project', 'tests/NetPulse.UiTests', '-c', 'Release', '--no-build', '--filter-trait', 'Kind=Docs') $out
Remove-Item -Recurse -Force (Join-Path $out 'work') -ErrorAction SilentlyContinue
Get-ChildItem $out -Filter *.png | ForEach-Object { "{0,-14} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
Write-Host "README 캡처: artifacts/readme-shots/$short (고른 것만 docs/images로 옮겨 커밋)"
