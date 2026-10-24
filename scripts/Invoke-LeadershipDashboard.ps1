<#
.SYNOPSIS
  Normalize Git work items and build the SMPG Leadership Dashboard HTML (Monday-ready).

.DESCRIPTION
  1. Load issues from fixture (offline) or pass -Live when GitLab token is configured.
  2. Normalize every item with Cascade meta + dashboard fields (points, blockers, age, owner-).
  3. Write normalized JSON + plan doc for the adapter.
  4. Build leadership metrics and export leadership_dashboard.html matching the reference board.

  Until velocity history and story points exist on live items, use -DemoBoard for the
  reference board the boss expects Monday; then switch to live-derived metrics.

.EXAMPLE
  .\scripts\Invoke-LeadershipDashboard.ps1 -DemoBoard
.EXAMPLE
  .\scripts\Invoke-LeadershipDashboard.ps1
.EXAMPLE
  .\scripts\Invoke-LeadershipDashboard.ps1 -Live
#>
[CmdletBinding()]
param(
    [switch]$Live,
    [switch]$DemoBoard,
    [string]$FixturePath,
    [string]$ConfigPath,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
# Agent capacity defaults (2-week sprint, ~55% commit)
try {
    Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
    $adv = Get-CascadePlanningCapacityAdvice -TeamSize 6
    Write-Host ("Capacity model: {0}d sprint, {1}% committed (~{2} person-days / team of {3})" -f $adv.SprintDays, $adv.CommittedPct, $adv.ApproxCommittedPersonDays, $adv.TeamSize) -ForegroundColor Cyan
} catch {}

$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'output' }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# --- Load issues ---
$issues = @()
if ($Live) {
    Write-Host "Loading issues from GitLab (live)..." -ForegroundColor Cyan
    $issues = @(Get-CascadeIssuesFromGitLab)
} else {
    if (-not $FixturePath) {
        $FixturePath = Join-Path $RepoRoot 'fixtures\work_items_fixture.json'
    }
    Write-Host "Loading fixture: $FixturePath" -ForegroundColor Cyan
    $issues = @(Get-CascadeIssuesFromFixture -Path $FixturePath)
}
Write-Host "Loaded $($issues.Count) issue(s)"

# --- Normalize (metadata + dashboard fields) ---
$normPath = Join-Path $OutDir 'normalized_work_items.json'
$normalized = @(Convert-CascadeGitLabIssuesToNormalized -Issues $issues -OutPath $normPath)
Write-Host ("Normalized {0} item(s)  |  with Cascade meta already: {1}  |  blockers flagged: {2}" -f `
        $normalized.Count, `
        @($normalized | Where-Object { $_.already_has_meta }).Count, `
        @($normalized | Where-Object { $_.is_blocker }).Count)

$planPath = Join-Path $OutDir 'normalize_plan.json'
Convert-CascadeNormalizedToPlanDoc -Normalized $normalized -OutPath $planPath | Out-Null

# --- Config (optional override) ---
$config = Get-CascadeDefaultLeadershipConfig
if ($ConfigPath -and (Test-Path -LiteralPath $ConfigPath)) {
    Write-Host "Loading leadership config: $ConfigPath"
    $raw = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    foreach ($p in $raw.PSObject.Properties.Name) {
        if ($config.PSObject.Properties.Name -contains $p) {
            $config.$p = $raw.$p
        }
    }
}

# --- Metrics + HTML ---
$metrics = Get-CascadeLeadershipMetrics -Issues $normalized -Config $config -UseDemoBoard:$DemoBoard
$htmlPath = Join-Path $OutDir 'leadership_dashboard.html'
Export-CascadeLeadershipHtml -Metrics $metrics -Path $htmlPath | Out-Null

# Also refresh the simple Monday snapshot from tree metrics when available
try {
    $treeIssues = foreach ($n in $normalized) {
        [pscustomobject]@{ iid = $n.iid; title = $n.title; description = $n.description; state = $n.state }
    }
    $treeMetrics = Get-CascadeMetrics -Issues $treeIssues
    Export-CascadeMondayHtml -Metrics $treeMetrics -Path (Join-Path $OutDir 'monday_dashboard.html') | Out-Null
    Export-CascadeTreeReport -Metrics $treeMetrics -Path (Join-Path $OutDir 'tree_report.txt') | Out-Null
} catch {
    Write-Warning "Tree/Monday snapshot skipped: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "=== Leadership board summary ===" -ForegroundColor Green
Write-Host ("  Features on track : {0}/{1}  (at risk {2}, parked {3})" -f `
        $metrics.features_on_track, $metrics.features_total, $metrics.features_at_risk, $metrics.features_parked)
Write-Host ("  Velocity (last 3) : {0}" -f ($metrics.velocity_history -join ' -> '))
Write-Host ("  Points remaining  : {0}{1}" -f $metrics.points_remaining, $(if ($metrics.points_is_story_count) { ' (story count; no points on items yet)' } else { '' }))
Write-Host ("  Capacity used     : {0}%  ({1}/{2} committed)" -f $metrics.capacity_used_pct, $metrics.committed_points, $metrics.capacity_points)
Write-Host ("  Open blockers     : {0}  avg age {1}d  (-7d: {2})" -f $metrics.blocker_count, $metrics.blocker_avg_age, $metrics.blocker_critical_count)
Write-Host ""
Write-Host "Outputs:" -ForegroundColor Cyan
Write-Host "  $normPath"
Write-Host "  $planPath"
Write-Host "  $htmlPath"
if ($DemoBoard) {
    Write-Host ""
    Write-Host "NOTE: -DemoBoard used reference rows from the attached dashboard image." -ForegroundColor Yellow
    Write-Host "      Re-run without -DemoBoard once Cascade meta + story points are on the items." -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Open leadership_dashboard.html in a browser for the Monday briefing."
