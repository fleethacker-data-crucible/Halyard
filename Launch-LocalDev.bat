@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0" || exit /b 1
where powershell.exe >nul 2>&1 || (echo Windows PowerShell 5.1 is required. & exit /b 1)
echo [CASCADE LOCAL DEV] No ARC authentication at startup.
echo Cloud AI and Git.mil will still require legitimate credentials if selected.
powershell.exe -NoProfile -NoLogo -STA -File "%~dp0scripts\Start-CascadeWeb.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" pause
exit /b %RC%
