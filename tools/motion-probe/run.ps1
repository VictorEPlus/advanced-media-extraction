# Records Media Workbench at 60 fps while a script opens folders, clicks files, steps, plays and pauses, then reports
# what moved, what flashed, and which video frame was on screen. The window appears on the main screen, on top, for
# about a minute; leave the mouse and keyboard alone until it closes.
#
#   tools/motion-probe/run.ps1            build Release, record, analyse
#   tools/motion-probe/run.ps1 -Images    also save a marked-up picture of every move and flash
#   tools/motion-probe/run.ps1 -Quick     only the app's layout log and the video barcode: seconds, not minutes
[CmdletBinding()]
param([switch]$Images, [switch]$NoBuild, [switch]$Quick, [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $here)
$python = Join-Path $here '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    # A private environment for this tool only; nothing is installed into the system Python.
    & py -3.12 -m venv (Join-Path $here '.venv')
    & $python -m pip install --disable-pip-version-check --quiet -r (Join-Path $here 'requirements.txt')
}
if (-not $NoBuild) {
    & dotnet build (Join-Path $root 'src\MediaWorkbench.App\MediaWorkbench.App.csproj') -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$run = Join-Path $root ('artifacts\motion-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$exe = Join-Path $root "src\MediaWorkbench.App\bin\$Configuration\net10.0-windows\MediaWorkbench.exe"
$process = Start-Process -FilePath $exe -ArgumentList @('--motion-probe', '--data-dir', ('"' + $run + '"')) -PassThru
if (-not $process.WaitForExit(240000)) { $process.Kill(); throw "The probe did not finish. See $run" }
if ($process.ExitCode -ne 0) { throw "The probe failed. See $run\startup-error.log" }
$arguments = @((Join-Path $here 'analyze.py'), $run)
if ($Images) { $arguments += '--images' }
if ($Quick) { $arguments += '--quick' }
& $python @arguments
