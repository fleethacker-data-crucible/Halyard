#requires -Version 5.1
<#
.SYNOPSIS
  Stops every running Halyard engine on this machine.
.DESCRIPTION
  The engine normally stops itself when its browser tab closes, or from "End session" in the browser.
  This script is the fallback: it reads the runtime files the engine writes under logs\ (one per
  process) and stops those processes, then looks for any PowerShell still running Start-Halyard.ps1.
  Build-Halyard.ps1 calls it before building so the DLL is never locked.
.PARAMETER Quiet
  Print nothing unless something was stopped.
.PARAMETER All
  Also stop every other PowerShell window that has Halyard.Core.dll loaded (seed, teardown, report or
  test runs left open), which is what blocks a rebuild.
.EXAMPLE
  .\scripts\Stop-Halyard.ps1
#>
[CmdletBinding()]
param([switch]$Quiet, [switch]$All)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Halyard-Process.ps1')
$stopped = Stop-HalyardProcesses -Root $root -IncludeHolders:$All
if ($stopped.Count -gt 0) {
    foreach ($p in $stopped) { Write-Host ('Stopped PID ' + $p + '.') -ForegroundColor Yellow }
} elseif (-not $Quiet) {
    Write-Host 'No Halyard engine is running.' -ForegroundColor DarkGray
}
