@echo off
rem Pulls the latest Cascade from git, builds it (stopping anything that holds the DLL) and launches it.
rem Double-click this in Explorer; no terminal needed. The window stays open if anything fails.
setlocal
cd /d "%~dp0"
echo == Pulling the latest Cascade
git pull || goto :fail
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Build-Cascade.ps1" || goto :fail
call "%~dp0Launch-Cascade.bat" %*
endlocal
exit /b 0
:fail
echo.
echo Update failed. Read the message above, then press any key to close.
pause >nul
exit /b 1
