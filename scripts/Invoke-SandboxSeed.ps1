#requires -Version 5.1
<#
.SYNOPSIS
  Seeds the sandbox repository with a synthetic Theme -> Epic -> Feature -> Story -> Task hierarchy.
.EXAMPLE
  .\scripts\Invoke-SandboxSeed.ps1 -Total 100 -RunId demo-1 -IncludeDefects
  About 80% of the items are complete; 20% carry one of 21 kinds of gap for the review workflow (-DefectRate changes the share).
.EXAMPLE
  .\scripts\Invoke-SandboxSeed.ps1 -CatalogPath fixtures\synthetic-work-items.json
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(5, 2000)][int]$Total = 100,
    [int]$Seed = 20261024,
    [string]$RunId,
    [switch]$IncludeDefects,
    [ValidateRange(0.0, 0.5)][double]$DefectRate = 0.2,
    [switch]$NoNativeLinks,
    [string]$CatalogPath,
    [switch]$NoArc
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\module\Halyard\Halyard.psd1') -Force
$p = @{ Total = $Total; Seed = $Seed; IncludeDefects = $IncludeDefects; DefectRate = $DefectRate; NoNativeLinks = $NoNativeLinks; UseArc = $(if ($NoArc) { 'Never' } else { 'Auto' }) }
if ($RunId) { $p['RunId'] = $RunId }
if ($CatalogPath) { $p['CatalogPath'] = $CatalogPath }
if ($WhatIfPreference) { $p['WhatIf'] = $true }
Invoke-HalyardSeed @p
