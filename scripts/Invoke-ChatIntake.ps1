<#
.SYNOPSIS
  Conversational work intake: describe work -> AI structures it -> create in GitLab.
  Honors CASCADE_WRITE_MODE (sandbox|production).
#>
param(
    [string]$Message = '',
    [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
    [string]$ParentFeatureId = '',
    [string]$ParentEpicId = '',
    [switch]$Force,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
trap {
    $msg = $_.Exception.Message
    Write-Host ("ChatIntake ERROR: {0}" -f $msg) -ForegroundColor Red
    if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
        $null = Write-CascadeErrorLog -Operation 'ChatIntake' -Message $msg -Exception $msg -Detail ($_ | Out-String)
    } else {
        $od = Join-Path (Split-Path $PSScriptRoot -Parent) 'output'
        if (-not (Test-Path $od)) { New-Item -ItemType Directory -Path $od -Force | Out-Null }
        ("[{0}] ChatIntake: {1}`n{2}" -f (Get-Date), $msg, ($_ | Out-String)) | Set-Content (Join-Path $od 'cascade_last_error.txt') -Encoding UTF8
        Write-Host ("Error log -> {0}" -f (Join-Path $od 'cascade_last_error.txt')) -ForegroundColor Yellow
    }
    break
}

$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
$wp = Join-Path $RepoRoot 'src\Cascade\WritePolicy.ps1'
if (Test-Path $wp) { . $wp }
$tp = Join-Path $RepoRoot 'src\Cascade\Taxonomy.ps1'
if (Test-Path $tp) { . $tp }

Set-Location $RepoRoot

$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^=#]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"')
        }
    }
}

Write-Host '=== Cascade Chat Intake ===' -ForegroundColor Cyan
$ctx = Write-CascadeWriteBanner
$null = Assert-CascadeWriteTarget -ProjectId $ctx.WriteProjectId

if (-not $Message) {
    Write-Host 'Describe the unit of work.' -ForegroundColor Yellow
    Write-Host '  - Type or paste multiple lines' -ForegroundColor DarkGray
    Write-Host '  - Finish with a single line containing only:  .' -ForegroundColor DarkGray
    Write-Host '  - (Empty line also ends if you already typed content)' -ForegroundColor DarkGray
    $lines = @()
    while ($true) {
        $line = Read-Host
        if ($null -eq $line) { break }
        if ($line -eq '.') { break }
        if ($line -eq '' -and $lines.Count -gt 0) { break }
        if ($line -eq '' -and $lines.Count -eq 0) { continue }
        $lines += $line
    }
    $Message = $lines -join "`n"
}
if ([string]::IsNullOrWhiteSpace($Message)) { throw 'No message provided' }

Write-Host ''
Write-Host 'Message:' -ForegroundColor DarkGray
Write-Host $Message

# Forced type from user text
$forcedType = $null
if ($Message -match '(?i)\bcreate\s+an?\s+THEME\b|\btype\s*:\s*THEME\b') {
    $forcedType = 'THEME'
} elseif ($Message -match '(?i)\bcreate\s+an?\s+EPIC\b|\btype\s*:\s*EPIC\b|\b\(not a Story\).*EPIC|\bEPIC\s*\(not') {
    $forcedType = 'EPIC'
} elseif ($Message -match '(?i)\bcreate\s+an?\s+FEATURE\b|\btype\s*:\s*FEATURE\b') {
    $forcedType = 'FEATURE'
} elseif ($Message -match '(?i)\bcreate\s+an?\s+STORY\b|\btype\s*:\s*STORY\b') {
    $forcedType = 'STORY'
}

$sys = @'
You are a JSON API for Cascade backlog intake. You are NOT a chat assistant.

CRITICAL RULES:
1. Reply with ONLY one JSON object. No markdown. No tables. No prose. No code fences.
2. Do not say "Key Takeaway" or "I have created" or ask follow-up questions.
3. First character must be { and last must be }.

Schema:
{
  "type": "EPIC" or "FEATURE" or "STORY",
  "title": "short title without type prefix",
  "user_story": "As a ... I need ... so that ...",
  "acceptance_criteria": ["criterion 1", "criterion 2"],
  "parent_feature_id": "",
  "parent_epic_id": "",
  "area": "cascade",
  "effort": "Small" or "Medium" or "Large" (FEATURE only),
  "points": "1|2|3|5|8|13" (STORY only),
  "sprint": "Sprint label e.g. 2026-S21" (STORY),
  "theme": "THEME-process-improvement",
  "milestone": "",
  "labels": ["cascade"],
  "rationale": "one short sentence"
}

If user requests EPIC, type must be "EPIC".
If user requests FEATURE, type must be "FEATURE".
Hierarchy: THEME > EPIC > FEATURE > STORY.
Feature effort: Small|Medium|Large only (field name effort).
Story: points + sprint (2-week sprints). Long bake stays on Epic; Story is a finished slice.
Tasks: checklist inside Story, not separate issues by default.
Milestone optional association, not parent.
'@

$user = @"
Return JSON only. No markdown.

User work request:
$Message

Hints:
- parent_feature_id preference: $ParentFeatureId
- parent_epic_id preference: $ParentEpicId
- forced_type: $forcedType
- If forced_type is set, type field MUST equal forced_type
"@

$messages = @(
    @{ role = 'system'; content = $sys }
    @{ role = 'user'; content = $user }
)

Write-Host ''
Write-Host ("Calling LLM ({0}) to structure work item..." -f $Profile) -ForegroundColor Cyan
$chat = Invoke-CascadeChat -Profile $Profile -Messages $messages -MaxTokens 2048
$text = $null
if ($chat.Content) { $text = [string]$chat.Content }
elseif ($chat -is [string]) { $text = $chat }
else {
    try { $text = [string]$chat.choices[0].message.content } catch { }
    if (-not $text) { try { $text = [string]$chat } catch { $text = '' } }
}

$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$text | Set-Content (Join-Path $outDir 'chat_intake_raw.txt') -Encoding UTF8

$parsed = $null
try {
    $parsed = ConvertFrom-CascadeLlmJson -Text $text
} catch { $parsed = $null }
if (-not $parsed -and $text) {
    $clean = $text.Trim()
    if ($clean -match '(?s)```(?:json)?\s*(\{.*?\})\s*```') { $clean = $Matches[1] }
    elseif ($clean -match '(?s)(\{.*\})') { $clean = $Matches[1] }
    try { $parsed = $clean | ConvertFrom-Json } catch { $parsed = $null }
}

# Markdown / prose fallback (Gemini often returns tables instead of JSON)
if (-not $parsed -and $text) {
    $mdTitle = $null
    $mdType = $forcedType
    if ($text -match '(?im)\|\s*\*\*Title\*\*\s*\|\s*([^|]+)\|') {
        $mdTitle = $Matches[1].Trim()
    }
    elseif ($text -match '(?im)\|\s*Title\s*\|\s*([^|]+)\|') {
        $mdTitle = $Matches[1].Trim()
    }
    elseif ($text -match '(?im)\*\*Title\*\*\s*[|:]\s*([^\n\r|]+)') {
        $mdTitle = $Matches[1].Trim()
    }
    if ($text -match '(?im)\|\s*\*\*Type\*\*\s*\|\s*([^|]+)\|') {
        $tv = $Matches[1]
        if ($tv -match '(?i)EPIC') { $mdType = 'EPIC' }
        elseif ($tv -match '(?i)FEATURE') { $mdType = 'FEATURE' }
        elseif ($tv -match '(?i)STORY') { $mdType = 'STORY' }
    }
    $mdAc = @()
    foreach ($ln in ($text -split "`n")) {
        if ($ln -match '^\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|') {
            $c1 = $Matches[1].Trim()
            if ($c1 -match '(?i)GitLab Synchronization|Parsing Precision|Write Isolation|Hierarchy|Operational|Epic exists|Child Feature') {
                $mdAc += ($c1 + ': ' + $Matches[2].Trim())
            }
        }
        if ($ln -match '^\s*-\s+\[ \]\s+(.+)') { $mdAc += $Matches[1].Trim() }
        if ($ln -match '^\s*-\s+(.+)' -and $ln -match '(?i)Epic exists|Child Feature|sandbox|cascade-meta') {
            $mdAc += $Matches[1].Trim()
        }
    }
    if ($mdTitle -or $mdType) {
        Write-Host 'Recovered structure from markdown response' -ForegroundColor Cyan
        $parsed = [pscustomobject]@{
            type = $(if ($mdType) { $mdType } else { 'STORY' })
            title = $(if ($mdTitle) { $mdTitle } else { 'Cascade work item' })
            user_story = "As a stakeholder, I need $($mdTitle) so that Cascade delivery advances."
            acceptance_criteria = $(if ($mdAc.Count -gt 0) { $mdAc } else { @('Work item created with cascade-meta', 'Reviewed by human') })
            parent_feature_id = $ParentFeatureId
            parent_epic_id = $ParentEpicId
            area = 'cascade'
            effort_band = 'Large'
            labels = @('cascade')
            rationale = 'Recovered from markdown LLM response'
        }
        if (Get-Command Write-CascadeInfoLog -ErrorAction SilentlyContinue) {
            Write-CascadeInfoLog -Operation 'ChatIntake' -Message "Markdown recovery title=$mdTitle type=$mdType"
        }
    }
}

# Fallback if LLM fails: still create a shell from the message
if (-not $parsed) {

    Write-Host 'LLM JSON parse failed - using local fallback structure' -ForegroundColor Yellow
    $rawPreview = ''
    if (Test-Path (Join-Path $outDir 'chat_intake_raw.txt')) {
        $rawPreview = Get-Content (Join-Path $outDir 'chat_intake_raw.txt') -Raw
    }
    if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
        $null = Write-CascadeErrorLog -Operation 'ChatIntake' -Message 'LLM JSON parse failed; using fallback structure' -Detail $rawPreview -Context @{
            profile = $Profile
            write_project = $ctx.WriteProjectId
            mode = $ctx.Mode
            forced_type = "$forcedType"
        }
    }
    $parsed = [pscustomobject]@{
        type = $(if ($forcedType) { $forcedType } else { 'STORY' })
        title = (($Message -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 1)
        user_story = "As a stakeholder, I need the work described so that Cascade product delivery advances."
        acceptance_criteria = @('Work item created in GitLab with cascade-meta', 'Reviewed by human')
        parent_feature_id = $ParentFeatureId
        parent_epic_id = $ParentEpicId
        area = 'cascade'
        effort_band = 'Medium'
        labels = @('cascade')
        rationale = 'Fallback without LLM parse'
    }
}

if ($forcedType) {
    try { $parsed.type = $forcedType } catch {
        $parsed | Add-Member -NotePropertyName type -NotePropertyValue $forcedType -Force
    }
}

($parsed | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $outDir 'chat_intake_parsed.json') -Encoding UTF8

$type = 'STORY'
try { if ($parsed.type) { $type = ([string]$parsed.type).ToUpperInvariant() } } catch {}
if ($forcedType) { $type = $forcedType }

$titleCore = [string]$parsed.title
# Prefer explicit Title: line from user message
foreach ($ln in ($Message -split "`n")) {
    if ($ln -match '(?i)^\s*Title\s*:\s*(.+)$') {
        $titleCore = $Matches[1].Trim()
        break
    }
}
# Drop instruction-like titles from fallback
if (-not $titleCore -or $titleCore -match '(?i)^create an?\s+(EPIC|FEATURE|STORY)') {
    $titleCore = (($Message -split "`n") | Where-Object {
        $x = $_.Trim()
        $x -and ($x -notmatch '(?i)^create an') -and ($x -notmatch '(?i)^title\s*:') -and ($x -notmatch '(?i)^acceptance') -and ($x -notmatch '^-')
    } | Select-Object -First 1)
}
if (-not $titleCore) { $titleCore = 'Cascade Agentic Backlog Operator' }
$titleCore = $titleCore.Trim() -replace '^#+\s*','' -replace '^(Title|TITLE)\s*:\s*',''
# If parsed title is still the instruction, override
if ($titleCore -match '(?i)^create an?\s+(EPIC|FEATURE|STORY)') {
    $titleCore = 'Cascade Agentic Backlog Operator'
}

function Format-IntakeTitle([string]$Type, [string]$Core) {
    $t = $Core.Trim()
    $t = $t -replace '^(THEME|EPIC|FEATURE|STORY)\s*::\s*',''
    if ($Type -eq 'THEME') { return "THEME :: $t" }
    if ($Type -eq 'EPIC') { return "EPIC :: $t" }
    if ($Type -eq 'FEATURE') { return "FEATURE :: $t" }
    return "STORY :: $t"
}
$title = Format-IntakeTitle $type $titleCore

$ac = @()
try {
    if ($parsed.acceptance_criteria) {
        foreach ($a in @($parsed.acceptance_criteria)) {
            if ("$a".Trim()) { $ac += "- [ ] $($a.ToString().Trim() -replace '^- \[ \]\s*','')" }
        }
    }
} catch {}
if ($ac.Count -eq 0) { $ac = @('- [ ] Acceptance criteria to be refined') }

$fid = $ParentFeatureId
$eid = $ParentEpicId
try { if ($parsed.parent_feature_id) { $fid = [string]$parsed.parent_feature_id } } catch {}
try { if ($parsed.parent_epic_id) { $eid = [string]$parsed.parent_epic_id } } catch {}

$area = 'cascade'
try { if ($parsed.area) { $area = [string]$parsed.area } } catch {}
$effort = 'Medium'
try { if ($parsed.effort_band) { $effort = [string]$parsed.effort_band } } catch {}
$userStory = ''
try { if ($parsed.user_story) { $userStory = [string]$parsed.user_story } } catch {}
if (-not $userStory) { $userStory = "As a stakeholder, I need $titleCore so that delivery outcomes improve." }

$idSlug = ($titleCore -replace '[^A-Za-z0-9]+','-').Trim('-')
if ($idSlug.Length -gt 40) { $idSlug = $idSlug.Substring(0,40).Trim('-') }
$catalogId = "${type}-${idSlug}"

$themeId = ''
try { if ($parsed.theme) { $themeId = [string]$parsed.theme } } catch {}
if (-not $themeId -and $type -eq 'EPIC') { $themeId = 'THEME-process-improvement' }
$pointsVal = ''
try { if ($parsed.points) { $pointsVal = [string]$parsed.points } } catch {}
$sprintVal = ''
try { if ($parsed.sprint) { $sprintVal = [string]$parsed.sprint } } catch {}
$milestoneVal = ''
try { if ($parsed.milestone) { $milestoneVal = [string]$parsed.milestone } } catch {}
$effortVal = $effort
try { if ($parsed.effort) { $effortVal = [string]$parsed.effort } } catch {}
if ($effortVal -match '(?i)extra') { $effortVal = 'Large' }
if ($type -eq 'FEATURE' -and $effortVal -notmatch '^(Small|Medium|Large)$') { $effortVal = 'Medium' }
if ($type -ne 'FEATURE') { $effortVal = '' }

$metaLines = [System.Collections.Generic.List[string]]::new()
[void]$metaLines.Add('~~~cascade-meta')
[void]$metaLines.Add("type: $type")
[void]$metaLines.Add("id: $catalogId")
[void]$metaLines.Add($(if ($themeId) { "theme: $themeId" } else { 'theme:' }))
[void]$metaLines.Add($(if ($fid) { "parent: $fid" } else { 'parent:' }))
[void]$metaLines.Add($(if ($eid) { "epic: $eid" } else { 'epic:' }))
[void]$metaLines.Add('parent_iid:')
[void]$metaLines.Add('state: open')
[void]$metaLines.Add("area: $area")
if ($type -eq 'FEATURE' -and $effortVal) { [void]$metaLines.Add("effort: $effortVal") }
if ($type -eq 'STORY' -and $pointsVal) { [void]$metaLines.Add("points: $pointsVal") }
if ($type -eq 'STORY' -and $sprintVal) { [void]$metaLines.Add("sprint: $sprintVal") }
if ($milestoneVal) { [void]$metaLines.Add("milestone: $milestoneVal") }
[void]$metaLines.Add('source: chat-intake')
[void]$metaLines.Add('~~~')

$desc = @"
$($metaLines -join "`n")

## User story
$userStory

## Acceptance criteria
$($ac -join "`n")

## Tasks
- [ ] Refine and confirm acceptance criteria
- [ ] Implement slice
- [ ] Verify in TEST
- [ ] Update parent link / board

## Origin
Chat intake to Cascade.

## Rationale
$([string]$parsed.rationale)

## Original request
$Message
"@

$labs = @('cascade', "area::$area")
if ($ctx.Mode -eq 'sandbox') { $labs = @('cascade-test') + $labs }
try {
    foreach ($lb in @($parsed.labels)) {
        if ($lb -and "$lb".Trim()) { $labs += "$lb".Trim() }
    }
} catch {}
$labs = @($labs | Select-Object -Unique)

Write-Host ''
Write-Host 'Proposed work item:' -ForegroundColor Green
Write-Host ("  Type    : {0}" -f $type)
Write-Host ("  Title   : {0}" -f $title)
Write-Host ("  Id      : {0}" -f $catalogId)
Write-Host ("  Parent  : {0}" -f $(if ($fid) { $fid } else { '(none)' }))
Write-Host ("  Epic    : {0}" -f $(if ($eid) { $eid } else { '(none)' }))
Write-Host ("  Project : {0} ({1})" -f $ctx.WriteProjectId, $ctx.Label)
Write-Host ("  Labels  : {0}" -f ($labs -join ', '))

if ($DryRun) {
    Write-Host 'DryRun - not creating in GitLab.' -ForegroundColor Yellow
    $desc | Set-Content (Join-Path $outDir 'chat_intake_description.md') -Encoding UTF8
    exit 0
}

if ($ctx.Mode -eq 'production' -and -not $Force) {
    if (-not $ctx.AllowProduction) {
        $ans = Read-Host 'PRODUCTION write - type CREATE to proceed'
        if ($ans -ne 'CREATE') { Write-Host 'Cancelled.'; exit 0 }
    }
} elseif (-not $Force) {
    $ans = Read-Host 'Create in sandbox? Type YES'
    if ($ans -ne 'YES') { Write-Host 'Cancelled.'; exit 0 }
}

Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
$url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$tok = Get-CascadeGitLabToken -AllowPrompt
$headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }
$base = "$url/api/v4/projects/$($ctx.WriteProjectId)"

$body = @{
    title       = $title
    description = $desc
    labels      = ($labs -join ',')
} | ConvertTo-Json -Depth 5

$created = Invoke-RestMethod -Method Post -Uri "$base/issues" -Headers $headers `
    -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 120

Write-Host ''
Write-Host ("CREATED !{0}" -f $created.iid) -ForegroundColor Green
Write-Host ("  {0}" -f $created.web_url)
("{0}" -f $created.iid) | Set-Content (Join-Path $outDir 'last_intake_iid.txt') -Encoding UTF8
($created | ConvertTo-Json -Depth 6) | Set-Content (Join-Path $outDir 'last_intake_issue.json') -Encoding UTF8
Write-Host 'Artifacts: output/chat_intake_parsed.json, output/last_intake_iid.txt'
