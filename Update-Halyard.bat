@echo off
rem DEVELOPMENT PC ONLY. Pulls the latest Halyard from git, builds it (stopping any running engine first) and
rem launches it. Needs git, the .NET 8 SDK and Node.js, none of which belong on the workstation; it is not part
rem of the release bundle. On the workstation, update by downloading the new Halyard-<version>.zip from Releases.
rem Double-click this in Explorer; no terminal needed. The window stays open if anything fails.
setlocal
cd /d "%~dp0"
echo == Pulling the latest Halyard
git pull || goto :fail
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Build-Halyard.ps1" || goto :fail
call "%~dp0Launch-Halyard.bat" %*
endlocal
exit /b 0
:fail
echo.
echo Update failed. Read the message above, then press any key to close.
pause >nul
exit /b 1
