@echo off
setlocal EnableExtensions
set "REPO=%~dp0"
cd /d "%REPO%" || exit /b 1
where powershell.exe >nul 2>&1 || (echo Windows PowerShell 5.1 is required & pause & exit /b 1)
REM CAC/TOTP MUST be requested while the PowerShell console is visible.
REM Hide only after ARC authentication and browser startup; -ShowConsole is
REM selected through --console below. Process-only policy follows approved ARC.
if /I "%~1"=="--console" (
  powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%REPO%scripts\Start-CascadeWeb.ps1" -AuthenticateArc
) else (
  powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%REPO%scripts\Start-CascadeWeb.ps1" -AuthenticateArc -HideConsole
)
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" (
  echo.
  echo Cascade GUI failed (exit %EXITCODE%).
  echo Check logs\cascade-YYYYMMDD.jsonl and ensure no enforced IT policy is blocking execution.
  powershell.exe -NoProfile -Command "Get-ExecutionPolicy -List | Format-Table -AutoSize"
  pause
)
endlocal & exit /b %EXITCODE%
