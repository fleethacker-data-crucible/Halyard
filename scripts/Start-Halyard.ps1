#requires -Version 5.1
<#
.SYNOPSIS
  Starts Halyard: loads config\halyard.env, unlocks Lazarette in this console when the provider needs it,
  starts the local engine on 127.0.0.1 and opens the browser. Launch-Halyard.bat calls this.
.PARAMETER KeepConsole
  Leave this window visible (useful for diagnosing Lazarette or startup problems). Also: Launch-Halyard.bat --console
.PARAMETER NoLazarette
  Never load Lazarette, even for GitLab (local development with GITLAB_TOKEN in halyard.env).
.PARAMETER Stay
  Keep the engine running after the browser tab closes (by default it stops itself when the tab is gone).
  Also: Launch-Halyard.bat --stay, or HALYARD_AUTO_STOP=0 in halyard.env.
.PARAMETER Replace
  If an engine is already running, stop it and start a new one without asking. Also: --replace
#>
[CmdletBinding()]
param(
    [int]$Port = 0,
    [switch]$NoBrowser,
    [switch]$KeepConsole,
    [Alias('NoArc')][switch]$NoLazarette,   # -NoArc / -Arc: former names, accepted for one release
    [Alias('Arc')][switch]$Lazarette,
    [switch]$Stay,
    [switch]$Replace,
    [string]$WebRoot,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
foreach ($a in @($Rest)) {
    switch -Regex ($a) {
        '^--?console$' { $KeepConsole = $true }
        '^--?no-(lazarette|arc)$' { $NoLazarette = $true }   # --no-arc / --arc: former flags (old shortcuts), one release
        '^--?(lazarette|arc)$' { $Lazarette = $true }
        '^--?no-browser$' { $NoBrowser = $true }
        '^--?stay$' { $Stay = $true }
        '^--?replace$' { $Replace = $true }
        default { if ($a) { Write-Warning "Ignoring unknown option $a" } }
    }
}
Import-Module (Join-Path $PSScriptRoot '..\module\Halyard\Halyard.psd1') -Force
. (Join-Path $PSScriptRoot 'Halyard-Process.ps1')
$root = Split-Path $PSScriptRoot -Parent
$running = @(Get-HalyardProcesses -Root $root)
if ($running.Count -gt 0) {
    $where = ($running | ForEach-Object { if ($_.Url) { $_.Url + ' (PID ' + $_.Pid + ')' } else { 'PID ' + $_.Pid } }) -join ', '
    Write-Host ('Halyard is already running: ' + $where) -ForegroundColor Yellow
    $answer = if ($Replace) { 'y' } else { Read-Host 'Stop it and start a new session? [Y/n]' }
    if ($answer -eq '' -or $answer -match '^[Yy]') {
        foreach ($p in Stop-HalyardProcesses -Root $root) { Write-Host ('  stopped PID ' + $p) -ForegroundColor DarkGray }
    } else {
        Write-Host 'Leaving the running engine alone. Use "End session" in its browser tab, or scripts\Stop-Halyard.ps1.' -ForegroundColor DarkGray
        exit 0
    }
}
$useVault = if ($NoLazarette) { 'Never' } elseif ($Lazarette) { 'Always' } else { 'Auto' }
try {
    $params = @{ Port = $Port; NoBrowser = $NoBrowser; KeepConsole = $KeepConsole; UseLazarette = $useVault; AutoStop = (-not $Stay) }
    if ($WebRoot) { $params['WebRoot'] = $WebRoot }
    Start-Halyard @params
} catch {
    Write-Host ''
    Write-Host ('Halyard could not start: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'This window stays open so you can read the error. Press Enter to close.' -ForegroundColor DarkGray
    [void](Read-Host)
    exit 1
}
