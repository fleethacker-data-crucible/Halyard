#requires -Version 5.1
<#
.SYNOPSIS
  Starts Cascade: loads config\cascade.env, unlocks ARC in this console when the provider needs it,
  starts the local engine on 127.0.0.1 and opens the browser. Launch-Cascade.bat calls this.
.PARAMETER KeepConsole
  Leave this window visible (useful for diagnosing ARC or startup problems). Also: Launch-Cascade.bat --console
.PARAMETER NoArc
  Never load ARC, even for GitLab (local development with GITLAB_TOKEN in cascade.env).
#>
[CmdletBinding()]
param(
    [int]$Port = 0,
    [switch]$NoBrowser,
    [switch]$KeepConsole,
    [switch]$NoArc,
    [switch]$Arc,
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
        default { if ($a) { Write-Warning "Ignoring unknown option $a" } }
    }
}
Import-Module (Join-Path $PSScriptRoot '..\module\Cascade\Cascade.psd1') -Force
$useArc = if ($NoArc) { 'Never' } elseif ($Arc) { 'Always' } else { 'Auto' }
try {
    $params = @{ Port = $Port; NoBrowser = $NoBrowser; KeepConsole = $KeepConsole; UseArc = $useArc }
    if ($WebRoot) { $params['WebRoot'] = $WebRoot }
    Start-Cascade @params
} catch {
    Write-Host ''
    Write-Host ('Cascade could not start: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'This window stays open so you can read the error. Press Enter to close.' -ForegroundColor DarkGray
    [void](Read-Host)
    exit 1
}
