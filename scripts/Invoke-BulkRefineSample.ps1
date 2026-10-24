<#
.SYNOPSIS
  Bulk backlog sample (READ-ONLY on CBR).
  Fetches area::data-crucible issues, sends them to the LLM in chunks,
  writes a triage table (JSON / CSV / HTML). No GitLab writes.
#>
param(
    [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
    [string]$State = 'opened',
    [string]$Labels = 'area::data-crucible',
    [int]$ChunkSize = 25,
    [int]$MaxItems = 100,
    [int]$MaxPages = 10,
    [switch]$SkipLlm,
    [switch]$Interactive
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

Write-Host '=== Cascade Bulk Refine SAMPLE (read-only) ===' -ForegroundColor Cyan
$ctx = Write-CascadeWriteBanner
Write-Host 'This mode does NOT write to GitLab (CBR or sandbox).' -ForegroundColor DarkGray
Write-Host 'Output = triage table for you to Exclude / Meeting / Accept-candidate / Skip.' -ForegroundColor DarkGray

$tax = $null
try { $tax = Get-CascadeTaxonomy } catch {}
$themeLines = ''
if ($tax) {
    $themeLines = @($tax.themes | ForEach-Object { '{0}' -f $_.id }) -join ', '
}

Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
$url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$tok = Get-CascadeGitLabToken -AllowPrompt
$headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }

$readId = 16629
try { if ($ctx.ReadProjectId) { $readId = [int]$ctx.ReadProjectId } } catch {}
if ($readId -le 0) { $readId = 16629 }
$readBase = "$url/api/v4/projects/$readId"

Write-Host ("READ project={0}  labels={1}  state={2}" -f $readId, $Labels, $State) -ForegroundColor Cyan
Write-Host ("ChunkSize={0}  MaxItems={1}" -f $ChunkSize, $MaxItems) -ForegroundColor DarkGray

# --- fetch ---
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
} while ($batch.Count -eq 50 -and $page -le $MaxPages -and $flat.Count -lt ($MaxItems * 2))

$issues = @(
    $flat.ToArray() |
        Where-Object { $null -ne $_ -and $_.PSObject.Properties['iid'] } |
        Sort-Object { [int](@($_.iid)[0]) } |
        Select-Object -First $MaxItems
)

Write-Host ("Fetched {0} issue(s)." -f $issues.Count) -ForegroundColor Green
if ($issues.Count -eq 0) {
    Write-Host 'No issues. Check label area::data-crucible and token access to CBR.' -ForegroundColor Yellow
    return
}

function Get-IssueSnippet([string]$text, [int]$max = 280) {
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    $t = ($text -replace '\s+', ' ').Trim()
    if ($t.Length -le $max) { return $t }
    return $t.Substring(0, $max) + '...'
}

function Get-LabelNames($issue) {
    $cur = @()
    try {
        if ($issue.labels) {
            if ($issue.labels[0].name) { $cur = @($issue.labels | ForEach-Object { $_.name }) }
            else { $cur = @($issue.labels | ForEach-Object { "$_" }) }
        }
    } catch { $cur = @() }
    return $cur
}

# Compact rows for LLM (save tokens)
$compact = @()
foreach ($iss in $issues) {
    if ($iss -is [System.Array]) { $iss = $iss[0] }
    $iid = [int](@($iss.iid)[0])
    $title = [string](@($iss.title)[0])
    $desc = ''
    try { $desc = [string](@($iss.description)[0]) } catch { $desc = '' }
    $labs = Get-LabelNames $iss
    $compact += [pscustomobject]@{
        iid     = $iid
        title   = $title
        labels  = ($labs -join ',')
        snippet = (Get-IssueSnippet $desc 280)
    }
}

$compactPath = Join-Path $outDir 'bulk_refine_input.json'
($compact | ConvertTo-Json -Depth 6) | Set-Content $compactPath -Encoding UTF8
Write-Host ("Wrote input snapshot: {0}" -f $compactPath) -ForegroundColor DarkGray

$rows = New-Object System.Collections.Generic.List[object]

function ConvertFrom-BulkJson([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return @() }
    $t = $Text.Trim()
    # strip BOM / fences
    $t = $t -replace '^\uFEFF', ''
    if ($t -match '(?s)```(?:json)?\s*(.*?)\s*```') { $t = $Matches[1].Trim() }
    # prefer array
    $startArr = $t.IndexOf('[')
    $startObj = $t.IndexOf('{')
    $json = $null
    if ($startArr -ge 0 -and ($startObj -lt 0 -or $startArr -le $startObj)) {
        $end = $t.LastIndexOf(']')
        if ($end -gt $startArr) { $json = $t.Substring($startArr, $end - $startArr + 1) }
    } elseif ($startObj -ge 0) {
        $end = $t.LastIndexOf('}')
        if ($end -gt $startObj) {
            $json = $t.Substring($startObj, $end - $startObj + 1)
            # wrap single object as array if it has iid
            if ($json -match '"iid"') { $json = "[$json]" }
        }
    }
    if (-not $json) { return @() }
    try {
        $parsed = $json | ConvertFrom-Json
        return @($parsed)
    } catch {
        return @()
    }
}

function Get-HeuristicType([string]$Title, [string]$Labels) {
    if ($Title -match '(?i)^THEME\s*::') { return 'THEME' }
    if ($Title -match '(?i)^EPIC\s*::') { return 'EPIC' }
    if ($Title -match '(?i)^FEATURE\s*::') { return 'FEATURE' }
    if ($Title -match '(?i)^STORY\s*::') { return 'STORY' }
    if ($Title -match '(?i)^INC\d+') { return 'STORY' }
    if ($Labels -match '(?i)type::theme') { return 'THEME' }
    if ($Labels -match '(?i)type::epic') { return 'EPIC' }
    if ($Labels -match '(?i)type::feature') { return 'FEATURE' }
    if ($Labels -match '(?i)type::story') { return 'STORY' }
    if ($Title -match '(?i)roadmap|gantt|memo|onboarding|training|survey|welcome pack') { return 'EPIC' }
    if ($Title -match '(?i)build out data lake|end-to-end') { return 'EPIC' }
    if ($Title -match '(?i)^(fix|deploy|fix|configure|setup|schedule|submit|send|process|validate|parallelize)') { return 'STORY' }
    return 'STORY'
}

function Get-HeuristicTheme([string]$Title, [string]$Labels, [string]$Snippet) {
    $blob = "$Title $Labels $Snippet"
    if ($blob -match '(?i)bedrock|sagemaker|llm|ml |machine learning|agentic|automation') { return 'THEME-automation' }
    if ($blob -match '(?i)qlik|data lake|parquet|etl|postgres|rds|bip dw|concerto|s3') { return 'THEME-platform-engineering' }
    if ($blob -match '(?i)training|qual card|onboarding|survey|welcome') { return 'THEME-process-improvement' }
    if ($blob -match '(?i)STIG|RMF|security guide|IAM|governance') { return 'THEME-process-improvement' }
    return ''
}

function Convert-BulkModelRow {
    param($pr, [string]$Title, [string]$Labels, [string]$Snippet)

    $iid = 0
    try { $iid = [int]($pr.iid) } catch { return $null }

    # type
    $type = ''
    foreach ($k in @('type_guess','type','work_item_type','item_type')) {
        if ($pr.PSObject.Properties.Name -contains $k -and "$($pr.$k)".Trim()) {
            $type = "$($pr.$k)".Trim().ToUpperInvariant()
            break
        }
    }
    if ($type -match 'THEME') { $type = 'THEME' }
    elseif ($type -match 'EPIC') { $type = 'EPIC' }
    elseif ($type -match 'FEATURE') { $type = 'FEATURE' }
    elseif ($type -match 'STORY|TASK|INC') { $type = 'STORY' }
    elseif ($type -notmatch '^(THEME|EPIC|FEATURE|STORY)$') { $type = '' }
    if (-not $type) { $type = Get-HeuristicType -Title $Title -Labels $Labels }

    # theme
    $theme = ''
    foreach ($k in @('theme_guess','theme','theme_id')) {
        if ($pr.PSObject.Properties.Name -contains $k -and "$($pr.$k)".Trim()) {
            $theme = "$($pr.$k)".Trim()
            break
        }
    }
    if (-not $theme) { $theme = Get-HeuristicTheme -Title $Title -Labels $Labels -Snippet $Snippet }

    # confidence
    $conf = ''
    foreach ($k in @('confidence','conf')) {
        if ($pr.PSObject.Properties.Name -contains $k -and "$($pr.$k)".Trim()) {
            $conf = "$($pr.$k)".Trim().ToLowerInvariant()
            break
        }
    }
    if ($conf -notmatch '^(high|medium|low)$') { $conf = 'medium' }

    # why: model often puts prose in action / recommended_action
    $why = ''
    foreach ($k in @('why','reason','rationale','recommended_action','recommendation','notes')) {
        if ($pr.PSObject.Properties.Name -contains $k -and "$($pr.$k)".Trim()) {
            $why = "$($pr.$k)".Trim()
            break
        }
    }

    # action enum
    $actionRaw = ''
    if ($pr.PSObject.Properties.Name -contains 'action') { $actionRaw = [string]$pr.action }
    $action = ''
    if ($actionRaw -match '^(ACCEPT_CANDIDATE|MEETING|SKIP)$') {
        $action = $Matches[1]
    } elseif ($actionRaw -match '(?i)accept_candidate|accept candidate') {
        $action = 'ACCEPT_CANDIDATE'
    } elseif ($actionRaw -match '(?i)^skip$|no action|defer') {
        $action = 'SKIP'
    } elseif ($actionRaw -match '(?i)meeting|refine|human|unclear|need(s)? review') {
        $action = 'MEETING'
    }

    # priority-based fallback (model's alternate schema)
    $prio = ''
    foreach ($k in @('priority','priority_guess')) {
        if ($pr.PSObject.Properties.Name -contains $k) { $prio = "$($pr.$k)".Trim().ToLowerInvariant(); break }
    }
    if (-not $action) {
        if ($prio -match 'critical|now') { $action = 'ACCEPT_CANDIDATE' }
        elseif ($prio -match 'later') { $action = 'SKIP' }
        elseif ($prio -match 'next') { $action = 'ACCEPT_CANDIDATE' }
        else { $action = 'MEETING' }
    }

    # if action field was long prose, use it as why
    if (-not $why -and $actionRaw -and $actionRaw -notmatch '^(ACCEPT_CANDIDATE|MEETING|SKIP)$') {
        $why = $actionRaw
    }
    if ($why.Length -gt 200) { $why = $why.Substring(0, 197) + '...' }

    # needs_split
    $needsSplit = $false
    if ($pr.PSObject.Properties.Name -contains 'needs_split') {
        try { $needsSplit = [bool]$pr.needs_split } catch { $needsSplit = $false }
    }
    if ($Title -match '(?i)build out data lake|end-to-end|43.*merge request') { $needsSplit = $true }

    $missing = @()
    if ($pr.PSObject.Properties.Name -contains 'missing') {
        try { $missing = @($pr.missing) } catch { $missing = @() }
    }
    if (-not $Snippet) { $missing += 'description' }

    # category into missing/why enrichment
    $cat = ''
    if ($pr.PSObject.Properties.Name -contains 'category') { $cat = [string]$pr.category }
    if ($cat -and -not $why) { $why = $cat }

    return [pscustomobject]@{
        iid          = $iid
        title        = $Title
        type_guess   = $type
        theme_guess  = $theme
        confidence   = $conf
        action       = $action
        why          = $why
        needs_split  = $needsSplit
        missing      = ($missing -join ',')
        exclude      = $false
        priority_hint = $prio
        category_hint = $cat
    }
}

function Build-BulkSystemPrompt {
    @"
You are a JSON API for Cascade bulk backlog triage. Not a chat assistant.

CRITICAL OUTPUT RULES:
1. Output ONLY a JSON array. No markdown fences. No keys other than the schema below.
2. First character must be [ and last must be ].
3. One object per input issue. Copy each iid exactly.
4. action MUST be exactly one of: ACCEPT_CANDIDATE, MEETING, SKIP (enum, not a sentence).
5. Put any free-text recommendation only in the why field.

Required schema per object:
{
  "iid": 123,
  "type_guess": "THEME|EPIC|FEATURE|STORY",
  "theme_guess": "THEME-automation|THEME-platform-engineering|THEME-process-improvement|",
  "confidence": "high|medium|low",
  "action": "ACCEPT_CANDIDATE|MEETING|SKIP",
  "why": "short reason max 120 chars",
  "needs_split": false,
  "missing": ["ac"]
}

Mapping rules:
- Most operational tickets are STORY.
- Large initiatives (data lake, roadmap, end-to-end ML) are EPIC and needs_split=true.
- INC#### tickets are STORY.
- ACCEPT_CANDIDATE = enough title/snippet to run 1:1 refine later.
- MEETING = unclear type, empty body, or needs SME.
- SKIP = explicit later/low value noise only.
- Themes available: $themeLines

Do NOT invent fields named category, priority, status, or recommended_action.
"@
}

if ($SkipLlm) {
    Write-Host 'SkipLlm: building heuristic rows only.' -ForegroundColor Yellow
    foreach ($c in $compact) {
        $tg = 'UNKNOWN'
        if ($c.title -match '(?i)^THEME\s*::') { $tg = 'THEME' }
        elseif ($c.title -match '(?i)^EPIC\s*::') { $tg = 'EPIC' }
        elseif ($c.title -match '(?i)^FEATURE\s*::') { $tg = 'FEATURE' }
        elseif ($c.title -match '(?i)^STORY\s*::') { $tg = 'STORY' }
        [void]$rows.Add([pscustomobject]@{
            iid = $c.iid
            title = $c.title
            type_guess = $tg
            theme_guess = ''
            confidence = 'low'
            action = 'MEETING'
            why = 'Heuristic only (no LLM)'
            needs_split = $false
            missing = @('review')
            exclude = $false
        })
    }
} else {
    $chunks = @()
    for ($i = 0; $i -lt $compact.Count; $i += $ChunkSize) {
        $take = [Math]::Min($ChunkSize, $compact.Count - $i)
        $chunks += , @($compact[$i..($i + $take - 1)])
    }
    Write-Host ("LLM chunks: {0}" -f $chunks.Count) -ForegroundColor Cyan
    $chunkIndex = 0
    foreach ($chunk in $chunks) {
        $chunkIndex++
        Write-Host ("  Chunk {0}/{1} ({2} issues)..." -f $chunkIndex, $chunks.Count, @($chunk).Count) -ForegroundColor DarkGray
        $payload = ($chunk | ConvertTo-Json -Depth 5 -Compress)
        if (-not $payload) { $payload = ($chunk | ConvertTo-Json -Depth 5) }
        $userPrompt = @"
Triage these GitLab issues. Return JSON array only. One object per iid.
action must be ACCEPT_CANDIDATE or MEETING or SKIP (enum only).
Put sentences in why, not in action.
Include type_guess and confidence on every row.

ISSUES:
$payload
"@
        $rawText = ''
        try {
            $messages = @(
                @{ role = 'system'; content = (Build-BulkSystemPrompt) }
                @{ role = 'user'; content = $userPrompt }
            )
            $chat = Invoke-CascadeChat -Profile $Profile -Messages $messages -MaxTokens 20000
            if ($chat.Content) { $rawText = [string]$chat.Content }
            elseif ($chat -is [string]) { $rawText = $chat }
            else {
                try { $rawText = [string]$chat.choices[0].message.content } catch { }
                if (-not $rawText) { try { $rawText = [string]$chat } catch { $rawText = '' } }
            }
            $rawText | Set-Content (Join-Path $outDir ("bulk_refine_raw_chunk{0}.txt" -f $chunkIndex)) -Encoding UTF8
            $parsedRows = @(ConvertFrom-BulkJson -Text $rawText)
            if ($parsedRows.Count -eq 0) {
                Write-Host ("  Chunk {0}: parse failed - heuristic fallback for this chunk" -f $chunkIndex) -ForegroundColor Yellow
                foreach ($c in @($chunk)) {
                    [void]$rows.Add([pscustomobject]@{
                        iid = $c.iid; title = $c.title; type_guess = 'UNKNOWN'; theme_guess = ''
                        confidence = 'low'; action = 'MEETING'; why = 'LLM parse failed'
                        needs_split = $false; missing = @('parse'); exclude = $false
                    })
                }
            } else {
                $byIid = @{}
                foreach ($c in @($chunk)) { $byIid[[string]$c.iid] = $c }
                foreach ($pr in $parsedRows) {
                    $id = $null
                    try { $id = [int]($pr.iid) } catch { continue }
                    $title = ''
                    $labels = ''
                    $snippet = ''
                    if ($byIid.ContainsKey([string]$id)) {
                        $title = [string]$byIid[[string]$id].title
                        $labels = [string]$byIid[[string]$id].labels
                        $snippet = [string]$byIid[[string]$id].snippet
                    }
                    $norm = Convert-BulkModelRow -pr $pr -Title $title -Labels $labels -Snippet $snippet
                    if ($null -ne $norm) { [void]$rows.Add($norm) }
                }
                # any iid missing from model response
                foreach ($c in @($chunk)) {
                    $found = $false
                    foreach ($r in $rows) { if ([int]$r.iid -eq [int]$c.iid) { $found = $true; break } }
                    if (-not $found) {
                        [void]$rows.Add([pscustomobject]@{
                            iid = $c.iid; title = $c.title; type_guess = 'UNKNOWN'; theme_guess = ''
                            confidence = 'low'; action = 'MEETING'; why = 'Missing from LLM chunk'
                            needs_split = $false; missing = @('omitted'); exclude = $false
                        })
                    }
                }
            }
        } catch {
            Write-Host ("  Chunk {0} LLM error: {1}" -f $chunkIndex, $_.Exception.Message) -ForegroundColor Red
            if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
                $null = Write-CascadeErrorLog -Operation 'BulkRefineSample' -Message $_.Exception.Message -Context @{ chunk = $chunkIndex }
            }
            foreach ($c in @($chunk)) {
                [void]$rows.Add([pscustomobject]@{
                    iid = $c.iid; title = $c.title; type_guess = 'UNKNOWN'; theme_guess = ''
                    confidence = 'low'; action = 'MEETING'; why = 'LLM error'
                    needs_split = $false; missing = @('error'); exclude = $false
                })
            }
        }
    }
}

# Dedupe by iid (keep first)
$seen = @{}
$final = New-Object System.Collections.Generic.List[object]
foreach ($r in $rows) {
    $k = [string]$r.iid
    if ($seen.ContainsKey($k)) { continue }
    $seen[$k] = $true
    [void]$final.Add($r)
}

if ($Interactive -and $final.Count -gt 0) {
    Write-Host ''
    Write-Host 'Interactive exclude: enter iid numbers to EXCLUDE (comma-separated), or blank to keep all.' -ForegroundColor Cyan
    Write-Host 'Example: 12,45,90' -ForegroundColor DarkGray
    # show compact table
    $final | Select-Object iid, action, confidence, type_guess, @{n='title';e={ if ($_.title.Length -gt 50) { $_.title.Substring(0,50)+'...' } else { $_.title } }} |
        Format-Table -AutoSize | Out-Host
    $ans = Read-Host 'Exclude iids'
    if ($ans -and $ans.Trim()) {
        $ex = @{}
        foreach ($part in ($ans -split '[,;\s]+')) {
            $n = 0
            if ([int]::TryParse($part, [ref]$n)) { $ex[[string]$n] = $true }
        }
        foreach ($r in $final) {
            if ($ex.ContainsKey([string]$r.iid)) { $r.exclude = $true }
        }
    }
}

$jsonPath = Join-Path $outDir 'bulk_refine_table.json'
$csvPath  = Join-Path $outDir 'bulk_refine_table.csv'
$htmlPath = Join-Path $outDir 'bulk_refine_table.html'

($final | ConvertTo-Json -Depth 6) | Set-Content $jsonPath -Encoding UTF8
$final | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

# simple HTML
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<!DOCTYPE html><html><head><meta charset="utf-8"><title>Cascade Bulk Refine Sample</title>')
[void]$sb.AppendLine('<style>body{font-family:Segoe UI,Arial,sans-serif;margin:16px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccc;padding:6px;font-size:13px}th{background:#1e3a5f;color:#fff}tr.meeting{background:#fff3cd}tr.accept{background:#d4edda}tr.skip{background:#e2e3e5}tr.exclude{opacity:0.45;text-decoration:line-through}</style>')
[void]$sb.AppendLine('</head><body>')
[void]$sb.AppendLine('<h1>Cascade Bulk Refine Sample</h1>')
[void]$sb.AppendLine(("<p>READ project {0} | labels={1} | rows={2} | generated {3}</p>" -f $readId, $Labels, $final.Count, (Get-Date).ToString('u')))
[void]$sb.AppendLine('<p>Actions: ACCEPT_CANDIDATE = later 1:1 refine candidate | MEETING = human refinement | SKIP = no action. Set exclude in JSON or interactive mode.</p>')
[void]$sb.AppendLine('<table><tr><th>iid</th><th>action</th><th>confidence</th><th>type</th><th>theme</th><th>split</th><th>why</th><th>title</th><th>exclude</th></tr>')
foreach ($r in $final) {
    $cls = 'skip'
    if ($r.exclude) { $cls = 'exclude' }
    elseif ($r.action -eq 'MEETING') { $cls = 'meeting' }
    elseif ($r.action -eq 'ACCEPT_CANDIDATE') { $cls = 'accept' }
    $title = [System.Net.WebUtility]::HtmlEncode([string]$r.title)
    $why = [System.Net.WebUtility]::HtmlEncode([string]$r.why)
    [void]$sb.AppendLine(("<tr class=`"{0}`"><td>{1}</td><td>{2}</td><td>{3}</td><td>{4}</td><td>{5}</td><td>{6}</td><td>{7}</td><td>{8}</td><td>{9}</td></tr>" -f `
        $cls, $r.iid, $r.action, $r.confidence, $r.type_guess, $r.theme_guess, $r.needs_split, $why, $title, $r.exclude))
}
[void]$sb.AppendLine('</table></body></html>')
$sb.ToString() | Set-Content $htmlPath -Encoding UTF8

# summary counts
$counts = @{ ACCEPT_CANDIDATE = 0; MEETING = 0; SKIP = 0; excluded = 0 }
foreach ($r in $final) {
    if ($r.exclude) { $counts.excluded++; continue }
    if ($counts.ContainsKey([string]$r.action)) { $counts[[string]$r.action]++ }
}
$summary = [pscustomobject]@{
    read_project = $readId
    labels = $Labels
    total = $final.Count
    accept_candidate = $counts.ACCEPT_CANDIDATE
    meeting = $counts.MEETING
    skip = $counts.SKIP
    excluded = $counts.excluded
    json = $jsonPath
    csv = $csvPath
    html = $htmlPath
}
($summary | ConvertTo-Json) | Set-Content (Join-Path $outDir 'bulk_refine_summary.json') -Encoding UTF8

Write-Host ''
Write-Host '=== Bulk sample complete (no GitLab writes) ===' -ForegroundColor Green
Write-Host ($summary | Format-List | Out-String)
Write-Host "Open HTML: $htmlPath"
Write-Host 'Next: review table -> run [F] only on ACCEPT_CANDIDATE / MEETING iids you care about.'
