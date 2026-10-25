#requires -Version 5.1
<#
.SYNOPSIS
  Permanently deletes synthetic work items from the sandbox repository.
.EXAMPLE
  .\scripts\Invoke-SandboxTeardown.ps1 -RunId demo-1
.EXAMPLE
  .\scripts\Invoke-SandboxTeardown.ps1 -AllSynthetic -DryRun
.EXAMPLE
  .\scripts\Invoke-SandboxTeardown.ps1 -Everything -ConfirmText 'DELETE fleethacker-data-crucible/Halyard-Ballast'
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][string]$RunId,
    [Parameter(Mandatory, ParameterSetName = 'All')][switch]$AllSynthetic,
    [Parameter(Mandatory, ParameterSetName = 'Everything')][switch]$Everything,
    [Parameter(ParameterSetName = 'Everything')][string]$ConfirmText,
    [switch]$DryRun,
    [Alias('NoArc')][switch]$NoLazarette   # -NoArc: former name, accepted for one release
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\module\Halyard\Halyard.psd1') -Force
$p = @{ DryRun = $DryRun; UseLazarette = $(if ($NoLazarette) { 'Never' } else { 'Auto' }) }
switch ($PSCmdlet.ParameterSetName) {
    'All' { $p['AllSynthetic'] = $true }
    'Everything' { $p['Everything'] = $true; $p['ConfirmText'] = $ConfirmText }
    default { $p['RunId'] = $RunId }
}
if ($WhatIfPreference) { $p['WhatIf'] = $true }
if ($PSBoundParameters.ContainsKey('Confirm')) { $p['Confirm'] = $PSBoundParameters['Confirm'] }
Invoke-HalyardTeardown @p
