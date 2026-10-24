@echo off
rem Starts Cascade in the browser. Options: --console (keep this window), --no-arc, --arc, --no-browser, --stay (keep running after the tab closes), --replace
setlocal
powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Cascade.ps1" %*
endlocal
