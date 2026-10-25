#requires -Version 5.1
<#
.SYNOPSIS
  Former name of build\Build-Halyard.ps1 (Cascade is now Halyard). Forwards every argument.
.DESCRIPTION
  Kept so an existing checkout's Update-Cascade.bat, which calls this path, still builds after it pulls the rename.
#>
& (Join-Path $PSScriptRoot 'Build-Halyard.ps1') @args
exit $LASTEXITCODE
