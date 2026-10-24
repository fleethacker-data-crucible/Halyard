<#
.SYNOPSIS
  Push / rebuild CBR issues into SANDBOX with Cascade topology and schema.
  Default UPSERT by source_iid (updates existing refine::sandbox-review mirrors).
  READS CBR; WRITES sandbox only. Does not modify CBR.

  Titles: TYPE :: Title :: HUMAN APPROVAL REQUIRED
  Meta: full cascade-meta including effort/points/sprint (TBD when unknown)
  Body: type-specific sections + PfW/DWI draft when Feature context exists + ORIGIN
#>
param(
    [string]$TablePath = '',
    [ValidateSet('All','Accept','Meeting','AcceptAndMeeting','Skip')][string]$Selection = 'All',
    [int]$MaxPush = 200,
    [string[]]$OnlyIids = @(),
    [string]$Labels = 'area::data-crucible',
    [switch]$FromCbrDirect,
    [switch]$DryRun,
    [switch]$Force,
    [switch]$Upsert,
    [switch]$NoUpsert
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
foreach ($extra in @('WritePolicy.ps1','Taxonomy.ps1','Logging.ps1','Schema.ps1','GitLab.ps1','GenAI.ps1')) {
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

$outDir = Join-Path $RepoRoot 'output'

# Default: upsert by source_iid (update existing sandbox mirror). -NoUpsert forces create-only.
$doUpsert = $true
if ($NoUpsert) { $doUpsert = $false }
if ($Upsert) { $doUpsert = $true }
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

Write-Host '=== Cascade Sandbox rebuild from CBR (schema-compliant) ===' -ForegroundColor Cyan
$ctx = Write-CascadeWriteBanner
Write-Host 'Sandbox only. CBR is not modified.' -ForegroundColor DarkGray

Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
$url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$tok = Get-CascadeGitLabToken -AllowPrompt
$headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }

$readId = 16629
$writeId = 52999
try { if ($ctx.ReadProjectId) { $readId = [int]$ctx.ReadProjectId } } catch {}
try { if ($ctx.WriteProjectId) { $writeId = [int]$ctx.WriteProjectId } } catch {}
if ($readId -le 0) { $readId = 16629 }
if ($writeId -le 0) { $writeId = 52999 }
$readBase = "$url/api/v4/projects/$readId"
$writeBase = "$url/api/v4/projects/$writeId"


function Get-SandboxOriginIndex {
    param($WriteBase, $Headers, [int]$MaxPages = 20)
    $map = @{}
    $page = 1
    do {
        $uri = "$WriteBase/issues?state=opened&per_page=50&page=$page&labels=refine%3A%3Asandbox-review"
        try {
            $raw = Invoke-RestMethod -Uri $uri -Headers $Headers -TimeoutSec 120
        } catch {
            break
        }
        $batch = @()
        if ($null -eq $raw) { $batch = @() }
        elseif ($raw -is [System.Array]) { $batch = @($raw) }
        else { $batch = @($raw) }
        if ($batch.Count -eq 0) { break }
        foreach ($iss in $batch) {
            if ($null -eq $iss) { continue }
            if ($iss -is [System.Array]) { $iss = $iss[0] }
            $desc = ''
            try { $desc = [string]$iss.description } catch { $desc = '' }
            $srcIid = $null
            if ($desc -match '(?im)^\s*source_iid:\s*(\d+)\s*$') {
                $srcIid = [int]$Matches[1]
            } elseif ($desc -match '(?i)CBR issue IID\*\*\s*\|\s*\*\*!(\d+)') {
                $srcIid = [int]$Matches[1]
            } elseif ($desc -match '(?i)source_iid[:\s]+(\d+)') {
                $srcIid = [int]$Matches[1]
            }
            if ($srcIid -and -not $map.ContainsKey($srcIid)) {
                $map[$srcIid] = [int]$iss.iid
            }
        }
        $page++
    } while ($batch.Count -eq 50 -and $page -le $MaxPages)
    return $map
}

function Get-LabelNames($issue) {
    $cur = @()
    try {
        if ($issue.labels) {
            if ($issue.labels[0].name) { $cur = @($issue.labels | ForEach-Object { $_.name }) }
            else { $cur = @($issue.labels | ForEach-Object { "$_" }) }
        }
    } catch { $cur = @() }
    return @($cur)
}

function Get-HeuristicType([string]$Title, [string]$Labs, [string]$Hint) {
    if ($Hint -match '^(THEME|EPIC|FEATURE|STORY)$') { return $Hint.ToUpperInvariant() }
    if ($Title -match '(?i)^THEME\s*::') { return 'THEME' }
    if ($Title -match '(?i)^EPIC\s*::') { return 'EPIC' }
    if ($Title -match '(?i)^FEATURE\s*::') { return 'FEATURE' }
    if ($Title -match '(?i)^STORY\s*::') { return 'STORY' }
    if ($Title -match '(?i)^INC\d+') { return 'STORY' }
    if ($Title -match '(?i)roadmap|gantt|governance memo|onboarding to systems|training') { return 'EPIC' }
    if ($Title -match '(?i)build out data lake|end-to-end|workflow for custom') { return 'FEATURE' }
    if ($Title -match '(?i)^(fix|deploy|fix|configure|setup|schedule|submit|send|process|validate|parallelize|test |research )') { return 'STORY' }
    return 'STORY'
}

function Get-HeuristicTheme([string]$Title, [string]$Labs, [string]$Snippet, [string]$Hint) {
    if ($Hint -and $Hint -match '^THEME-') { return $Hint }
    $blob = "$Title $Labs $Snippet"
    if ($blob -match '(?i)bedrock|sagemaker|llm|ml |machine learning|automation') { return 'THEME-automation' }
    if ($blob -match '(?i)qlik|data lake|parquet|etl|postgres|rds|bip dw|concerto|s3') { return 'THEME-platform-engineering' }
    if ($blob -match '(?i)training|qual card|onboarding|survey|welcome|governance|STIG|RMF') { return 'THEME-process-improvement' }
    return 'THEME-platform-engineering'
}

function Get-CleanTitle([string]$Title) {
    $t = $Title
    if ($t -match '^(?i)(THEME|EPIC|FEATURE|STORY)\s*::\s*(.+)$') { $t = $Matches[2].Trim() }
    return $t
}

function New-Slug([string]$Title, [int]$Iid) {
    $idSlug = ($Title -replace '^(THEME|EPIC|FEATURE|STORY)\s*::\s*', '' -replace '[^A-Za-z0-9]+','-').Trim('-').ToLowerInvariant()
    if ($idSlug.Length -gt 36) { $idSlug = $idSlug.Substring(0,36).Trim('-') }
    if (-not $idSlug) { $idSlug = "cbr-$Iid" }
    return $idSlug
}

function Build-TypeBody {
    param(
        [string]$Type,
        [string]$Title,
        [string]$Snippet,
        [string]$Why,
        [string]$Theme,
        [string]$Parent,
        [bool]$NeedsSplit,
        [string]$Action
    )
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('## Review decision')
    [void]$sb.AppendLine('- [ ] Approve structure')
    [void]$sb.AppendLine('- [ ] Edit type / parent / sizing')
    [void]$sb.AppendLine('- [ ] Split')
    [void]$sb.AppendLine('- [ ] Reject (leave CBR as-is)')
    [void]$sb.AppendLine('- [ ] Needs meeting')
    [void]$sb.AppendLine('')

    switch ($Type) {
        'THEME' {
            [void]$sb.AppendLine('## Purpose')
            [void]$sb.AppendLine($(if ($Snippet) { $Snippet } else { "Portfolio lane for: $Title" }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## In scope')
            [void]$sb.AppendLine('- (confirm during review)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Out of scope')
            [void]$sb.AppendLine('- (confirm during review)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Owner')
            [void]$sb.AppendLine('- (assign)')
        }
        'EPIC' {
            [void]$sb.AppendLine('## Outcome')
            [void]$sb.AppendLine($(if ($Snippet) { $Snippet } elseif ($Why) { $Why } else { "Deliver outcome for: $Title" }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Success picture')
            [void]$sb.AppendLine('- [ ] (define measurable success)')
            [void]$sb.AppendLine('- [ ] (define verification)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Owner')
            [void]$sb.AppendLine('- (assign)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Child Features')
            [void]$sb.AppendLine('- (to be linked)')
            if ($NeedsSplit) {
                [void]$sb.AppendLine('')
                [void]$sb.AppendLine('> **needs_split:** true - break into Features before execution.')
            }
        }
        'FEATURE' {
            [void]$sb.AppendLine('## Description')
            [void]$sb.AppendLine($(if ($Snippet) { $Snippet } elseif ($Why) { $Why } else { $Title }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Parent Epic')
            [void]$sb.AppendLine($(if ($Parent) { $Parent } else { '(none - set during review)' }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Definition of done')
            [void]$sb.AppendLine('- [ ] Capability works in target environment')
            [void]$sb.AppendLine('- [ ] Documentation / runbook updated if operational')
            [void]$sb.AppendLine('- [ ] Reviewer acceptance')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Effort')
            [void]$sb.AppendLine('TBD - set Small | Medium | Large during review.')
            [void]$sb.AppendLine('')
            # PfW draft when we have any signal
            [void]$sb.AppendLine('## Proposal for Work')
            [void]$sb.AppendLine('### Problem / opportunity')
            [void]$sb.AppendLine($(if ($Why) { $Why } else { "Enable: $Title" }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('### Proposed approach')
            [void]$sb.AppendLine($(if ($Snippet) { $Snippet } else { '(draft from CBR during review)' }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('### Scope in')
            [void]$sb.AppendLine('- (confirm)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('### Scope out')
            [void]$sb.AppendLine('- (confirm)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('### Done-when')
            [void]$sb.AppendLine('- [ ] (observable completion)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('### Dependencies')
            [void]$sb.AppendLine('- (teams / systems)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('### Suggested child Stories')
            [void]$sb.AppendLine('- STORY :: (slice 1)')
            [void]$sb.AppendLine('- STORY :: (slice 2)')
            if ($Snippet -and $Snippet.Length -gt 400) {
                [void]$sb.AppendLine('')
                [void]$sb.AppendLine('> Context volume suggests elevating this PfW to a **Defined Work Instruction (DWI)** if research/contracts are non-trivial.')
            }
        }
        default { # STORY
            [void]$sb.AppendLine('## User need')
            [void]$sb.AppendLine($(if ($Snippet) { $Snippet } elseif ($Why) { $Why } else { "As a stakeholder, I need progress on: $Title" }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Acceptance criteria')
            [void]$sb.AppendLine('- [ ] Observable result demonstrated')
            [void]$sb.AppendLine('- [ ] No undocumented side effects in scope')
            [void]$sb.AppendLine('- [ ] Evidence linked (MR, test note, or ticket)')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Tasks')
            [void]$sb.AppendLine('- [ ] Clarify scope with owner')
            [void]$sb.AppendLine('- [ ] Implement / execute')
            [void]$sb.AppendLine('- [ ] Verify and record evidence')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Parent Feature')
            [void]$sb.AppendLine($(if ($Parent) { $Parent } else { '(none - set during review)' }))
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('## Sizing')
            [void]$sb.AppendLine('- **Points:** TBD (1|2|3|5|8|13)')
            [void]$sb.AppendLine('- **Sprint:** TBD')
            if ($NeedsSplit) {
                [void]$sb.AppendLine('')
                [void]$sb.AppendLine('> **needs_split:** true - may exceed one sprint; consider Feature + multiple Stories.')
            }
        }
    }
    return $sb.ToString()
}

# --- Load work list ---
$selected = @()

if ($FromCbrDirect) {
    Write-Host ("Direct CBR fetch labels={0}" -f $Labels) -ForegroundColor Cyan
    $flat = New-Object System.Collections.Generic.List[object]
    $page = 1
    do {
        $uri = "$readBase/issues?state=opened&per_page=50&page=$page&order_by=updated_at&sort=desc"
        if ($Labels) { $uri += '&labels=' + [uri]::EscapeDataString($Labels) }
        $raw = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 120
        $batch = @()
        if ($null -eq $raw) { $batch = @() }
        elseif ($raw -is [System.Array]) { $batch = @($raw) }
        else { $batch = @($raw) }
        foreach ($b in $batch) {
            if ($null -eq $b) { continue }
            if ($b -is [System.Array]) {
                foreach ($inner in $b) {
                    if ($null -ne $inner -and $inner.PSObject.Properties['iid']) { [void]$flat.Add($inner) }
                }
            } elseif ($b.PSObject.Properties.Name -contains 'iid') {
                [void]$flat.Add($b)
            }
        }
        $page++
    } while ($batch.Count -eq 50 -and $page -le 15 -and $flat.Count -lt $MaxPush)

    foreach ($iss in $flat) {
        if ($iss -is [System.Array]) { $iss = $iss[0] }
        $iid = [int](@($iss.iid)[0])
        if ($OnlyIids.Count -gt 0) {
            $ok = $false
            foreach ($o in $OnlyIids) {
                $n = 0
                if ([int]::TryParse("$o", [ref]$n) -and $n -eq $iid) { $ok = $true }
            }
            if (-not $ok) { continue }
        }
        $title = [string](@($iss.title)[0])
        $desc = ''
        try { $desc = [string](@($iss.description)[0]) } catch { $desc = '' }
        $labs = (Get-LabelNames $iss) -join ','
        $selected += [pscustomobject]@{
            iid = $iid
            title = $title
            labels = $labs
            snippet = $(if ($desc.Length -gt 500) { $desc.Substring(0,500) } else { $desc })
            type_guess = ''
            theme_guess = ''
            action = 'ACCEPT_CANDIDATE'
            why = 'Direct CBR replicate'
            needs_split = $false
            confidence = 'medium'
            source_issue = $iss
        }
    }
} else {
    if (-not $TablePath) { $TablePath = Join-Path $outDir 'bulk_refine_table.json' }
    if (-not (Test-Path $TablePath)) {
        throw "Table not found: $TablePath. Run [L] first, or use -FromCbrDirect."
    }
    $rawTable = Get-Content $TablePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $rows = @($rawTable)
    $want = @()
    switch ($Selection) {
        'Accept' { $want = @('ACCEPT_CANDIDATE') }
        'Meeting' { $want = @('MEETING') }
        'Skip' { $want = @('SKIP') }
        'AcceptAndMeeting' { $want = @('ACCEPT_CANDIDATE', 'MEETING') }
        default { $want = @('ACCEPT_CANDIDATE', 'MEETING', 'SKIP') }
    }
    foreach ($r in $rows) {
        if ($r.exclude -eq $true -or "$($r.exclude)" -eq 'True') { continue }
        $act = [string]$r.action
        if ($want -notcontains $act) { continue }
        $iid = 0
        try { $iid = [int]$r.iid } catch { continue }
        if ($OnlyIids.Count -gt 0) {
            $ok = $false
            foreach ($o in $OnlyIids) {
                $n = 0
                if ([int]::TryParse("$o", [ref]$n) -and $n -eq $iid) { $ok = $true }
            }
            if (-not $ok) { continue }
        }
        $selected += $r
    }
}

if ($MaxPush -gt 0 -and $selected.Count -gt $MaxPush) {
    Write-Host ("Capping at MaxPush={0} (had {1})" -f $MaxPush, $selected.Count) -ForegroundColor Yellow
    $selected = @($selected | Select-Object -First $MaxPush)
}

Write-Host ("Rows to push: {0}  READ={1} WRITE={2}" -f $selected.Count, $readId, $writeId) -ForegroundColor Cyan
if ($selected.Count -eq 0) {
    Write-Host 'Nothing to push.' -ForegroundColor Yellow
    return
}

$selected | Select-Object iid, action, type_guess, @{n='title';e={ $t=[string]$_.title; if ($t.Length -gt 50) { $t.Substring(0,50)+'...' } else { $t } }} |
    Format-Table -AutoSize | Out-Host

if (-not $Force -and -not $DryRun) {
    $ans = Read-Host ("Upsert/create {0} schema-compliant sandbox issue(s)? [Y/N]" -f $selected.Count)
    if ($ans -notmatch '^(Y|y)') {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        return
    }
}

$logPath = Join-Path $outDir 'bulk_push_sandbox.jsonl'
$stats = [ordered]@{ created = 0; updated = 0; skipped = 0; errors = 0; dry_run = [bool]$DryRun; upsert = [bool]$doUpsert }

$originIndex = @{}
if ($doUpsert) {
    Write-Host 'Indexing sandbox refine::sandbox-review by source_iid...' -ForegroundColor DarkGray
    try {
        $originIndex = Get-SandboxOriginIndex -WriteBase $writeBase -Headers $headers
        Write-Host ("  Indexed {0} existing mirror(s)" -f $originIndex.Count) -ForegroundColor DarkGray
    } catch {
        Write-Host ("  Index failed (will create-only): {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        $originIndex = @{}
    }
}


foreach ($r in $selected) {
    $iid = [int]$r.iid
    $action = [string]$r.action
    if (-not $action) { $action = 'ACCEPT_CANDIDATE' }
    $why = [string]$r.why
    $titleBase = [string]$r.title

    Write-Host ("Pushing CBR !{0}..." -f $iid) -ForegroundColor DarkGray
    try {
        $src = $null
        if ($r.PSObject.Properties.Name -contains 'source_issue' -and $r.source_issue) {
            $src = $r.source_issue
        } else {
            $src = Invoke-RestMethod -Uri "$readBase/issues/$iid" -Headers $headers -TimeoutSec 60
        }
        $srcTitle = [string]$src.title
        if (-not $srcTitle) { $srcTitle = $titleBase }
        $srcDesc = ''
        try { $srcDesc = [string]$src.description } catch { $srcDesc = '' }
        if ($null -eq $srcDesc) { $srcDesc = '' }
        $srcLabs = Get-LabelNames $src
        $labsJoined = $srcLabs -join ','
        $snippet = [string]$r.snippet
        if (-not $snippet -and $srcDesc) {
            $snippet = if ($srcDesc.Length -gt 500) { $srcDesc.Substring(0,500) } else { $srcDesc }
        }

        $typeGuess = Get-HeuristicType -Title $srcTitle -Labs $labsJoined -Hint ([string]$r.type_guess)
        $theme = Get-HeuristicTheme -Title $srcTitle -Labs $labsJoined -Snippet $snippet -Hint ([string]$r.theme_guess)
        $needsSplit = $false
        try { $needsSplit = [bool]$r.needs_split } catch { $needsSplit = $false }

        $cleanTitle = Get-CleanTitle $srcTitle
        $slug = New-Slug -Title $srcTitle -Iid $iid
        $cascadeId = "$typeGuess-$slug"
        $parent = ''
        # parent left TBD; theme always set
        $suffix = ' :: HUMAN APPROVAL REQUIRED'
        $newTitle = "{0} :: {1}{2}" -f $typeGuess, $cleanTitle, $suffix
        if ($newTitle.Length -gt 255) {
            $maxCore = 255 - $typeGuess.Length - $suffix.Length - 4
            if ($maxCore -lt 40) { $maxCore = 40 }
            if ($cleanTitle.Length -gt $maxCore) { $cleanTitle = $cleanTitle.Substring(0, $maxCore - 3) + '...' }
            $newTitle = "{0} :: {1}{2}" -f $typeGuess, $cleanTitle, $suffix
        }

        $effort = ''
        $points = ''
        $sprint = ''
        if ($typeGuess -eq 'FEATURE') { $effort = 'TBD' }
        if ($typeGuess -eq 'STORY') { $points = 'TBD'; $sprint = 'TBD' }

        $extra = @{
            source = 'bulk-push-sandbox'
            source_project = "$readId"
            source_iid = "$iid"
            bulk_action = $action
            confidence = $(if ($r.confidence) { [string]$r.confidence } else { 'medium' })
            needs_split = "$needsSplit"
            review_status = 'pending'
        }
        if ($typeGuess -eq 'FEATURE') {
            $extra['artifact_level'] = 'Proposal for Work'
            $extra['pfw_id'] = "PFW-$slug"
        }

        $meta = New-CascadeMetaBlockFromTaxonomy -Type $typeGuess -Id $cascadeId `
            -Theme $theme -Parent $parent -State open -Area 'data-crucible' `
            -Effort $effort -Points $points -Sprint $sprint -Milestone '' -Extra $extra

        $mirror = @"
### Cascade
- **Type:** $typeGuess
- **Id:** $cascadeId
- **Theme:** $theme
- **Parent:** $(if ($parent) { $parent } else { '(none - set in review)' })
- **Effort:** $(if ($typeGuess -eq 'FEATURE') { 'TBD' } else { 'n/a' })
- **Points / Sprint:** $(if ($typeGuess -eq 'STORY') { 'TBD / TBD' } else { 'n/a' })
- **Artifact:** $(if ($typeGuess -eq 'FEATURE') { 'Proposal for Work (draft in body)' } else { 'n/a' })
- **CBR origin:** project $readId !$iid
"@

        $typeBody = Build-TypeBody -Type $typeGuess -Title $cleanTitle -Snippet $snippet `
            -Why $why -Theme $theme -Parent $parent -NeedsSplit $needsSplit -Action $action

        $body = @"
$meta

$mirror

$typeBody

## ORIGIN (CBR system of record)
| Field | Value |
|-------|-------|
| **CBR project** | $readId |
| **CBR issue IID** | **!$iid** |
| **Bulk action** | $action |
| **Why (triage)** | $why |

Sandbox is a review copy. Edit here for structure; translate approved changes to CBR **!$iid**.

## Original CBR title
$srcTitle

## Original CBR labels
$labsJoined

## Original CBR description
$srcDesc
"@

        $labs = @('cascade-test', 'cascade', 'area::data-crucible', 'refine::sandbox-review')
        if ($action -eq 'MEETING') { $labs += 'refine::human-needed' }
        elseif ($action -eq 'SKIP') { $labs += 'refine::bulk-skip' }
        else { $labs += 'refine::ai-accepted' }
        $labs += ('type::{0}' -f $typeGuess.ToLowerInvariant())
        $labs = @($labs | Select-Object -Unique)

        $existingSid = $null
        if ($doUpsert -and $originIndex.ContainsKey($iid)) {
            $existingSid = [int]$originIndex[$iid]
        }

        if ($DryRun) {
            if ($existingSid) {
                Write-Host ("  DryRun UPDATE sandbox !{0} <- CBR !{1} | {2}" -f $existingSid, $iid, $newTitle) -ForegroundColor Yellow
                $stats.updated++
            } else {
                Write-Host ("  DryRun CREATE <- CBR !{0} | {1}" -f $iid, $newTitle) -ForegroundColor Yellow
                $stats.created++
            }
            continue
        }

        $null = Assert-CascadeWriteTarget -ProjectId $writeId
        $payloadObj = @{
            title       = $newTitle
            description = $body
            labels      = ($labs -join ',')
        }
        $payload = $payloadObj | ConvertTo-Json -Depth 6
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)

        if ($existingSid) {
            $updated = Invoke-RestMethod -Method Put -Uri "$writeBase/issues/$existingSid" -Headers $headers `
                -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
            Write-Host ("  UPDATED sandbox !{0} <- CBR !{1} [{2}]" -f $updated.iid, $iid, $typeGuess) -ForegroundColor Cyan
            $stats.updated++
            $line = @{
                source_iid = $iid
                sandbox_iid = $updated.iid
                type = $typeGuess
                title = $newTitle
                action = 'updated'
                ts = (Get-Date).ToString('o')
            } | ConvertTo-Json -Compress
            Add-Content -Path $logPath -Value $line -Encoding UTF8
        } else {
            $created = Invoke-RestMethod -Method Post -Uri "$writeBase/issues" -Headers $headers `
                -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
            Write-Host ("  CREATED sandbox !{0} <- CBR !{1} [{2}]" -f $created.iid, $iid, $typeGuess) -ForegroundColor Green
            $stats.created++
            if ($doUpsert) { $originIndex[$iid] = [int]$created.iid }
            $line = @{
                source_iid = $iid
                sandbox_iid = $created.iid
                type = $typeGuess
                title = $newTitle
                action = 'created'
                ts = (Get-Date).ToString('o')
            } | ConvertTo-Json -Compress
            Add-Content -Path $logPath -Value $line -Encoding UTF8
        }
    } catch {
        Write-Host ("  ERROR CBR !{0}: {1}" -f $iid, $_.Exception.Message) -ForegroundColor Red
        $stats.errors++
        if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
            $null = Write-CascadeErrorLog -Operation 'BulkPushSandbox' -Message $_.Exception.Message -Context @{ source_iid = $iid }
        }
    }
}

($stats | ConvertTo-Json) | Set-Content (Join-Path $outDir 'bulk_push_summary.json') -Encoding UTF8
Write-Host ''
Write-Host '=== Push summary ===' -ForegroundColor Cyan
Write-Host ($stats | ConvertTo-Json)
Write-Host "Log: $logPath"
Write-Host 'Filter sandbox: refine::sandbox-review'
