param([string] $Output = (Join-Path $PSScriptRoot 'NetPulse.exe'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.Xaml.dll', 'System.Net.Http.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$assemblyName = [IO.Path]::GetFileNameWithoutExtension($Output)
$fontResources = Join-Path ([IO.Path]::GetTempPath()) ("netpulse-" + [guid]::NewGuid().ToString('N') + ".resources")
try {
    $writer = New-Object System.Resources.ResourceWriter($fontResources)
    try {
        foreach ($weight in 'Regular','SemiBold','Bold') {
            $file = "Pretendard-$weight.otf"
            $font = [IO.File]::OpenRead((Join-Path $PSScriptRoot "fonts\$file"))
            $writer.AddResource("fonts/" + $file.ToLowerInvariant(), $font, $true)
        }
    }
    finally { $writer.Dispose() }

    $arguments = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$Output",
        "/resource:$(Join-Path $PSScriptRoot 'Dashboard.xaml'),Dashboard.xaml",
        "/resource:$fontResources,$assemblyName.g.resources",
        "/resource:$(Join-Path $PSScriptRoot 'fonts\OFL-Pretendard.txt'),OFL-Pretendard.txt",
        "/win32manifest:$(Join-Path $PSScriptRoot 'App.manifest')",
        "/win32icon:$(Join-Path $PSScriptRoot 'NetPulse.ico')")
    $arguments += $references | ForEach-Object { "/reference:$(Join-Path $framework $_)" }
    $arguments += @('Dashboard.cs', 'Monitor.cs', 'Comparison.cs', 'Evidence.cs', 'Analysis.cs', 'AnalysisWindow.cs', 'QuickDiagnostics.cs', 'QuickWindow.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "컴파일 실패 ($LASTEXITCODE)" }
}
finally {
    if (Test-Path -LiteralPath $fontResources) { Remove-Item -LiteralPath $fontResources }
}
