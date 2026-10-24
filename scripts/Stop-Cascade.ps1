#requires -Version 5.1
<#
.SYNOPSIS
  Stops every running Cascade engine on this machine.
.DESCRIPTION
  The engine normally stops itself when its browser tab closes, or from "End session" in the browser.
  This script is the fallback: it reads the runtime files the engine writes under logs\ (one per
  process) and stops those processes, then looks for any PowerShell still running Start-Cascade.ps1.
  Build-Cascade.ps1 calls it before building so the DLL is never locked.
.PARAMETER Quiet
  Print nothing unless something was stopped.
.EXAMPLE
  .\scripts\Stop-Cascade.ps1
#>
[CmdletBinding()]
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Cascade-Process.ps1')
$stopped = Stop-CascadeProcesses -Root $root
if ($stopped.Count -gt 0) {
    foreach ($p in $stopped) { Write-Host ('Stopped Cascade engine (PID ' + $p + ').') -ForegroundColor Yellow }
} elseif (-not $Quiet) {
    Write-Host 'No Cascade engine is running.' -ForegroundColor DarkGray
}
