<#
.SYNOPSIS
  READ-ONLY gap report for normalize work queue (Central Business Repo first).

  Flags missing: Cascade meta, parent, AC, PfW/DWI, points/effort, blockers.

  Writes:
    output\gap_report_data_crucible.csv
    output\gap_report_summary.json
    output\gap_report_steps.log
#>
param(
    [string]$Labels = '',
    [string]$State = 'all',
    [int]$MaxIssues = 0
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

function Write-GapStep {
    param([string]$Msg, [string]$Level = 'INFO')
    $line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'HH:mm:ss'), $Level, $Msg
    $c = switch ($Level) { 'ERROR' { 'Red' } 'WARN' { 'Yellow' } 'OK' { 'Green' } default { 'DarkGray' } }
    Write-Host $line -ForegroundColor $c
    try {
        $p = Join-Path $RepoRoot 'output\gap_report_steps.log'
        $d = Split-Path $p -Parent
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
        Add-Content $p $line -Encoding UTF8
    } catch {}
}

# Load cascade.env
$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path -LiteralPath $envFile) {
    Get-Content -LiteralPath $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or $line -eq '') { return }
        if ($line -match '^\s*([^=]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"').Trim("'")
        }
    }
    Write-GapStep 'Loaded cascade.env' 'OK'
} else {
    Write-GapStep 'cascade.env not found' 'WARN'
}

if ([string]::IsNullOrWhiteSpace($env:GITLAB_URL)) { $env:GITLAB_URL = 'https://web.git.mil' }
# Gap report ALWAYS reads real backlog (CBR), never the empty sandbox.
# Known ids: CBR / data-crucible = 16629; cascade-test-sandbox = 52999
$cbrDefault = '16629'
$sandboxKnown = @('52999')

$writeId = if ($env:GITLAB_PROJECT_ID) { $env:GITLAB_PROJECT_ID.Trim() } else { '' }
$sourceId = if ($env:GITLAB_SOURCE_PROJECT_ID) { $env:GITLAB_SOURCE_PROJECT_ID.Trim() } else { '' }

$readProjectId = $cbrDefault
if ($sourceId -and $sourceId -notin $sandboxKnown -and $sourceId -ne $writeId) {
    $readProjectId = $sourceId
} elseif ($sourceId -and $sourceId -notin $sandboxKnown) {
    $readProjectId = $sourceId
} elseif ($writeId -eq $cbrDefault) {
    $readProjectId = $cbrDefault
}

# Never read sandbox
if ($readProjectId -in $sandboxKnown) {
    Write-GapStep ("Refusing sandbox project {0} for gap READ - using CBR {1}" -f $readProjectId, $cbrDefault) 'WARN'
    $readProjectId = $cbrDefault
}
if ($sourceId -in $sandboxKnown) {
    Write-Host ("NOTE: GITLAB_SOURCE_PROJECT_ID={0} is the sandbox. Fix cascade.env to 16629." -f $sourceId) -ForegroundColor Yellow
}

# This script only READs - point API at CBR without clobbering write target in file permanently
$savedWrite = $env:GITLAB_PROJECT_ID
$env:GITLAB_PROJECT_ID = $readProjectId

if (-not $Labels) {
    $Labels = if ($env:GITLAB_LABELS) { $env:GITLAB_LABELS } else { 'area::data-crucible' }
}

Write-Host '=== Cascade GAP REPORT (read-only) ===' -ForegroundColor Cyan
Write-Host 'No GitLab writes. Use CSV as normalize work queue.' -ForegroundColor Green
Write-Host ("READ backlog project={0}  |  WRITE sandbox (env)={1}" -f $readProjectId, $(if ($savedWrite) { $savedWrite } else { '(unset)' })) -ForegroundColor Yellow
Write-GapStep ("project={0} labels={1} state={2}" -f $readProjectId, $Labels, $State)

$null = Get-CascadeGitLabToken -AllowPrompt
Write-GapStep 'Token OK' 'OK'

Write-GapStep 'Pulling issues...'
$raw = Get-CascadeIssuesFromGitLab -State $State -Labels $Labels
$all = New-Object System.Collections.Generic.List[object]
if ($raw -is [System.Array]) { foreach ($x in $raw) { if ($null -ne $x) { [void]$all.Add($x) } } }
elseif ($null -ne $raw) { [void]$all.Add($raw) }

if ($MaxIssues -gt 0 -and $all.Count -gt $MaxIssues) {
    $all = [System.Collections.Generic.List[object]]@($all | Select-Object -First $MaxIssues)
}
Write-GapStep ("Fetched {0} issue(s)" -f $all.Count) 'OK'

function Test-HasPfwOrDwi {
    param([hashtable]$Meta, [string]$Body, [string]$Title)
    if ($Meta) {
        foreach ($k in @('pfw_id','pfw_iid','dwi_id','dwi_iid','proposal_id','proposal_iid','dwi_ref','artifact_level')) {
            if ($Meta[$k] -and [string]$Meta[$k] -notmatch '^\s*$') {
                if ($k -eq 'artifact_level' -and [string]$Meta[$k] -match '(?i)lightweight') { return $true }
                if ($k -ne 'artifact_level') { return $true }
            }
        }
    }
    $b = "$Body $Title"
    if ($b -match '(?i)\b(pfw|dwi|proposal\s+for\s+work|defined\s+work\s+instruction|statement\s+of\s+work)\b') { return $true }
    if ($b -match '(?i)fixtures/proposals/') { return $true }
    return $false
}

function Test-HasPoints {
    param([hashtable]$Meta, [string]$Body)
    if ($Meta) {
        foreach ($k in @('points','story_points','effort_band','estimate_hours')) {
            if ($Meta[$k] -and [string]$Meta[$k] -notmatch '^\s*$') { return $true }
        }
    }
    if ($Body -match '(?i)(story\s*points?|effort\s*band|estimate[d]?\s*hours?)\s*[:=]') { return $true }
    return $false
}

$rows = New-Object System.Collections.Generic.List[object]
$counts = @{
    total = 0
    open = 0
    closed = 0
    cascade_dup = 0
    missing_meta = 0
    missing_parent = 0
    missing_ac = 0
    missing_pfw = 0
    missing_points = 0
    has_blocker = 0
    story = 0
    feature = 0
    epic = 0
    other = 0
}

foreach ($iss in $all) {
    $counts.total++
    $iid = 0
    try { $iid = [int]$iss.iid } catch { }
    $title = [string]$iss.title
    $state = [string]$iss.state
    $desc = [string]$iss.description
    $labs = @()
    try { if ($iss.labels -is [System.Array]) { $labs = @($iss.labels | ForEach-Object { "$_" }) } } catch {}

    if ($state -eq 'opened' -or $state -eq 'open') { $counts.open++ } else { $counts.closed++ }

    $isDup = ($title -match '\[CASCADE-DUP\]') -or ($labs -contains 'cascade-test')
    if ($isDup) { $counts.cascade_dup++ }

    $meta = $null
    try { $meta = Get-CascadeMeta -Description $desc } catch { $meta = $null }

    $inferred = 'STORY'
    try {
        if (Get-Command Get-CascadeInferredTypeFromIssue -ErrorAction SilentlyContinue) {
            $inferred = Get-CascadeInferredTypeFromIssue -Issue $iss
        }
    } catch {}
    if ($meta -and $meta['type']) { $inferred = ([string]$meta['type']).ToUpper() }

    switch ($inferred) {
        'EPIC' { $counts.epic++ }
        'FEATURE' { $counts.feature++ }
        'STORY' { $counts.story++ }
        'TASK' { $counts.story++ }
        default { $counts.other++ }
    }

    $defs = @()
    try {
        $defs = @(Get-CascadeWorkItemDeficiencies -Meta $meta -Body $desc -Title $title -InferredType $inferred | ForEach-Object { "$_" })
    } catch {}

    $hasMeta = [bool]($meta -and (Test-CascadeMeta -Meta $meta -ErrorAction SilentlyContinue))
    if (-not $hasMeta) { $counts.missing_meta++ }

    $parent = ''
    if ($meta -and $meta['parent']) { $parent = [string]$meta['parent'] }
    $hasParent = -not [string]::IsNullOrWhiteSpace($parent)
    if (-not $hasParent -and $inferred -match 'STORY|TASK|FEATURE') { $counts.missing_parent++ }

    $hasAc = $false
    try { $hasAc = Test-CascadeBodyHasAcceptanceCriteria -Body $desc } catch {
        $hasAc = ($desc -match '(?im)acceptance\s*criteria')
    }
    if ($inferred -match 'STORY|TASK' -and -not $hasAc) { $counts.missing_ac++ }

    $hasPfw = Test-HasPfwOrDwi -Meta $meta -Body $desc -Title $title
    if ($inferred -eq 'FEATURE' -and -not $hasPfw) { $counts.missing_pfw++ }
    # Also count Stories with no PfW context as soft gap for report column only

    $hasPts = Test-HasPoints -Meta $meta -Body $desc
    if ($inferred -match 'STORY|TASK' -and -not $hasPts) { $counts.missing_points++ }

    $blocked = $false
    try {
        if (Get-Command Test-CascadeIsBlocker -ErrorAction SilentlyContinue) {
            $blocked = Test-CascadeIsBlocker -State $(if ($meta -and $meta['state']) { $meta['state'] } else { $state }) -Labels $labs -Title $title -Meta $meta
        }
    } catch {
        $blocked = ($labs -join ' ') -match '(?i)blocker|blocked'
    }
    if ($blocked) { $counts.has_blocker++ }

    $web = ''
    try { $web = [string]$iss.web_url } catch {}

    $priority = 0
    if (-not $hasParent) { $priority += 40 }
    if ($inferred -match 'STORY|TASK' -and -not $hasAc) { $priority += 30 }
    if ($inferred -eq 'FEATURE' -and -not $hasPfw) { $priority += 25 }
    if (-not $hasMeta) { $priority += 15 }
    if ($inferred -match 'STORY|TASK' -and -not $hasPts) { $priority += 10 }
    if ($isDup) { $priority = [Math]::Max(0, $priority - 50) }

    $row = [pscustomobject]@{
        iid              = $iid
        title            = $title
        state            = $state
        inferred_type    = $inferred
        is_cascade_dup   = $isDup
        has_cascade_meta = $hasMeta
        parent           = $parent
        has_parent       = $hasParent
        has_acceptance   = $hasAc
        has_pfw_or_dwi   = $hasPfw
        has_points       = $hasPts
        is_blocker       = $blocked
        deficiencies     = ($defs -join ';')
        labels           = ($labs -join ';')
        web_url          = $web
        normalize_priority = $priority
        suggested_next   = $(
            if ($isDup) { 'skip_or_delete_test_dup' }
            elseif (-not $hasParent) { 'set_parent' }
            elseif ($inferred -match 'STORY|TASK' -and -not $hasAc) { 'draft_acceptance_criteria' }
            elseif ($inferred -eq 'FEATURE' -and -not $hasPfw) { 'link_or_draft_pfw_dwi' }
            elseif ($inferred -match 'STORY|TASK' -and -not $hasPts) { 'set_points' }
            elseif (-not $hasMeta) { 'normalize_meta' }
            else { 'ok_or_review' }
        )
    }
    [void]$rows.Add($row)
}

$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$csvPath = Join-Path $outDir 'gap_report_data_crucible.csv'
$jsonPath = Join-Path $outDir 'gap_report_summary.json'

$rows | Sort-Object -Property normalize_priority -Descending | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

$summary = [pscustomobject]@{
    generated          = (Get-Date).ToString('o')
    project_id         = $env:GITLAB_PROJECT_ID
    labels             = $Labels
    state_filter       = $State
    counts             = $counts
    normalize_order    = @('set_parent', 'draft_acceptance_criteria', 'link_or_draft_pfw_dwi', 'set_points', 'normalize_meta')
    csv                = $csvPath
    top_priority_iids  = @($rows | Sort-Object normalize_priority -Descending | Where-Object { -not $_.is_cascade_dup -and $_.normalize_priority -gt 0 } | Select-Object -First 15 | ForEach-Object { $_.iid })
}
$summary | ConvertTo-Json -Depth 6 | Set-Content $jsonPath -Encoding UTF8

Write-Host ''
Write-Host '=== SUMMARY ===' -ForegroundColor Cyan
Write-Host ("Total issues:     {0}" -f $counts.total)
Write-Host ("  open/closed:    {0} / {1}" -f $counts.open, $counts.closed)
Write-Host ("  types S/F/E:    {0} / {1} / {2}" -f $counts.story, $counts.feature, $counts.epic)
Write-Host ("  CASCADE-DUP:    {0}" -f $counts.cascade_dup)
Write-Host ("Missing meta:     {0}" -f $counts.missing_meta) -ForegroundColor Yellow
Write-Host ("Missing parent:   {0}" -f $counts.missing_parent) -ForegroundColor Yellow
Write-Host ("Stories missing AC: {0}" -f $counts.missing_ac) -ForegroundColor Yellow
Write-Host ("Features missing PfW/DWI: {0}" -f $counts.missing_pfw) -ForegroundColor Yellow
Write-Host ("Stories missing points: {0}" -f $counts.missing_points) -ForegroundColor Yellow
Write-Host ("Blocker-flagged:  {0}" -f $counts.has_blocker)
Write-Host ''
Write-Host ("CSV: {0}" -f $csvPath) -ForegroundColor Green
Write-Host ("Summary JSON: {0}" -f $jsonPath) -ForegroundColor Green
if ($savedWrite) { $env:GITLAB_PROJECT_ID = $savedWrite }
Write-Host 'Sort CSV by normalize_priority (high first). suggested_next is the work queue.' -ForegroundColor Cyan
Write-GapStep 'Gap report complete' 'OK'
