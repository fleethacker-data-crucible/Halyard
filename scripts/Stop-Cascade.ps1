#requires -Version 5.1
<#
.SYNOPSIS
  Former name of scripts\Stop-Halyard.ps1 (Cascade is now Halyard). Forwards every argument.
#>
& (Join-Path $PSScriptRoot 'Stop-Halyard.ps1') @args
