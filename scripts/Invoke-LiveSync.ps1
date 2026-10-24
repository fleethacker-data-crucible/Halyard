<#
.SYNOPSIS
  Live: pull issues from web.git.mil and produce tree + monday HTML.
  Default label filter: area::data-crucible (override with GITLAB_LABELS).
#>
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or $line -eq '') { return }
        if ($line -match '^\s*([^=]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"').Trim("'")
        }
    }
}

Write-Host 'Testing GitLab before full pull...' -ForegroundColor Cyan
$probe = Test-CascadeGitLabConnection -SampleIssues 3
if (-not $probe.Ok) {
    Write-Host 'Fix connection (menu [G] or config\cascade.env) then retry.' -ForegroundColor Yellow
    exit 1
}

$labels = if ($env:GITLAB_LABELS) { $env:GITLAB_LABELS } else { 'area::data-crucible' }
Write-Host ("Label filter: {0}" -f $(if ($labels) { $labels } else { '(none)' })) -ForegroundColor Cyan
$rawIssues = Get-CascadeIssuesFromGitLab -State all -Labels $labels
$issues = New-Object System.Collections.Generic.List[object]
if ($rawIssues -is [System.Array]) {
    foreach ($x in $rawIssues) { if ($null -ne $x) { [void]$issues.Add($x) } }
} elseif ($null -ne $rawIssues) {
    [void]$issues.Add($rawIssues)
}
$issues = $issues.ToArray()
Write-Host ("Normalized list count: {0}" -f $issues.Count) -ForegroundColor DarkGray

if ($issues.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($labels)) {
    Write-Host ''
    Write-Host "No issues matched labels='$labels'." -ForegroundColor Yellow
    Write-Host 'Scouting labels on recent issues in this project (no filter)...' -ForegroundColor Cyan
    $scout = @(Get-CascadeIssuesFromGitLab -State all -Labels '' -PerPage 100)
    # only first page worth - Get already pages; limit display
    $labelCounts = @{}
    $n = 0
    foreach ($iss in $scout) {
        $n++
        $labs = @()
        try {
            if ($iss.labels -is [System.Array]) { $labs = @($iss.labels | ForEach-Object { "$_" }) }
            elseif ($iss.labels) { $labs = @([string]$iss.labels) }
        } catch { }
        foreach ($lb in $labs) {
            if ([string]::IsNullOrWhiteSpace($lb)) { continue }
            if (-not $labelCounts.ContainsKey($lb)) { $labelCounts[$lb] = 0 }
            $labelCounts[$lb]++
        }
    }
    Write-Host ("Scouted {0} issue(s). Distinct labels:" -f $n) -ForegroundColor Cyan
    if ($labelCounts.Count -eq 0) {
        Write-Host '  (no labels found on those issues)' -ForegroundColor DarkGray
    } else {
        $labelCounts.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
            Write-Host ("  {0,4}  {1}" -f $_.Value, $_.Key)
        }
    }
    Write-Host ''
    Write-Host 'Set GITLAB_LABELS in config\cascade.env to the exact label name (case-sensitive),' -ForegroundColor Yellow
    Write-Host 'or GITLAB_LABELS= for all issues. Then run [5] again.' -ForegroundColor Yellow
    exit 1
}

if ($issues.Count -eq 0) {
    Write-Host 'No issues returned (empty project or filter). Nothing to export.' -ForegroundColor Yellow
    exit 1
}

$metrics = Get-CascadeMetrics -Issues $issues
$out = Join-Path $RepoRoot 'output'
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }
Export-CascadeTreeReport -Metrics $metrics -Path (Join-Path $out 'tree_report_live.txt')
Export-CascadeMondayHtml -Metrics $metrics -Path (Join-Path $out 'monday_dashboard_live.html')
$metrics | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out 'metrics_live.json')
# also save raw issue list for offline work
$issues | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out 'issues_live.json')
Write-Host ("Live sync complete. Issues: {0}" -f $issues.Count) -ForegroundColor Green
