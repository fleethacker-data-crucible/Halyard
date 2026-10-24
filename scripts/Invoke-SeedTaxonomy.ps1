<#
.SYNOPSIS
  Seed active Themes + Cascade Epic into sandbox (append-safe; skips existing titles).
#>
param(
    [switch]$Force,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
foreach ($extra in @('WritePolicy.ps1','Taxonomy.ps1','Logging.ps1')) {
    $p = Join-Path $RepoRoot "src\Cascade\$extra"
    if (Test-Path $p) { . $p }
}
$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^=#]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"')
        }
    }
}

Write-Host '=== Cascade Seed Taxonomy (Themes + Epic) ===' -ForegroundColor Cyan
$ctx = Write-CascadeWriteBanner
$null = Assert-CascadeWriteTarget -ProjectId $ctx.WriteProjectId
$tax = Get-CascadeTaxonomy
$defaults = Get-CascadeAgentDefaults

Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
$url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$tok = Get-CascadeGitLabToken -AllowPrompt
$headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }
$base = "$url/api/v4/projects/$($ctx.WriteProjectId)"

# Existing titles
$existing = @{}
try {
    $page = 1
    do {
        $batch = Invoke-RestMethod -Uri "$base/issues?state=opened&per_page=100&page=$page" -Headers $headers -TimeoutSec 60
        foreach ($i in @($batch)) { $existing[$i.title] = $i }
        $page++
    } while (@($batch).Count -eq 100)
} catch {
    Write-Host "Warn: could not list issues: $($_.Exception.Message)" -ForegroundColor Yellow
}

function New-SeedIssue([string]$Title, [string]$Description, [string[]]$Labels) {
    if ($existing.ContainsKey($Title)) {
        Write-Host ("  SKIP exists: {0} (!{1})" -f $Title, $existing[$Title].iid) -ForegroundColor DarkGray
        return $existing[$Title]
    }
    if ($DryRun) {
        Write-Host ("  DRYRUN would create: {0}" -f $Title) -ForegroundColor Yellow
        return $null
    }
    if (-not $Force) {
        # auto-create in sandbox without YES spam when seeding
    }
    $body = @{
        title       = $Title
        description = $Description
        labels      = ($Labels -join ',')
    } | ConvertTo-Json -Depth 5
    $created = Invoke-RestMethod -Method Post -Uri "$base/issues" -Headers $headers `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
    Write-Host ("  CREATED !{0} {1}" -f $created.iid, $Title) -ForegroundColor Green
    $existing[$Title] = $created
    return $created
}

Write-Host ("Sprint defaults: {0}d, commit {1}%" -f $defaults.sprint.length_days, $defaults.capacity.default_committed_pct) -ForegroundColor DarkGray
Write-Host 'Seeding Themes...' -ForegroundColor Cyan

$themeIssues = @{}
foreach ($th in @($tax.themes)) {
    $meta = New-CascadeMetaBlockFromTaxonomy -Type THEME -Id $th.id -State open -Status $th.status `
        -Area $th.area -Extra @{ window = 'ongoing'; source = 'taxonomy-seed' }
    $desc = @"
$meta

## Purpose
$($th.purpose)

## In scope
Work that belongs in this portfolio lane.

## Out of scope
Work that belongs under another active Theme.

## Status
$($th.status) (Themes are Active or Sunset only; window is ongoing)

## Child items
"@
    $labs = @('cascade-test', 'cascade', 'type::theme', "theme::$($th.id -replace '^THEME-','')", "area::$($th.area)")
    $issue = New-SeedIssue -Title $th.title -Description $desc -Labels $labs
    if ($issue) { $themeIssues[$th.id] = $issue }
}

Write-Host 'Seeding seed Epics...' -ForegroundColor Cyan
foreach ($ep in @($tax.seed_epics)) {
    $meta = New-CascadeMetaBlockFromTaxonomy -Type EPIC -Id $ep.id -Theme $ep.theme -State open -Area 'cascade' `
        -Extra @{ source = 'taxonomy-seed' }
    $sp = ($ep.success_picture | ForEach-Object { "- $_" }) -join "`n"
    $desc = @"
$meta

## Outcome
$($ep.outcome)

## Success picture
$sp

## Child items

## Related milestones
- Cascade MVP (Sandbox)
"@
    $labs = @('cascade-test', 'cascade', 'type::epic', 'theme::process-improvement', 'area::cascade')
    $created = New-SeedIssue -Title $ep.title -Description $desc -Labels $labs
    # append-only link on Theme
    if ($created -and $themeIssues.ContainsKey($ep.theme)) {
        $parent = $themeIssues[$ep.theme]
        $newDesc = Add-CascadeChildLinkAppendOnly -ParentDescription $parent.description `
            -ChildTitle $ep.title -ChildIid $created.iid -SectionHeader '## Child items'
        if (-not $DryRun -and $newDesc -ne $parent.description) {
            $payload = @{ description = $newDesc } | ConvertTo-Json -Depth 3
            $null = Invoke-RestMethod -Method Put -Uri "$base/issues/$($parent.iid)" -Headers $headers `
                -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
            Write-Host ("  Appended child link on Theme !{0}" -f $parent.iid) -ForegroundColor Cyan
        }
    }
}

Write-Host 'Seed complete.' -ForegroundColor Green
Write-Host 'Next: [I] for Features/Stories under the Cascade Epic; assign sprint on Stories.'
