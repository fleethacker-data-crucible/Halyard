<#
.SYNOPSIS
  One-at-a-time backlog refinement.
  READS from central business repo (GITLAB_SOURCE_PROJECT_ID / CBR).
  WRITES only to sandbox (CASCADE_WRITE_MODE=sandbox).
  CURRENT vs PROPOSED; Accept = create refined copy in sandbox;
  Reject = sandbox stub with refine::human-needed for meeting agenda.
#>
param(
    [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
    [string]$State = 'opened',
    [string]$Labels = 'area::data-crucible',
    [int]$StartAtIid = 0,
    [int]$MaxItems = 0,
    [switch]$DryRun
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
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$reportPath = Join-Path $outDir 'backlog_refine_session.jsonl'
$summaryPath = Join-Path $outDir 'backlog_refine_summary.json'

Write-Host '=== Cascade Backlog Refinement (1 item at a time) ===' -ForegroundColor Cyan
$ctx = Write-CascadeWriteBanner
Write-Host 'READ  = central business repo (source)' -ForegroundColor Cyan
Write-Host 'WRITE = sandbox only (Accept/Reject copies)' -ForegroundColor Cyan
Write-Host 'Does NOT modify CBR issues.' -ForegroundColor DarkGray

$tax = Get-CascadeTaxonomy
$defaults = Get-CascadeAgentDefaults
$themeLines = @($tax.themes | ForEach-Object { '{0} | {1}' -f $_.id, $_.title }) -join "`n"

Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
$url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$tok = Get-CascadeGitLabToken -AllowPrompt
$headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }

$readId = [int]$ctx.ReadProjectId
$writeId = [int]$ctx.WriteProjectId
if ($readId -le 0) { $readId = 16629 }
if ($writeId -le 0) { $writeId = 52999 }
if ($readId -eq $writeId) {
    Write-Host 'WARNING: read_project equals write_project. Set GITLAB_SOURCE_PROJECT_ID=16629 so refine reads CBR.' -ForegroundColor Yellow
}

$readBase = "$url/api/v4/projects/$readId"
$writeBase = "$url/api/v4/projects/$writeId"

Write-Host ("Fetching open issues from READ project {0} (CBR)..." -f $readId) -ForegroundColor Cyan
Write-Host ("Label filter: {0}" -f $(if ($Labels) { $Labels } else { '(none - all labels)' })) -ForegroundColor DarkGray
$flat = New-Object System.Collections.Generic.List[object]
$page = 1
do {
    $uri = "$readBase/issues?state=$State&per_page=50&page=$page&order_by=updated_at&sort=desc"
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
    # safety: don't pull entire enterprise backlog in one session without MaxItems
    if ($MaxItems -gt 0 -and $flat.Count -ge ($MaxItems * 3)) { break }
} while ($batch.Count -eq 50 -and $page -le 20)

$queue = @(
    $flat.ToArray() |
        Where-Object { $null -ne $_ -and $_.PSObject.Properties['iid'] } |
        Sort-Object { [int](@($_.iid)[0]) }
)
if ($StartAtIid -gt 0) {
    $queue = @($queue | Where-Object { [int](@($_.iid)[0]) -ge $StartAtIid })
}
if ($MaxItems -gt 0) {
    $queue = @($queue | Select-Object -First $MaxItems)
}

Write-Host ("Loaded {0} issue(s) from CBR project {1}." -f $queue.Count, $readId) -ForegroundColor Green
if ($queue.Count -eq 0) {
    Write-Host 'No issues on CBR with label area::data-crucible (or your -Labels filter). Check token access to 16629.' -ForegroundColor Yellow
    return
}

$stats = [ordered]@{ accepted = 0; rejected_human = 0; skipped = 0; errors = 0; total = [int]$queue.Count; read_project = $readId; write_project = $writeId }

function Get-BodyPreview([string]$text, [int]$max = 900) {
    if ([string]::IsNullOrWhiteSpace($text)) { return '(empty)' }
    $t = $text -replace "`r", ''
    if ($t.Length -le $max) { return $t }
    return $t.Substring(0, $max) + "`n... (truncated)"
}

function Build-RefineSystemPrompt {
    @"
You are a JSON API for Cascade backlog refinement. You are NOT a chat assistant.

CRITICAL:
1. Reply with ONLY one JSON object. No markdown. No tables. No prose. No code fences.
2. First character must be { and last must be }.
3. Do not write Key Takeaway, Summary of Refinements, or Refined Issue Specification.

Hierarchy: THEME > EPIC > FEATURE > STORY. Milestone is association only.
Feature effort: Small|Medium|Large. Story points: 1|2|3|5|8|13 plus sprint. Tasks = checklist in Story.
Allowed themes:
$themeLines

Schema:
{
  "confidence": "high|medium|low",
  "needs_human_refinement": false,
  "human_questions": [],
  "type": "THEME|EPIC|FEATURE|STORY",
  "title": "TYPE :: short name",
  "id": "TYPE-slug",
  "theme": "THEME-...",
  "parent": "",
  "effort": "",
  "points": "",
  "sprint": "",
  "milestone": "",
  "area": "",
  "labels": ["cascade"],
  "outcome": "",
  "success_picture": [],
  "short_description": "",
  "definition_of_done": [],
  "user_story": "",
  "acceptance_criteria": [],
  "tasks": [],
  "body_markdown": "markdown body without cascade-meta fence",
  "rationale": "one sentence"
}

If too vague: needs_human_refinement=true, confidence=low, still return best-effort JSON shell.
"@
}

function ConvertFrom-RefineMarkdown {
    param([string]$Text, [string]$FallbackTitle)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $mdTitle = $null
    $mdType = $null
    if ($Text -match '(?im)\|\s*\*\*Title\*\*\s*\|\s*`?([^|`]+)`?\s*\|') { $mdTitle = $Matches[1].Trim() }
    elseif ($Text -match '(?im)\|\s*Title\s*\|\s*`?([^|`]+)`?\s*\|') { $mdTitle = $Matches[1].Trim() }
    elseif ($Text -match '(?im)\*\*Title\*\*\s*[|:]\s*`?([^\r\n|`]+)') { $mdTitle = $Matches[1].Trim() }
    if ($Text -match '(?im)\|\s*\*\*Type\*\*\s*\|\s*`?([^|`]+)`?\s*\|') {
        $tv = $Matches[1]
        if ($tv -match '(?i)THEME') { $mdType = 'THEME' }
        elseif ($tv -match '(?i)EPIC') { $mdType = 'EPIC' }
        elseif ($tv -match '(?i)FEATURE') { $mdType = 'FEATURE' }
        elseif ($tv -match '(?i)STORY') { $mdType = 'STORY' }
    }
    if (-not $mdType -and $mdTitle -match '^(THEME|EPIC|FEATURE|STORY)\s*::') {
        $mdType = $Matches[1].ToUpperInvariant()
    }
    if (-not $mdType -and $FallbackTitle -match '^(THEME|EPIC|FEATURE|STORY)\s*::') {
        $mdType = $Matches[1].ToUpperInvariant()
        if (-not $mdTitle) { $mdTitle = $FallbackTitle }
    }
    if (-not $mdTitle) { $mdTitle = $FallbackTitle }
    if (-not $mdTitle -and -not $mdType) { return $null }

    # Prefer fenced markdown body if present
    $bodyMd = ''
    if ($Text -match '(?s)```(?:markdown|md)?\s*(.*?)```') {
        $bodyMd = $Matches[1].Trim()
        # strip meta fence from recovered body; tool re-applies
        if ($bodyMd -match '(?s)~~~cascade-meta.*?~~~') {
            $bodyMd = $bodyMd -replace '(?s)~~~cascade-meta.*?~~~\s*', ''
        }
        if ($bodyMd -match '(?s)<!--.*?-->') {
            $bodyMd = $bodyMd -replace '(?s)<!--.*?-->\s*', ''
        }
    }

    $id = $null
    if ($Text -match '(?im)id:\s*(THEME|EPIC|FEATURE|STORY)-([a-z0-9\-]+)') {
        $id = $Matches[1].ToUpperInvariant() + '-' + $Matches[2]
        if (-not $mdType) { $mdType = $Matches[1].ToUpperInvariant() }
    }
    if (-not $id -and $mdType) {
        $slug = ($mdTitle -replace '^(THEME|EPIC|FEATURE|STORY)\s*::\s*', '' -replace '[^A-Za-z0-9]+','-').Trim('-').ToLowerInvariant()
        if ($slug.Length -gt 40) { $slug = $slug.Substring(0,40).Trim('-') }
        $id = "$mdType-$slug"
    }

    return [pscustomobject]@{
        confidence = 'low'
        needs_human_refinement = $true
        human_questions = @('AI returned markdown; verify taxonomy fields in refinement meeting')
        type = $(if ($mdType) { $mdType } else { 'STORY' })
        title = $(if ($mdTitle) { $mdTitle } else { $FallbackTitle })
        id = $id
        theme = ''
        parent = ''
        effort = ''
        points = ''
        sprint = ''
        milestone = ''
        area = 'cascade'
        labels = @('cascade', 'refine::from-markdown')
        outcome = ''
        success_picture = @()
        short_description = ''
        definition_of_done = @()
        user_story = ''
        acceptance_criteria = @()
        tasks = @()
        body_markdown = $bodyMd
        rationale = 'Recovered from markdown LLM response'
    }
}

function Build-ProposedDescription($prop, [int]$SourceIid, [int]$SourceProject) {
    $type = ([string]$prop.type).ToUpperInvariant()
    if (-not $type) { $type = 'STORY' }
    $id = [string]$prop.id
    if (-not $id) { $id = "$type-unknown" }
    $extra = @{
        source = 'backlog-refine'
        source_project = "$SourceProject"
        source_iid = "$SourceIid"
        confidence = $([string]$prop.confidence)
    }
    $meta = New-CascadeMetaBlockFromTaxonomy -Type $type -Id $id `
        -Theme $([string]$prop.theme) -Parent $([string]$prop.parent) `
        -State open -Area $([string]$prop.area) `
        -Effort $([string]$prop.effort) -Points $([string]$prop.points) `
        -Sprint $([string]$prop.sprint) -Milestone $([string]$prop.milestone) `
        -Extra $extra

    $body = [string]$prop.body_markdown
    if ([string]::IsNullOrWhiteSpace($body)) {
        $parts = New-Object System.Collections.Generic.List[string]
        if ($type -eq 'EPIC') {
            [void]$parts.Add('## Outcome')
            [void]$parts.Add($([string]$prop.outcome))
            [void]$parts.Add('')
            [void]$parts.Add('## Success picture')
            foreach ($b in @($prop.success_picture)) { if ("$b".Trim()) { [void]$parts.Add("- $b") } }
        } elseif ($type -eq 'FEATURE') {
            [void]$parts.Add('## Description')
            [void]$parts.Add($([string]$prop.short_description))
            [void]$parts.Add('')
            [void]$parts.Add('## Definition of done')
            foreach ($b in @($prop.definition_of_done)) { if ("$b".Trim()) { [void]$parts.Add("- $b") } }
            if ($prop.effort) { [void]$parts.Add(''); [void]$parts.Add("**Effort:** $($prop.effort)") }
        } elseif ($type -eq 'STORY') {
            [void]$parts.Add('## User need')
            [void]$parts.Add($([string]$prop.user_story))
            [void]$parts.Add('')
            [void]$parts.Add('## Acceptance criteria')
            foreach ($b in @($prop.acceptance_criteria)) { if ("$b".Trim()) { [void]$parts.Add("- [ ] $b") } }
            [void]$parts.Add('')
            [void]$parts.Add('## Tasks')
            $tasks = @($prop.tasks)
            if ($tasks.Count -eq 0) { $tasks = @('Refine AC','Implement slice','Verify in TEST') }
            foreach ($b in $tasks) { if ("$b".Trim()) { [void]$parts.Add("- [ ] $b") } }
        } else {
            [void]$parts.Add('## Purpose')
            $pur = [string]$prop.short_description
            if (-not $pur) { $pur = [string]$prop.outcome }
            [void]$parts.Add($(if ($pur) { $pur } else { 'Theme lane for related Epics.' }))
            [void]$parts.Add('')
            [void]$parts.Add('## Child items')
        }
        [void]$parts.Add('')
        [void]$parts.Add(("Source: project {0} !{1}" -f $SourceProject, $SourceIid))
        $body = $parts -join "`n"
    }
    return ($meta + "`n`n" + $body.Trim())
}

function New-SandboxIssue([string]$Title, [string]$Description, [string[]]$Labels) {
    $null = Assert-CascadeWriteTarget -ProjectId $writeId
    $labs = @($Labels + @('cascade-test') | Where-Object { $_ } | Select-Object -Unique)
    $payload = @{
        title       = $Title
        description = $Description
        labels      = ($labs -join ',')
    } | ConvertTo-Json -Depth 6
    return Invoke-RestMethod -Method Post -Uri "$writeBase/issues" -Headers $headers `
        -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
}

$n = 0
foreach ($issue in $queue) {
    $n++
    if ($issue -is [System.Array]) {
        if ($issue.Count -eq 0) { continue }
        $issue = $issue[0]
    }
    if ($null -eq $issue -or -not $issue.PSObject.Properties['iid']) {
        Write-Host 'Skipping non-issue row' -ForegroundColor Yellow
        continue
    }
    $iid = [int](@($issue.iid)[0])
    $title = [string](@($issue.title)[0])
    $desc = [string](@($issue.description)[0])
    if ($null -eq $desc) { $desc = '' }
    $curLabels = @()
    try {
        if ($issue.labels) {
            if ($issue.labels[0].name) { $curLabels = @($issue.labels | ForEach-Object { $_.name }) }
            else { $curLabels = @($issue.labels | ForEach-Object { "$_" }) }
        }
    } catch { $curLabels = @() }

    Write-Host ''
    Write-Host ('========== [{0}/{1}] CBR !{2} ==========' -f $n, $queue.Count, $iid) -ForegroundColor Cyan
    Write-Host "CURRENT title: $title" -ForegroundColor White
    Write-Host ("CURRENT labels: {0}" -f ($curLabels -join ', ')) -ForegroundColor DarkGray
    Write-Host 'CURRENT body (preview):' -ForegroundColor DarkGray
    Write-Host (Get-BodyPreview $desc 700) -ForegroundColor Gray

    $userPrompt = @"
Return JSON only. No markdown tables.

Refine this GitLab issue into taxonomy-compliant Cascade form. One item only.

source_project: $readId
iid: $iid
current_title: $title
current_labels: $($curLabels -join ', ')
current_description:
$desc
"@

    $prop = $null
    $rawText = ''
    try {
        Write-Host ("Calling LLM ($Profile) for CBR !{0} ..." -f $iid) -ForegroundColor DarkGray
        $messages = @(
            @{ role = 'system'; content = (Build-RefineSystemPrompt) }
            @{ role = 'user'; content = $userPrompt }
        )
        $chat = Invoke-CascadeChat -Profile $Profile -Messages $messages -MaxTokens 4096
        if ($chat.Content) { $rawText = [string]$chat.Content }
        elseif ($chat -is [string]) { $rawText = $chat }
        else {
            try { $rawText = [string]$chat.choices[0].message.content } catch { }
            if (-not $rawText) { try { $rawText = [string]$chat } catch { $rawText = '' } }
        }
        if (-not $rawText) { throw 'Empty LLM response' }
        $rawText | Set-Content (Join-Path $outDir ("refine_raw_{0}.txt" -f $iid)) -Encoding UTF8
        try {
            $prop = ConvertFrom-CascadeLlmJson -Text $rawText
        } catch {
            $prop = $null
        }
        if (-not $prop) {
            $prop = ConvertFrom-RefineMarkdown -Text $rawText -FallbackTitle $title
            if ($prop) {
                Write-Host 'Recovered structure from markdown response (verify carefully).' -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host ("LLM error: {0}" -f $_.Exception.Message) -ForegroundColor Red
        $stats.errors++
        if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
            $null = Write-CascadeErrorLog -Operation 'BacklogRefine' -Message $_.Exception.Message -Context @{ iid = $iid; read_project = $readId }
        }
        continue
    }

    if (-not $prop) {
        Write-Host 'Could not parse LLM JSON or markdown - offer human queue.' -ForegroundColor Yellow
        Write-Host '[R] Reject -> sandbox human meeting stub  [S] Skip  [Q] Quit' -ForegroundColor Cyan
        $choice = (Read-Host 'Choice').Trim().ToUpperInvariant()
        if ($choice -eq 'Q') { break }
        if ($choice -ne 'R') { $stats.skipped++; continue }
        $prop = [pscustomobject]@{
            type = 'STORY'; title = $title; id = "STORY-cbr-$iid"; confidence = 'low'
            needs_human_refinement = $true
            human_questions = @('AI produced unparseable output')
            labels = @('refine::human-needed'); body_markdown = "Original CBR !$iid`n`n$desc"
            theme = ''; parent = ''; area = 'cascade'; effort = ''; points = ''; sprint = ''; milestone = ''
            outcome = ''; success_picture = @(); short_description = ''; definition_of_done = @()
            user_story = ''; acceptance_criteria = @(); tasks = @(); rationale = 'parse failure'
        }
        $choice = 'R'
    } else {
        $proposedTitle = [string]$prop.title
        if (-not $proposedTitle) { $proposedTitle = $title }
        $proposedDesc = Build-ProposedDescription -prop $prop -SourceIid $iid -SourceProject $readId
        $conf = [string]$prop.confidence
        $needsHuman = $false
        try { $needsHuman = [bool]$prop.needs_human_refinement } catch {}
        if ($conf -eq 'low') { $needsHuman = $true }

        Write-Host ''
        Write-Host '--- PROPOSED (will write to SANDBOX only, not CBR) ---' -ForegroundColor Green
        Write-Host ("confidence: {0}  needs_human: {1}" -f $conf, $needsHuman) -ForegroundColor Yellow
        Write-Host ("title: {0}" -f $proposedTitle) -ForegroundColor White
        Write-Host ("type/id/theme/parent: {0} / {1} / {2} / {3}" -f $prop.type, $prop.id, $prop.theme, $prop.parent) -ForegroundColor DarkGray
        if ($prop.rationale) { Write-Host ("rationale: {0}" -f $prop.rationale) -ForegroundColor DarkGray }
        if ($prop.human_questions) {
            foreach ($q in @($prop.human_questions)) { if ("$q".Trim()) { Write-Host ("  ? {0}" -f $q) -ForegroundColor Yellow } }
        }
        Write-Host 'body preview:' -ForegroundColor DarkGray
        Write-Host (Get-BodyPreview $proposedDesc 1100) -ForegroundColor Gray
        Write-Host '---------------' -ForegroundColor Green
        Write-Host ''
        Write-Host '[A] Accept -> create in SANDBOX   [R] Reject -> SANDBOX human meeting   [S] Skip   [Q] Quit' -ForegroundColor Cyan
        $choice = (Read-Host 'Choice').Trim().ToUpperInvariant()
    }

    if ($choice -eq 'Q') {
        Write-Host 'Quit session.' -ForegroundColor Yellow
        break
    }
    if ($choice -eq 'S' -or $choice -eq '') {
        $stats.skipped++
        $line = @{ source_iid = $iid; action = 'skip'; title = $title; ts = (Get-Date).ToString('o') } | ConvertTo-Json -Compress
        Add-Content -Path $reportPath -Value $line -Encoding UTF8
        continue
    }

    $proposedTitle = [string]$prop.title
    if (-not $proposedTitle) { $proposedTitle = $title }
    $proposedDesc = Build-ProposedDescription -prop $prop -SourceIid $iid -SourceProject $readId

    if ($choice -eq 'R') {
        Write-Host 'Reject -> sandbox human-meeting stub' -ForegroundColor Yellow
        $stats.rejected_human++
        $qs = @($prop.human_questions) -join '; '
        $stubTitle = "REFINE-MEETING :: $proposedTitle"
        $stubDesc = @"
~~~cascade-meta
type: STORY
id: STORY-refine-cbr-$iid
source_project: $readId
source_iid: $iid
state: open
area: cascade
source: backlog-refine-reject
~~~

## Needs human backlog refinement
Source CBR project **$readId** issue **!$iid**

**Current title:** $title
**AI proposed title:** $proposedTitle
**Questions:** $qs

## Original description (CBR)
$desc
"@
        $line = @{ source_iid = $iid; action = 'reject_human'; title = $title; proposed_title = $proposedTitle; ts = (Get-Date).ToString('o') } | ConvertTo-Json -Compress
        Add-Content -Path $reportPath -Value $line -Encoding UTF8
        if ($DryRun) {
            Write-Host 'DryRun: would create sandbox stub' -ForegroundColor Yellow
        } else {
            try {
                $created = New-SandboxIssue -Title $stubTitle -Description $stubDesc -Labels @('cascade-test','refine::human-needed','cascade','area::data-crucible')
                Write-Host ("Sandbox meeting stub !{0}" -f $created.iid) -ForegroundColor Yellow
            } catch {
                Write-Host ("Sandbox create failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
                $stats.errors++
            }
        }
        continue
    }

    if ($choice -eq 'A') {
        Write-Host 'Accept -> create refined issue in SANDBOX (CBR unchanged)' -ForegroundColor Green
        $newLabs = @()
        try { $newLabs = @($prop.labels) } catch {}
        if (-not $newLabs -or $newLabs.Count -eq 0) { $newLabs = @('cascade') }
        $newLabs = @($newLabs + @('cascade-test','refine::ai-accepted','area::data-crucible') | Where-Object { $_ -ne 'refine::human-needed' } | Select-Object -Unique)
        $line = @{ source_iid = $iid; action = 'accept'; title = $proposedTitle; type = $([string]$prop.type); ts = (Get-Date).ToString('o') } | ConvertTo-Json -Compress
        Add-Content -Path $reportPath -Value $line -Encoding UTF8
        if ($DryRun) {
            Write-Host 'DryRun: would POST to sandbox' -ForegroundColor Yellow
            $stats.accepted++
            continue
        }
        try {
            $created = New-SandboxIssue -Title $proposedTitle -Description $proposedDesc -Labels $newLabs
            Write-Host ("Sandbox !{0} created from CBR !{1}" -f $created.iid, $iid) -ForegroundColor Green
            $stats.accepted++
        } catch {
            Write-Host ("Sandbox create failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
            $stats.errors++
            if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
                $null = Write-CascadeErrorLog -Operation 'BacklogRefine.Apply' -Message $_.Exception.Message -Context @{ source_iid = $iid }
            }
        }
        continue
    }

    Write-Host 'Unknown choice - skipped' -ForegroundColor Yellow
    $stats.skipped++
}

($stats | ConvertTo-Json) | Set-Content $summaryPath -Encoding UTF8
Write-Host ''
Write-Host '=== Session summary ===' -ForegroundColor Cyan
Write-Host ($stats | ConvertTo-Json)
Write-Host "Detail log: $reportPath"
Write-Host "Human meeting in sandbox: label refine::human-needed"
Write-Host ("READ={0} WRITE={1}" -f $readId, $writeId)
