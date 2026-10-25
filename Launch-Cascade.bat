@echo off
rem Former name of Launch-Halyard.bat (Cascade is now Halyard). Kept so existing shortcuts and the old
rem Update-Cascade.bat keep working; it forwards every option.
call "%~dp0Launch-Halyard.bat" %*
