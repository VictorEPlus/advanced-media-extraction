@echo off
rem Double-clickable entry point for Media Workbench.
rem %~dp0 expands to this file's own folder, so this works from any clone location with no
rem absolute paths and nothing to reconfigure. This replaces the "Media Workbench.lnk" shortcut,
rem which could not be made portable: a .lnk stores an absolute working directory, so it only ever
rem worked on the machine that created it.
call "%~dp0scripts\Launch.bat" %*
