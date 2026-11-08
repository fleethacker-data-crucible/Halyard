#requires -Version 5.1
<#
.SYNOPSIS
  Starts Halyard: loads config\halyard.env, unlocks Lazarette in this console when the provider needs it,
  starts the local engine on 127.0.0.1 and opens it in its own app window with a tray icon. Launch-Halyard.bat
  calls this hidden; a second launch brings the running copy's window forward instead of starting another.
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
    [switch]$NoLazarette,
    [switch]$Lazarette,
    [switch]$Stay,
    [switch]$Replace,
    [string]$WebRoot,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
foreach ($a in @($Rest)) {
    switch -Regex ($a) {
        '^--?console$' { $KeepConsole = $true }
        '^--?no-lazarette$' { $NoLazarette = $true }
        '^--?lazarette$' { $Lazarette = $true }
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
    # Already running: bring its window forward (it opens a new one if the old one was closed). --replace starts afresh.
    if (-not $Replace -and (Send-HalyardOpenSignal -Root $root)) { exit 0 }
    $where = ($running | ForEach-Object { if ($_.Url) { $_.Url + ' (PID ' + $_.Pid + ')' } else { 'PID ' + $_.Pid } }) -join ', '
    $answer = if ($Replace) { 'Yes' } else { Show-HalyardMessage -Kind Question -Text ("Halyard is already running ($where), but it did not answer.`n`nStop it and start a new session?") }
    if ($answer -eq 'Yes') {
        foreach ($p in Stop-HalyardProcesses -Root $root) { Write-Host ('  stopped PID ' + $p) -ForegroundColor DarkGray }
    } else { exit 0 }
}
$useVault = if ($NoLazarette) { 'Never' } elseif ($Lazarette) { 'Always' } else { 'Auto' }
try {
    $params = @{ Port = $Port; NoBrowser = $NoBrowser; KeepConsole = $KeepConsole; UseLazarette = $useVault; AutoStop = (-not $Stay) }
    if ($WebRoot) { $params['WebRoot'] = $WebRoot }
    Start-Halyard @params
} catch {
    # The launcher runs hidden, so the error goes to a message box (and to the console for --console runs).
    Write-Host ('Halyard could not start: ' + $_.Exception.Message) -ForegroundColor Red
    [void](Show-HalyardMessage -Kind Error -Text ("Halyard could not start:`n`n" + $_.Exception.Message + "`n`nRun Launch-Halyard.bat --console to see the full startup log."))
    exit 1
}
