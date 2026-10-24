#requires -Version 5.1
<#
.SYNOPSIS
  Permanently deletes synthetic work items from the sandbox repository.
.EXAMPLE
  .\scripts\Invoke-SandboxTeardown.ps1 -RunId demo-1
.EXAMPLE
  .\scripts\Invoke-SandboxTeardown.ps1 -AllSynthetic -DryRun
.EXAMPLE
  .\scripts\Invoke-SandboxTeardown.ps1 -Everything -ConfirmText 'DELETE sun-of-anton/cascade-sandbox'
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][string]$RunId,
    [Parameter(Mandatory, ParameterSetName = 'All')][switch]$AllSynthetic,
    [Parameter(Mandatory, ParameterSetName = 'Everything')][switch]$Everything,
    [Parameter(ParameterSetName = 'Everything')][string]$ConfirmText,
    [switch]$DryRun,
    [switch]$NoArc
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\module\Cascade\Cascade.psd1') -Force
$p = @{ DryRun = $DryRun; UseArc = $(if ($NoArc) { 'Never' } else { 'Auto' }) }
switch ($PSCmdlet.ParameterSetName) {
    'All' { $p['AllSynthetic'] = $true }
    'Everything' { $p['Everything'] = $true; $p['ConfirmText'] = $ConfirmText }
    default { $p['RunId'] = $RunId }
}
if ($WhatIfPreference) { $p['WhatIf'] = $true }
if ($PSBoundParameters.ContainsKey('Confirm')) { $p['Confirm'] = $PSBoundParameters['Confirm'] }
Invoke-CascadeTeardown @p
