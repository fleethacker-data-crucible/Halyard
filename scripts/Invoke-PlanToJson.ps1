<#
.SYNOPSIS
  Convert output\migrate_plan.csv -> output\migrate_plan.json and build parent package JSON.
#>
param(
    [string]$PlanCsv,
    [switch]$BuildPackage
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

if (-not $PlanCsv) { $PlanCsv = Join-Path $RepoRoot 'output\migrate_plan.csv' }
if (-not (Test-Path $PlanCsv)) {
    Write-Host "No plan CSV at $PlanCsv - run migrate dry-run first (menu 2)." -ForegroundColor Yellow
    exit 1
}

$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$jsonPath = Join-Path $outDir 'migrate_plan.json'
$doc = Convert-CascadePlanCsvToJson -PlanCsv $PlanCsv -OutPath $jsonPath

Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
$byType = $doc.items | Group-Object proposed_type
foreach ($g in $byType) { Write-Host ("  {0,4}  {1}" -f $g.Count, $g.Name) }
$orphans = @($doc.items | Where-Object { [string]::IsNullOrWhiteSpace($_.proposed_parent) }).Count
Write-Host ("  {0,4}  without parent" -f $orphans)

if ($BuildPackage) {
    $pkgPath = Join-Path $outDir 'parent_package.json'
    $null = New-CascadeParentPackage -PlanDoc $doc -OutPath $pkgPath
    Write-Host "Parent package ready for LLM." -ForegroundColor Green
}

Write-Host ""
Write-Host "JSON paths:" -ForegroundColor Green
Write-Host "  $jsonPath"
if ($BuildPackage) { Write-Host "  $(Join-Path $outDir 'parent_package.json')" }
