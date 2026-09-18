@echo off
setlocal
rem Builds the repository and launches exactly what was just built, so a freshly pulled tree is
rem what actually runs. Building is the normal path, not a fallback: a stale MediaWorkbench.exe
rem left over from an earlier commit must never be launched silently.
rem No absolute paths: everything is resolved relative to this script's folder, so the repo can live anywhere.
set "AME_ROOT=%~dp0.."
for %%I in ("%AME_ROOT%") do set "AME_ROOT=%%~fI"
set "AME_EXE=%AME_ROOT%\src\MediaWorkbench.App\bin\Release\net10.0-windows\MediaWorkbench.exe"

where dotnet >nul 2>nul
if errorlevel 1 goto :no_sdk

rem The dotnet muxer exits 0 even when global.json pins an SDK that is not installed, so the
rem installed major versions are checked explicitly rather than trusting the build exit code.
call :require_sdk 10
if errorlevel 1 goto :no_sdk

echo Building the Release configuration...
dotnet build "%AME_ROOT%\AdvancedMediaExtraction.slnx" --configuration Release --nologo
if errorlevel 1 goto :build_failed

if not exist "%AME_EXE%" goto :build_failed

:launch
call :report
echo Starting %AME_EXE%
start "" "%AME_EXE%"
exit /b 0

:no_sdk
echo.
echo The .NET 10 SDK was not found. The projects target net10.0 and global.json pins 10.0.200,
echo so an older SDK cannot build them.
echo.
echo Installed SDKs:
where dotnet >nul 2>nul && dotnet --list-sdks 2>nul
echo.
echo Install the required SDK with:
echo     winget install --id Microsoft.DotNet.SDK.10 --exact
echo.
if not exist "%AME_EXE%" (
    echo No previously built MediaWorkbench.exe was found either, so there is nothing to run.
    pause
    exit /b 1
)
echo WARNING: an existing build was found, but it CANNOT be refreshed without the SDK.
echo It may predate the commit you have checked out and will not show recent changes.
pause
goto :launch

:build_failed
echo.
echo Build failed. Run scripts\Verify.ps1 for full diagnostics.
echo Not launching, because any existing MediaWorkbench.exe would be from an earlier build.
pause
exit /b 1

:require_sdk
set "AME_SDK_OK="
for /f "usebackq tokens=1 delims=." %%I in (`dotnet --list-sdks 2^>nul`) do if "%%I"=="%~1" set "AME_SDK_OK=1"
if defined AME_SDK_OK exit /b 0
exit /b 1

rem Prints what is about to run, so "am I seeing the latest changes?" is answerable at a glance.
:report
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Item -LiteralPath $env:AME_EXE).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')" 2^>nul`) do echo Built:  %%I
where git >nul 2>nul || goto :eof
for /f "usebackq delims=" %%I in (`git -C "%AME_ROOT%" log -1 --oneline 2^>nul`) do echo Commit: %%I
goto :eof
