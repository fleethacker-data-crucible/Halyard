@echo off
rem Starts Halyard in the browser. Options: --console (keep this window), --no-lazarette, --lazarette, --no-browser, --stay (keep running after the tab closes), --replace
setlocal
powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Halyard.ps1" %*
endlocal
