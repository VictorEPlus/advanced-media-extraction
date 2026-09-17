@echo off
setlocal
rem Launches the newest MediaWorkbench.exe found under this repository (any configuration or publish output).
rem No absolute paths: everything is resolved relative to this script's folder, so the repo can live anywhere.
set "AME_ROOT=%~dp0.."
for %%I in ("%AME_ROOT%") do set "AME_ROOT=%%~fI"

call :find_exe
if defined AME_EXE goto :launch

echo No build found under "%AME_ROOT%". Building the Release configuration first...
where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET SDK was not found. Install it with: winget install --id Microsoft.DotNet.SDK.10 --exact
    pause
    exit /b 1
)
dotnet build "%AME_ROOT%\AdvancedMediaExtraction.slnx" --configuration Release
if errorlevel 1 (
    echo Build failed. Run scripts\Verify.ps1 for full diagnostics.
    pause
    exit /b 1
)
call :find_exe
if not defined AME_EXE (
    echo The build finished but MediaWorkbench.exe was not found.
    pause
    exit /b 1
)

:launch
echo Starting %AME_EXE%
start "" "%AME_EXE%"
exit /b 0

:find_exe
set "AME_EXE="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$roots = @((Join-Path $env:AME_ROOT 'src\MediaWorkbench.App\bin'), (Join-Path $env:AME_ROOT 'artifacts')) | Where-Object { Test-Path -LiteralPath $_ }; Get-ChildItem -LiteralPath $roots -Recurse -Filter MediaWorkbench.exe -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\smoke-' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName"`) do set "AME_EXE=%%I"
exit /b 0
