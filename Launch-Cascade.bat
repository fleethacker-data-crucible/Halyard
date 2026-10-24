@echo off
rem Starts Cascade in the browser. Options: --console (keep this window), --no-arc, --arc, --no-browser
setlocal
powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Cascade.ps1" %*
endlocal
