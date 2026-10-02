@echo off
rem Double-clickable entry point for the new Avalonia version of Media Workbench (branch avalonia-shell).
rem Builds the repository, then starts the Avalonia app. Launch.cmd still starts the WPF app.
call "%~dp0scripts\Launch.bat" avalonia
