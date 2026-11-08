@echo off
rem Starts Halyard in its own window with a tray icon. A second launch brings the running window forward.
rem Options: --console (keep this window, for diagnosing startup), --no-lazarette, --lazarette, --no-browser,
rem --stay (keep running after the window closes), --replace (stop a running copy and start afresh)
setlocal
echo %* | find /i "--console" >nul
if not errorlevel 1 (
  powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Halyard.ps1" %*
  goto :eof
)
start "" powershell.exe -STA -NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Halyard.ps1" %*
endlocal
