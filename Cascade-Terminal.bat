@echo off
setlocal EnableExtensions
set "REPO=%~dp0"
cd /d "%REPO%" || exit /b 1
where powershell.exe >nul 2>&1 || (echo Windows PowerShell 5.1 is required & pause & exit /b 1)
REM Match the known-working ARC startup: STA, NoProfile, and process-only policy.
REM This does not override Group Policy, AppLocker or WDAC; use approved scripts.
powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%REPO%scripts\Start-CascadeHost.ps1"
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" (
  echo.
  echo Cascade Terminal failed (exit %EXITCODE%).
  echo If Windows reports running scripts is disabled, inspect effective policy below.
  echo MachinePolicy or UserPolicy restrictions require your IT team's approval.
  powershell.exe -NoProfile -Command "Get-ExecutionPolicy -List | Format-Table -AutoSize"
  pause
)
endlocal & exit /b %EXITCODE%
