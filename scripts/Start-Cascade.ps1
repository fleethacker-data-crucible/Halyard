#requires -Version 5.1
<#
.SYNOPSIS
  Former name of scripts\Start-Halyard.ps1 (Cascade is now Halyard). Forwards every argument.
#>
& (Join-Path $PSScriptRoot 'Start-Halyard.ps1') @args
