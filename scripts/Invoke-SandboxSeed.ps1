#requires -Version 5.1
<#
.SYNOPSIS
  Seeds the sandbox repository with a synthetic Theme -> Epic -> Feature -> Story -> Task hierarchy.
.EXAMPLE
  .\scripts\Invoke-SandboxSeed.ps1 -Total 100 -RunId demo-1
.EXAMPLE
  .\scripts\Invoke-SandboxSeed.ps1 -CatalogPath fixtures\synthetic-work-items.json
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(5, 2000)][int]$Total = 100,
    [int]$Seed = 20261024,
    [string]$RunId,
    [switch]$IncludeDefects,
    [switch]$NoNativeLinks,
    [string]$CatalogPath,
    [switch]$NoArc
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\module\Cascade\Cascade.psd1') -Force
$p = @{ Total = $Total; Seed = $Seed; IncludeDefects = $IncludeDefects; NoNativeLinks = $NoNativeLinks; UseArc = $(if ($NoArc) { 'Never' } else { 'Auto' }) }
if ($RunId) { $p['RunId'] = $RunId }
if ($CatalogPath) { $p['CatalogPath'] = $CatalogPath }
if ($WhatIfPreference) { $p['WhatIf'] = $true }
Invoke-CascadeSeed @p
