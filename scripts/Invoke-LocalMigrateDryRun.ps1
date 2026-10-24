<#
.SYNOPSIS
  Offline: propose Cascade titles/types/metadata for all fixture issues - output/migrate_plan.csv
#>
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$fixture = Join-Path $RepoRoot 'fixtures\work_items_fixture.json'
$mapping = Join-Path $RepoRoot 'fixtures\mapping_sample.csv'
$out = Join-Path $RepoRoot 'output\migrate_plan.csv'

$plan = Invoke-CascadeMigrateDryRun -FixturePath $fixture -MappingCsv $mapping -OutPath $out
Write-Host ""
Write-Host "Sample of plan:"
$plan | Select-Object -First 8 Iid, ProposedType, ProposedTitle, AlreadyHasMeta | Format-Table -AutoSize
Write-Host "Edit $out (and mapping_sample.csv) then re-run. Apply against live Git only with Invoke-CascadeMigrateApply when ready."
