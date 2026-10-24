<#
.SYNOPSIS
  SAFE WRITE: Clone source issue, apply full Story-shaped DoR body from AI
  proposals ONLY on the NEW issue. Source is never modified.

  Writes:
    - Cascade meta + visible ### Cascade mirror
    - Story text
    - Acceptance Criteria (from AI or placeholder)
    - Task checklist (in-Story, not separate Git issues)
    - Parent Feature (Cascade id)
    - PfW / DWI (Defined Work Instruction) link if AI or corpus suggests one
    - DoR readiness notes

  Labels: cascade-test + source labels. Title prefix [CASCADE-DUP].
#>
param(
    [Parameter(Mandatory)][int]$SourceIid,
    [int]$SourceProjectId = 0,
    [int]$TargetProjectId = 0,
    [string]$ResponseJson = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

function Invoke-CascadeGitWrite {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [byte[]]$BodyBytes = $null,
        [int]$TimeoutSec = 180,
        [int]$Retries = 3
    )
    $attempt = 0
    while ($true) {
        $attempt++
        try {
            if ($null -ne $BodyBytes) {
                return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers `
                    -Body $BodyBytes -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSec
            } else {
                return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec $TimeoutSec
            }
        } catch {
            $msg = $_.Exception.Message
            if ($attempt -ge $Retries) { throw }
            if ($msg -match 'timed out|timeout|unable to connect|actively refused') {
                Write-Host ("[!] GitLab {0} attempt {1}/{2} failed: {3} - retrying..." -f $Method, $attempt, $Retries, $msg) -ForegroundColor Yellow
                Start-Sleep -Seconds (3 * $attempt)
                continue
            }
            throw
        }
    }
}


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

if (-not $ResponseJson) {
    $ResponseJson = Join-Path $RepoRoot 'output\ai_response_one_dup.json'
}
if (-not (Test-Path -LiteralPath $ResponseJson)) {
    throw "AI response not found: $ResponseJson - run menu [P] first so proposals exist."
}

$raw = Get-Content -LiteralPath $ResponseJson -Raw | ConvertFrom-Json
$props = @()
if ($raw.proposals) {
    if ($raw.proposals -is [System.Array]) { $props = @($raw.proposals) } else { $props = @($raw.proposals) }
}
$analysis = $null
if ($raw.analysis) {
    if ($raw.analysis -is [System.Array]) { $analysis = $raw.analysis | Select-Object -First 1 }
    else { $analysis = $raw.analysis }
}

Write-Host '=== SAFE WRITE: full Story DoR body on DUPLICATE only ===' -ForegroundColor Cyan
Write-Host ("Source !{0} is NOT modified. New issue gets [CASCADE-DUP] + cascade-test." -f $SourceIid) -ForegroundColor Green
Write-Host 'Terminology: DoR=Definition of Ready | PfW=Proposal for Work | DWI=Defined Work Instruction'

# Guard: AI response must match source iid (avoid applying pgdump AC onto DNS ticket)
$respIid = 0
if ($analysis -and $analysis.iid) { try { $respIid = [int]$analysis.iid } catch {} }
if ($respIid -eq 0 -and $props.Count -gt 0) {
    $fp = $props[0]
    if ($fp.for_iid) { try { $respIid = [int]$fp.for_iid } catch {} }
    elseif ($fp.iid) { try { $respIid = [int]$fp.iid } catch {} }
}
if ($respIid -gt 0 -and $respIid -ne $SourceIid) {
    Write-Host ''
    Write-Host ("MISMATCH: AI response is for !{0} but you asked to apply to source !{1}." -f $respIid, $SourceIid) -ForegroundColor Red
    Write-Host 'That would put the wrong Acceptance Criteria on the duplicate.' -ForegroundColor Yellow
    if ($Force) {
        # Cluster/automation: never apply wrong iid even with -Force
        throw ("AI response iid mismatch (!{0} vs !{1}). Re-run AI for !{1}." -f $respIid, $SourceIid)
    }
    Write-Host 'Run [P] again for this source iid, then [M] with the SAME iid.' -ForegroundColor Cyan
    $cont = Read-Host 'Type FORCE to apply mismatched proposals anyway (not recommended)'
    if ($cont -ne 'FORCE') { Write-Host 'Cancelled.'; exit 0 }
}


$type = 'STORY'
$id = "STORY-$SourceIid"
$parent = ''
$state = 'OPEN'
$ac = @()
$checklist = @()
$pfwOrDwi = ''
$points = 0
$effortBand = ''
$createFeature = $null
$featureShort = ''
$featureDod = @()
$featureProposal = $null
$featureEffort = ''
$featureFte = ''
$featureProposalMd = ''
$epicOutcome = ''
$epicSuccess = @()
$epicMetrics = @()
$timeHours = 0
$titleClean = ''
$dwiMd = ''
$noteBits = @()

foreach ($p in $props) {
    $action = [string]$p.action
    if ($p.type) { $type = ([string]$p.type).ToUpper() }
    if ($p.id) { $id = [string]$p.id }
    if ($p.parent) { $parent = [string]$p.parent }
    if ($p.state) { $state = [string]$p.state }
    if ($action -match 'set_parent' -and $p.parent) { $parent = [string]$p.parent }
    if ($action -match 'normalize_meta' -and $p.meta) {
        $m = $p.meta
        if ($m.type) { $type = ([string]$m.type).ToUpper() }
        if ($m.id) { $id = [string]$m.id }
        if ($m.parent) { $parent = [string]$m.parent }
        if ($m.state) { $state = [string]$m.state }
    }
    if ($p.acceptance_criteria) {
        $list = $p.acceptance_criteria
        if ($list -is [System.Array]) { $ac = @($list | ForEach-Object { "$_" }) }
        else { $ac = @([string]$list) }
    }
    if ($p.task_checklist) {
        $list = $p.task_checklist
        if ($list -is [System.Array]) { $checklist = @($list | ForEach-Object { "$_" }) }
        else { $checklist = @([string]$list) }
    }
    if ($action -match 'link_pfw|link_dwi' -or $p.pfw_id -or $p.dwi_id -or $p.note -match 'PfW|DWI|Proposal') {
        if ($p.pfw_id) { $pfwOrDwi = [string]$p.pfw_id }
        elseif ($p.dwi_id) { $pfwOrDwi = [string]$p.dwi_id }
        elseif ($p.note) {
        $n = [string]$p.note
        if ($n -notmatch '(?i)Normalized Cascade|human must accept|drafted testable') { $noteBits += $n }
    }
    }
    if ($p.note) {
        $n = [string]$p.note
        if ($n -notmatch '(?i)Normalized Cascade|human must accept|drafted testable') { $noteBits += $n }
    }

    if ($p.points) { try { $points = [int]$p.points } catch {} }

    if ($p.time_estimate_hours) { try { $timeHours = [int]$p.time_estimate_hours } catch {} }
    if ($p.time_estimate) { try { $timeHours = [int]$p.time_estimate } catch {} }
    if ($p.title_clean) { $titleClean = [string]$p.title_clean }
    if ($p.dwi_markdown) { $dwiMd = [string]$p.dwi_markdown }
    if ($p.dwi) { $dwiMd = [string]$p.dwi }
    if ($p.effort -and $action -match 'feature|create_feature|set_parent') {
        # feature effort often on create_feature; also allow on set_parent note
    }
    if ($p.effort) { $featureEffort = [string]$p.effort }
    if ($p.fte_weeks) { $featureFte = [string]$p.fte_weeks }
    if ($p.proposal_markdown) { $featureProposalMd = [string]$p.proposal_markdown }

    if ($p.story_points) { try { $points = [int]$p.story_points } catch {} }
    if ($p.effort_band) { $effortBand = [string]$p.effort_band }
    if ($action -match 'create_feature' -or $action -eq 'create_feature') {
        $createFeature = $p
    }
}

if ($analysis) {
    if ($analysis.recommended_parent -and -not $parent) { $parent = [string]$analysis.recommended_parent }
    if ($analysis.proposed_type) { $type = ([string]$analysis.proposed_type).ToUpper() }
    if ($analysis.proposed_id) { $id = [string]$analysis.proposed_id }
}

if ($type -eq 'TASK') { $type = 'STORY' }

# Corpus hint: match proposal filenames under fixtures/proposals
$propDir = Join-Path $RepoRoot 'fixtures\proposals'
if (-not $pfwOrDwi -and (Test-Path $propDir)) {
    $names = @(Get-ChildItem $propDir -Filter '*.md' | ForEach-Object { $_.BaseName })
    $blob = (($props | ConvertTo-Json -Depth 6) + ' ' + $parent + ' ' + $id).ToLowerInvariant()
    foreach ($n in $names) {
        $key = ($n -replace '_', ' ').ToLowerInvariant()
        $tokens = $key.Split(' ') | Where-Object { $_.Length -gt 4 }
        $hits = 0
        foreach ($tok in $tokens) { if ($blob -match [regex]::Escape($tok)) { $hits++ } }
        if ($hits -ge 2 -or ($n -match 'PGDUMP|pgdump' -and $blob -match 'pgdump|postgres')) {
            $pfwOrDwi = "fixtures/proposals/$n.md (PfW/DWI corpus)"
            break
        }
    }
}
if (-not $pfwOrDwi -and $parent -match 'postgres|data-lake|bronze') {
    if (Test-Path (Join-Path $propDir 'Automate_Daily_PGDUMP_Backups.md')) {
        $pfwOrDwi = 'fixtures/proposals/Automate_Daily_PGDUMP_Backups.md (PfW/DWI corpus - review & accept)'
    }
}

if ($points -eq 0 -and $ac.Count -gt 0) { $points = 3 }
if ($timeHours -eq 0 -and $points -gt 0) {
    # conservative hours from points (planning default)
    $map = @{ 1 = 2; 2 = 4; 3 = 8; 5 = 16; 8 = 24; 13 = 40 }
    if ($map.ContainsKey($points)) { $timeHours = [int]$map[$points] } else { $timeHours = 8 }
}
if ($checklist.Count -eq 0) {
    $checklist = @(
        'Research / discover',
        'Design (short note when non-obvious)',
        'Implement',
        'Test in TEST',
        'Peer review',
        'Document (as applicable)',
        'Promote / go live (verify + monitor)'
    )
}

Write-Host ("type={0} parent={1} AC={2} checklist={3} points={4} effort={5}" -f $type, $parent, $ac.Count, $checklist.Count, $points, $effortBand)
Write-Host ("PfW/DWI: {0}" -f $(if ($pfwOrDwi) { $pfwOrDwi } else { '(none linked - add before Feature/Story Ready if required)' }))

function Get-LocalBase {
    param([string]$ProjectIdOverride = '')
    Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
    $url = $env:GITLAB_URL
    if ([string]::IsNullOrWhiteSpace($url)) { $url = 'https://web.git.mil' }
    $projectId = $ProjectIdOverride
    if ([string]::IsNullOrWhiteSpace($projectId)) { $projectId = $env:GITLAB_PROJECT_ID }
    if ([string]::IsNullOrWhiteSpace($projectId)) { throw 'GITLAB_PROJECT_ID not set' }
    if ($projectId -match '^\d+$') { return ($url.TrimEnd('/') + '/api/v4/projects/' + $projectId) }
    return ($url.TrimEnd('/') + '/api/v4/projects/' + [uri]::EscapeDataString($projectId))
}
function Get-LocalHeaders {
    $tok = Get-CascadeGitLabToken -AllowPrompt
    return @{ 'PRIVATE-TOKEN' = $tok; 'Content-Type' = 'application/json'; 'Accept' = 'application/json' }
}

# Read source from SourceProjectId (or GITLAB_SOURCE_PROJECT_ID / same as write)
# Write dups to TargetProjectId (or GITLAB_PROJECT_ID)
$writePid = if ($TargetProjectId -gt 0) { [string]$TargetProjectId } else { $env:GITLAB_PROJECT_ID }
$readPid = if ($SourceProjectId -gt 0) { [string]$SourceProjectId } elseif ($env:GITLAB_SOURCE_PROJECT_ID) { $env:GITLAB_SOURCE_PROJECT_ID } else { $writePid }
$base = Get-LocalBase -ProjectIdOverride $writePid
$baseRead = Get-LocalBase -ProjectIdOverride $readPid
$headers = Get-LocalHeaders
Write-Host ("GitLab READ={0} WRITE={1}" -f $readPid, $writePid) -ForegroundColor DarkGray

$src = Invoke-RestMethod -Method Get -Uri "$baseRead/issues/$SourceIid" -Headers $headers -TimeoutSec 180
$labs = @()
try { if ($src.labels -is [System.Array]) { $labs = @($src.labels | ForEach-Object { "$_" }) } } catch {}
if ($labs -notcontains 'cascade-test') { $labs += 'cascade-test' }

$baseTitle = if ($titleClean) { $titleClean } else { $src.title }
# Light title cleanup if AI omitted title_clean
if (-not $titleClean) {
    $baseTitle = ($baseTitle -replace '\s+', ' ').Trim()
    $baseTitle = $baseTitle.Substring(0,1).ToUpper() + $baseTitle.Substring(1)
}
$dupTitle = 'STORY :: {0}' -f $baseTitle


# Resolve parent epic from catalog; optionally create missing Feature as CASCADE-DUP shell
$parentGitIid = ''
$parentEpic = ''
$catalogPath = Join-Path $RepoRoot 'fixtures\hierarchy_catalog.json'
if ((Test-Path $catalogPath) -and $parent) {
    try {
        $cat = Get-Content $catalogPath -Raw | ConvertFrom-Json
        $feat = @($cat.features) | Where-Object { $_.id -eq $parent } | Select-Object -First 1
        if ($feat) {
            if ($feat.epic) { $parentEpic = [string]$feat.epic }
            Write-Host ("Catalog Feature '{0}' under {1}" -f $parent, $parentEpic) -ForegroundColor DarkGray
        }
    } catch {}
}

# If AI asked create_feature OR parent has no Git issue, offer Feature shell on cascade-test
$wantFeatureShell = $false
if ($createFeature) { $wantFeatureShell = $true }
if ($parent -and -not $parentGitIid) { $wantFeatureShell = $true }

$featureShellIid = 0
if ($wantFeatureShell -and $parent) {
    Write-Host ''
    Write-Host ("Parent '{0}' is catalog-only (no GitLab Feature issue yet)." -f $parent) -ForegroundColor Yellow
    Write-Host 'Create a CASCADE-DUP Feature SHELL (safe, labeled cascade-test) so hierarchy exists in Git?' -ForegroundColor Cyan
    $ynF = 'N'
    if (-not $Force) {
        $ynF = Read-Host '  Type YES to create Feature shell, or Enter to skip'
    } else { $ynF = 'YES' }
    if ($ynF -eq 'YES') {
        $featLabel = $parent
        if ($parent -match '^FEATURE-') {
            $featLabel = ($parent -replace '^FEATURE-','')
            $featLabel = ($featLabel -replace '-',' ')
            $featLabel = (Get-Culture).TextInfo.ToTitleCase($featLabel.ToLower())
        }
        if ($createFeature -and $createFeature.title) { $featLabel = [string]$createFeature.title }
        $fTitle = 'FEATURE :: {0}' -f $featLabel
        # Humanize feature title
        if ($createFeature -and $createFeature.title) { $fTitle = 'FEATURE :: {0}' -f $createFeature.title }
        if (-not $featureShort) {
            $featureShort = "Delivers $parent under $parentEpic for the associated delivery Stories."
        }
        if (-not $featureEffort) { $featureEffort = 'Medium' }

# Normalize feature effort words
if ($featureEffort) {
    $fe = $featureEffort.Trim()
    switch -Regex ($fe) {
        '^(?i)XS|extra\s*small$' { $featureEffort = 'Extra Small' }
        '^(?i)S|small$' { $featureEffort = 'Small' }
        '^(?i)M|medium$' { $featureEffort = 'Medium' }
        '^(?i)L|large$' { $featureEffort = 'Large' }
        '^(?i)XL|extra\s*large$' { $featureEffort = 'Extra Large' }
    }
}

        $dodLines = ''
        if ($featureDod.Count -gt 0) {
            $dodLines = ($featureDod | ForEach-Object { "- [ ] $_" }) -join "`n"
        } else {
            $dodLines = @"
- [ ] Proposal for Work accepted with name and date
- [ ] Child Stories parented with points and time estimates
- [ ] Primary path verified in TEST
- [ ] Production promotion only through agreed gate
"@
        }
        if (-not $featureProposalMd) {
            $featureProposalMd = @"
### Executive summary
Feature **$parent** under **$parentEpic** is the parent capability for Story !$SourceIid. Context is limited to a single Story; expand this Proposal when more child Stories exist or the Technical Focal adds architecture notes.

### Problem
The delivery gap described by Story !$SourceIid is not yet packaged as a Feature-level capability with clear scope, options, and verification.

### Options
1. **Proceed incremental** - deliver via child Stories under this Feature; TEST then gated PROD.
2. **Research first** - if approach is unclear, open a Research Story before build Stories.
3. **Defer** - park Feature if capacity or dependencies (OPS/IAM) block progress.

### Recommended option
Option 1 unless acceptance criteria cannot be met without further discovery.

### Scope in
- Work required to satisfy child Story acceptance criteria for $parent
- TEST verification and peer review

### Scope out
- Unrelated medallion layers
- Standing maintenance outside this Feature
- Production changes without gate

### Technical approach
Derive implementation from child Stories. Preferred stack and scripts should be filled by the Technical Focal when known (repo paths, IaC, ETL job names).

### Risks
- Insufficient Story context for a full architecture decision
- OPS or IAM latency
- Scope creep across medallion layers


### Open questions (owner to resolve)
- Confirm Technical Focal and SME
- Confirm target environments and data classification handling
- Attach or link detailed design if this Feature becomes multi-sprint
"@
        }

        $childBody = "- Source Story: [#$SourceIid]($($src.web_url))"
        $fDesc = @"
## Feature
**Id:** $parent

## Artifacts
Proposal for Work

## Short description
$featureShort

## Effort
$featureEffort$(if ($featureFte) { " | FTE weeks: $featureFte" } else { '' })

## Definition of Done
$dodLines

## Parent
- Epic: **$parentEpic**$(if ($epicGitIid) { " (!$epicGitIid)" } else { '' })

## Children
- Story !$SourceIid

## Ownership
- **Product Owner:** Cascade AI
- **Technical Focal:** (unassigned)
- **SME:** (unassigned)

## Proposal for Work
$featureProposalMd
"@
        $fMeta = @{
            type = 'FEATURE'
            id = $parent
            parent = $parentEpic
            state = 'OPEN'
            artifact_level = 'Proposal for Work'
            effort = $featureEffort
            child_iids = [string]$SourceIid
            children = ("!{0}" -f $SourceIid)
        }
        if ($featureFte) { $fMeta['fte_weeks'] = $featureFte }
        if ($epicGitIid) { $fMeta['parent_iid'] = $epicGitIid }

        $fFull = Build-CascadeDescriptionWithMirror -Meta $fMeta -BodyMarkdown $fDesc
        $fLabs = @('cascade-test')
        if ($src.labels -is [System.Array]) {
            foreach ($lb in $src.labels) {
                $s = "$lb"
                if ($s -match 'area::' -and $fLabs -notcontains $s) { $fLabs += $s }
            }
        }
        $fPayload = @{ title = $fTitle; description = $fFull; labels = ($fLabs -join ',') } | ConvertTo-Json -Depth 5
        $fCreated = Invoke-RestMethod -Method Post -Uri "$base/issues" -Headers $headers `
            -Body ([Text.Encoding]::UTF8.GetBytes($fPayload)) `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 180
        $featureShellIid = [int]$fCreated.iid
        $parentGitIid = [string]$featureShellIid
        Write-Host ("Created Feature shell !{0} {1}" -f $featureShellIid, $fCreated.web_url) -ForegroundColor Green
    }
}


Write-Host ''

# Epic shell if catalog epic has no GitLab issue
$epicGitIid = ''
if ($parentEpic) {
    Write-Host ''
    Write-Host ("Epic '{0}' is catalog-only (no GitLab Epic issue yet)." -f $parentEpic) -ForegroundColor Yellow
    Write-Host 'Create a CASCADE-DUP Epic SHELL (safe, cascade-test)?' -ForegroundColor Cyan
    $ynE = 'N'
    if (-not $Force) {
        $ynE = Read-Host '  Type YES to create Epic shell, or Enter to skip'
    } else { $ynE = 'YES' }
    if ($ynE -eq 'YES') {
        $epicLabel = $parentEpic
        if ($parentEpic -match '^EPIC-') {
            $epicLabel = ($parentEpic -replace '^EPIC-','')
            $epicLabel = ($epicLabel -replace '-',' ')
            $epicLabel = (Get-Culture).TextInfo.ToTitleCase($epicLabel.ToLower())
        }
        $eTitle = 'EPIC :: {0}' -f $epicLabel
        if (-not $epicOutcome) {
            $epicOutcome = "$parentEpic delivers its roadmap outcomes through child Features, starting with $parent related to Story !$SourceIid."
        }
        $spLines = ''
        if ($epicSuccess.Count -gt 0) {
            $spLines = ($epicSuccess | ForEach-Object { "- [ ] $_" }) -join "`n"
        } else {
            $spLines = @"
- [ ] Child Features are parented and status-visible
- [ ] Active Features have Stories with points for planning rollup
- [ ] Leadership can read health without opening Stories
"@
        }
        $mLines = ''
        if ($epicMetrics.Count -gt 0) {
            $mLines = ($epicMetrics | ForEach-Object { "- $_" }) -join "`n"
        } else {
            $mLines = @"
- Active Features (count)
- Features completed in last 3 sprints (count)
- Open blockers older than 5 business days (count)
"@
        }
        $eDesc = @"
## Epic
**Id:** $parentEpic

## Outcome
$epicOutcome

## Success picture
$spLines

## Signals
$mLines

## Children
- Feature **$parent**$(if ($featureShellIid -gt 0) { " (!$featureShellIid)" } else { '' })
- Story !$SourceIid

## Ownership
- **Product Owner:** Cascade AI
- **Technical Focal:** (unassigned)
- **SME:** (unassigned)
"@
        $eMeta = @{
            type = 'EPIC'
            id = $parentEpic
            parent = ''
            state = 'OPEN'
            children = $parent
            child_iids = $(if ($featureShellIid -gt 0) { [string]$featureShellIid } else { '' })
            lifecycle = 'BETA'
            health = 'ON_TRACK'
        }

        $eFull = Build-CascadeDescriptionWithMirror -Meta $eMeta -BodyMarkdown $eDesc
        $eLabs = @('cascade-test')
        if ($src.labels -is [System.Array]) {
            foreach ($lb in $src.labels) {
                $s = "$lb"
                if ($s -match 'area::' -and $eLabs -notcontains $s) { $eLabs += $s }
            }
        }
        $ePayload = @{ title = $eTitle; description = $eFull; labels = ($eLabs -join ',') } | ConvertTo-Json -Depth 5
        $eCreated = Invoke-RestMethod -Method Post -Uri "$base/issues" -Headers $headers `
            -Body ([Text.Encoding]::UTF8.GetBytes($ePayload)) `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 180
        $epicGitIid = [string]([int]$eCreated.iid)
        Write-Host ("Created Epic shell !{0} {1}" -f $epicGitIid, $eCreated.web_url) -ForegroundColor Green
        # If Feature shell already exists, patch its parent_iid (best-effort)
        if ($featureShellIid -gt 0 -and $epicGitIid) {
            try {
                $fIssue = Invoke-RestMethod -Method Get -Uri ("$base/issues/{0}" -f $featureShellIid) -Headers $headers -TimeoutSec 120
                $fMeta2 = @{ type = 'FEATURE'; id = $parent; parent = $parentEpic; parent_iid = $epicGitIid; state = 'OPEN'; artifact_level = 'Proposal for Work' }
                $fBodyKeep = [string]$fIssue.description
                $fFull2 = Build-CascadeDescriptionWithMirror -Meta $fMeta2 -BodyMarkdown $fBodyKeep
                $patch = @{ description = $fFull2 } | ConvertTo-Json -Depth 5
                $null = Invoke-RestMethod -Method Put -Uri ("$base/issues/{0}" -f $featureShellIid) -Headers $headers `
                    -Body ([Text.Encoding]::UTF8.GetBytes($patch)) `
                    -ContentType 'application/json; charset=utf-8' -TimeoutSec 180
                Write-Host ("Updated Feature shell !{0} parent_iid -> Epic !{1}" -f $featureShellIid, $epicGitIid) -ForegroundColor Green
            } catch {
                Write-Host ("[!] Could not link Feature shell to Epic: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
            }
        }
    }
}

Write-Host 'Will CREATE and WRITE full Story DoR body on a NEW issue only:' -ForegroundColor Yellow
Write-Host ("  title : {0}" -f $dupTitle)
Write-Host '  sections: Story Text | Acceptance Criteria | Task checklist | Parent | PfW/DWI | DoR notes'
if (-not $Force) {
    $yn = Read-Host '  Type YES to create+write (source unchanged; delete new issue when done)'
    if ($yn -ne 'YES') { Write-Host 'Cancelled.'; exit 0 }
}

$stub = "Source !$SourceIid`n`n$($src.description)"
$payload = @{ title = $dupTitle; description = $stub; labels = ($labs -join ',') } | ConvertTo-Json -Depth 5
$created = Invoke-CascadeGitWrite -Method Post -Uri "$base/issues" -Headers $headers `
    -BodyBytes ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 180 -Retries 3
$newIid = [int]$created.iid
Write-Host ("Created !{0}" -f $newIid) -ForegroundColor Green

$id = ($id -replace [regex]::Escape([string]$SourceIid), [string]$newIid)
if ($id -notmatch [string]$newIid) { $id = "STORY-$newIid" }

$meta = @{
    type       = $type
    id         = $id
    parent     = $(if ($parent) { $parent } else { '' })
    parent_iid = $(if ($parentGitIid) { $parentGitIid } else { '' })
    state      = $state
}
if ($pfwOrDwi) { $meta['dwi_ref'] = $pfwOrDwi }
if ($points -gt 0) { $meta['points'] = $points }
if ($timeHours -gt 0) { $meta['time_estimate_hours'] = $timeHours }

$acMd = if ($ac.Count -gt 0) {
    ($ac | ForEach-Object { '- [ ] {0}' -f $_ }) -join "`n"
} else {
    '- [ ] **MISSING** - add testable criteria before Ready (DoR)'
}
$checkMd = ($checklist | ForEach-Object { '- [ ] {0}' -f $_ }) -join "`n"
$dorReady = if ($parent -and $ac.Count -gt 0) { 'STRUCTURALLY READY - parent + AC present' } else { 'NOT READY - missing parent and/or AC' }

if (-not $dwiMd) {
    $dwiMd = @"
### Purpose
Implement Story work under Feature $parent so all acceptance criteria pass.

### Steps
1. Review parent Feature and constraints.
2. Implement the change.
3. Test in TEST against acceptance criteria.
4. Peer review.
5. Document operational impact.

### Environments
TEST before any PROD promote.

### Verification
Acceptance criteria satisfied; evidence linked.

### Rollback
Revert or restore prior path on failure.
"@
}

$acMd = (($ac | ForEach-Object { '* {0}' -f $_ }) -join "`n")
if (-not $acMd) { $acMd = '* (none - add before Ready)' }
$taskMd = (($checklist | ForEach-Object { '* [ ] {0}' -f $_ }) -join "`n")
if (-not $taskMd) { $taskMd = '* [ ] Implement`n* [ ] Test in TEST`n* [ ] Peer review' }

$parentLine = if ($parent) { '**{0}**{1}' -f $parent, $(if ($parentGitIid) { " (!$parentGitIid)" } else { '' }) } else { '(none - set before Ready)' }
$childLine = '(none - Tasks stay in-checklist unless promoted to child work items)'
$effortStory = @"
- **Story points:** $(if ($points -gt 0) { $points } else { 'unset' })
- **Weight (GitLab):** $(if ($points -gt 0) { $points } else { 'unset' }) (same as story points)
- **Hours (planning):** $(if ($timeHours -gt 0) { "$timeHours" } else { 'unset' })
"@

$storyBody = @"
## 1. Story
**Title:** $baseTitle

$($src.description)

## 2. Acceptance Criteria
$acMd

## 3. Tasks
$taskMd

## 4. Parent
$parentLine

## 5. Children
$childLine

## 6. Effort
$effortStory

## 7. Defined Work Instructions (DWI)
$dwiMd

## 8. Definition of Ready (DoR)
- **Status:** $dorReady

## 9. Ownership
- **Product Owner:** Cascade AI
- **Technical Focal:** (unassigned)
- **SME:** (unassigned)
"@

$meta = @{
    type = 'STORY'
    id = ('STORY-{0}' -f $newIid)
    parent = $parent
    parent_iid = $(if ($parentGitIid) { $parentGitIid } else { '' })
    state = 'OPEN'
    points = $points
    time_estimate_hours = $timeHours
    product_owner = 'Cascade AI'
    technical_focal = ''
    sme = ''
}
if ($pfwOrDwi) { $meta['dwi_ref'] = $pfwOrDwi }

$newDesc = Build-CascadeDescriptionWithMirror -Meta $meta -BodyMarkdown $storyBody
$update = @{ title = $dupTitle; description = $newDesc } | ConvertTo-Json -Depth 5
$null = Invoke-CascadeGitWrite -Method Put -Uri "$base/issues/$newIid" -Headers $headers `
    -BodyBytes ([Text.Encoding]::UTF8.GetBytes($update)) -TimeoutSec 180 -Retries 3

# GitLab effort fields:
# - weight = Story points (works on issues/work items; shown as Weight)
# - time_estimate may be unavailable on some work-item UIs / instances
if ($points -gt 0) {
    try {
        $wBody = @{ weight = [int]$points } | ConvertTo-Json
        $null = Invoke-CascadeGitWrite -Method Put -Uri ("$base/issues/{0}" -f $newIid) -Headers $headers `
            -BodyBytes ([Text.Encoding]::UTF8.GetBytes($wBody)) -TimeoutSec 120 -Retries 2
        Write-Host ("GitLab weight (story points) set: {0}" -f $points) -ForegroundColor DarkGray
    } catch {
        Write-Host ("[!] GitLab weight not set: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
    }
}
if ($timeHours -gt 0) {
    $dur = '{0}h' -f [math]::Max(1, [int]$timeHours)
    $teUri = "{0}/issues/{1}/time_estimate" -f $base, $newIid
    try {
        $null = Invoke-RestMethod -Method Post -Uri $teUri -Headers $headers `
            -Body @{ duration = $dur } -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 90
        Write-Host ("GitLab time_estimate set: {0} (if UI supports it)" -f $dur) -ForegroundColor DarkGray
    } catch {
        Write-Host ("[!] time_estimate API not available on this instance (use Weight + body hours). {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
    }
}

Write-Host '=== DONE (safe dup) ===' -ForegroundColor Green
Write-Host ("NEW issue !{0}" -f $newIid)
Write-Host $created.web_url
Write-Host 'Open in browser: confirm sections 1-6 + ### Cascade. Then delete/close when finished.'
Write-Host ("Source !{0} was NOT changed." -f $SourceIid) -ForegroundColor Green
$newIid | Set-Content (Join-Path $RepoRoot 'output\last_dup_iid.txt') -Encoding UTF8
