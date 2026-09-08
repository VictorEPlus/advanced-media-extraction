[CmdletBinding()]
param([string]$FfmpegDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Verify.ps1') -FfmpegDirectory $FfmpegDirectory
Push-Location -LiteralPath $root
try {
    $output = Join-Path $root 'artifacts\publish\win-x64'
    & dotnet publish src/MediaWorkbench.App/MediaWorkbench.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:RestoreLockedMode=true --output $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
    $docsOutput = Join-Path $output 'docs'
    New-Item -ItemType Directory -Path $docsOutput -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $docsOutput }
    $archive = Join-Path $root 'artifacts\MediaWorkbench-win-x64.zip'
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
    Write-Host "Published: $archive" -ForegroundColor Green
    Write-Host 'Extract the entire ZIP on the other Windows x64 machine. FFmpeg/FFprobe must still be installed or configured there.'
}
finally {
    Pop-Location
}
