<#
.SYNOPSIS
  Offline experiment: load fixture issues, show unparsed count, write tree + monday HTML.
#>
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$fixture = Join-Path $RepoRoot 'fixtures\work_items_fixture.json'
$issues = Get-CascadeIssuesFromFixture -Path $fixture
Write-Host "Loaded $($issues.Count) issues from fixture"

$metrics = Get-CascadeMetrics -Issues $issues
Write-Host "Parsed with Cascade meta: $($metrics.ParsedCount)"
Write-Host "Unparsed (need migration): $($metrics.UnparsedCount)"

Export-CascadeTreeReport -Metrics $metrics -Path (Join-Path $RepoRoot 'output\tree_report.txt') | Out-Null
Export-CascadeMondayHtml -Metrics $metrics -Path (Join-Path $RepoRoot 'output\monday_dashboard.html') | Out-Null

Write-Host ""
Write-Host "Next: run Invoke-LocalMigrateDryRun.ps1 to generate a migration plan CSV."
