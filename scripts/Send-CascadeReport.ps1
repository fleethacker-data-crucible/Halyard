#requires -Version 5.1
<#
.SYNOPSIS
  Former name of scripts\Send-HalyardReport.ps1 (Cascade is now Halyard). Forwards every argument.
.DESCRIPTION
  Kept because a weekly task registered before the rename runs this path. Re-register with
  Register-HalyardReportSchedule.ps1 to move the task to the new name.
#>
& (Join-Path $PSScriptRoot 'Send-HalyardReport.ps1') @args
