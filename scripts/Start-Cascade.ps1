#requires -Version 5.1
<#
.SYNOPSIS
  Starts Cascade: loads config\cascade.env, unlocks ARC in this console when the provider needs it,
  starts the local engine on 127.0.0.1 and opens the browser. Launch-Cascade.bat calls this.
.PARAMETER KeepConsole
  Leave this window visible (useful for diagnosing ARC or startup problems). Also: Launch-Cascade.bat --console
.PARAMETER NoArc
  Never load ARC, even for GitLab (local development with GITLAB_TOKEN in cascade.env).
.PARAMETER Stay
  Keep the engine running after the browser tab closes (by default it stops itself when the tab is gone).
  Also: Launch-Cascade.bat --stay, or CASCADE_AUTO_STOP=0 in cascade.env.
.PARAMETER Replace
  If an engine is already running, stop it and start a new one without asking. Also: --replace
#>
[CmdletBinding()]
param(
    [int]$Port = 0,
    [switch]$NoBrowser,
    [switch]$KeepConsole,
    [switch]$NoArc,
    [switch]$Arc,
    [switch]$Stay,
    [switch]$Replace,
    [string]$WebRoot,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
foreach ($a in @($Rest)) {
    switch -Regex ($a) {
        '^--?console$' { $KeepConsole = $true }
        '^--?no-arc$' { $NoArc = $true }
        '^--?arc$' { $Arc = $true }
        '^--?no-browser$' { $NoBrowser = $true }
        '^--?stay$' { $Stay = $true }
        '^--?replace$' { $Replace = $true }
        default { if ($a) { Write-Warning "Ignoring unknown option $a" } }
    }
}
Import-Module (Join-Path $PSScriptRoot '..\module\Cascade\Cascade.psd1') -Force
. (Join-Path $PSScriptRoot 'Cascade-Process.ps1')
$root = Split-Path $PSScriptRoot -Parent
$running = @(Get-CascadeProcesses -Root $root)
if ($running.Count -gt 0) {
    $where = ($running | ForEach-Object { if ($_.Url) { $_.Url + ' (PID ' + $_.Pid + ')' } else { 'PID ' + $_.Pid } }) -join ', '
    Write-Host ('Cascade is already running: ' + $where) -ForegroundColor Yellow
    $answer = if ($Replace) { 'y' } else { Read-Host 'Stop it and start a new session? [Y/n]' }
    if ($answer -eq '' -or $answer -match '^[Yy]') {
        foreach ($p in Stop-CascadeProcesses -Root $root) { Write-Host ('  stopped PID ' + $p) -ForegroundColor DarkGray }
    } else {
        Write-Host 'Leaving the running engine alone. Use "End session" in its browser tab, or scripts\Stop-Cascade.ps1.' -ForegroundColor DarkGray
        exit 0
    }
}
$useArc = if ($NoArc) { 'Never' } elseif ($Arc) { 'Always' } else { 'Auto' }
try {
    $params = @{ Port = $Port; NoBrowser = $NoBrowser; KeepConsole = $KeepConsole; UseArc = $useArc; AutoStop = (-not $Stay) }
    if ($WebRoot) { $params['WebRoot'] = $WebRoot }
    Start-Cascade @params
} catch {
    Write-Host ''
    Write-Host ('Cascade could not start: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'This window stays open so you can read the error. Press Enter to close.' -ForegroundColor DarkGray
    [void](Read-Host)
    exit 1
}
