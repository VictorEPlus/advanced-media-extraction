[CmdletBinding()]
param(
    [switch]$Launch,
    [string]$FfmpegDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'The desktop application requires Windows x64.'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 10 SDK: winget install --id Microsoft.DotNet.SDK.10 --exact. Then reopen PowerShell.'
    }
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.0\.' -or [version]$sdk -lt [version]'10.0.200') {
        throw 'Install .NET SDK 10.0.200 or newer in the 10.0 family, then reopen PowerShell.'
    }
    Write-Host "Using .NET SDK $sdk" -ForegroundColor Cyan
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    if ($FfmpegDirectory) {
        $env:AME_FFMPEG_DIR = (Resolve-Path -LiteralPath $FfmpegDirectory).Path
    }
    if (-not $env:AME_FFMPEG_DIR) {
        $localTools = Join-Path $root 'tools\ffmpeg\bin'
        if (Test-Path -LiteralPath $localTools) { $env:AME_FFMPEG_DIR = $localTools }
    }
    foreach ($tool in @('ffmpeg', 'ffprobe')) {
        $executable = $tool
        if ($env:AME_FFMPEG_DIR) { $executable = Join-Path $env:AME_FFMPEG_DIR "$tool.exe" }
        if (-not (Get-Command $executable -ErrorAction SilentlyContinue)) {
            throw "Missing $tool. Install FFmpeg with: winget install --id Gyan.FFmpeg --exact. Reopen PowerShell, or pass -FfmpegDirectory 'C:\tools\ffmpeg\bin'."
        }
        $versionOutput = & $executable -version
        if ($LASTEXITCODE -ne 0) { throw "$tool failed its startup check." }
        Write-Host $versionOutput[0]
    }
    & dotnet restore AdvancedMediaExtraction.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed. Check network/NuGet access and the SDK version.' }
    & dotnet build AdvancedMediaExtraction.slnx --no-restore --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. See compiler output above.' }
    & dotnet test tests/MediaWorkbench.Tests/MediaWorkbench.Tests.csproj --no-build --no-restore --configuration $Configuration --logger 'trx;LogFileName=tests.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. See artifacts/test-results/tests.trx.' }
    $executable = Join-Path $root "src\MediaWorkbench.App\bin\$Configuration\net10.0-windows\MediaWorkbench.exe"
    $smokeDirectory = Join-Path $root ('artifacts\smoke-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $executable -ArgumentList @('--smoke-test', '--data-dir', ('"' + $smokeDirectory + '"')) -WindowStyle Hidden -PassThru
    # The full desktop check plays, scans, tags and exports real media; it takes a few minutes.
    if (-not $process.WaitForExit(600000)) {
        $process.Kill()
        throw "Desktop startup timed out. Inspect $smokeDirectory."
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $smokeDirectory 'smoke-test.txt'))) {
        throw "Desktop startup check failed. Inspect $smokeDirectory\startup-error.log."
    }
    Write-Host 'PASS: build, unit tests, media integration tests, and desktop startup.' -ForegroundColor Green
    if ($Launch) {
        Start-Process -FilePath $executable -WindowStyle Normal
    }
}
finally {
    Pop-Location
}
