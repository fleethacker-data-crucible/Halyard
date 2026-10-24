<#
.SYNOPSIS
  Inventory Stories -> JSON package -> AI cluster.
  CSV is local source only; API receives JSON.
#>
param(
    [string]$GapCsv = '',
    [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
    [int]$MaxItems = 80,
    [int]$SourceProjectId = 0
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^=#]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"')
        }
    }
}

$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$items = @()

function Get-RowVal($row, [string[]]$Names) {
    foreach ($n in $Names) {
        if ($null -eq $row) { return $null }
        $prop = $row.PSObject.Properties[$n]
        if ($null -ne $prop -and $null -ne $prop.Value -and "$($prop.Value)".Trim() -ne '') {
            return $prop.Value
        }
    }
    return $null
}

# --- CSV local source ---
if (-not $GapCsv) {
    foreach ($c in @(
        (Join-Path $outDir 'gap_report_data_crucible.csv'),
        (Join-Path $outDir 'gap_report.csv'),
        (Join-Path $outDir 'normalize_queue.csv')
    )) {
        if (Test-Path -LiteralPath $c) { $GapCsv = $c; break }
    }
}

if ($GapCsv -and (Test-Path -LiteralPath $GapCsv)) {
    $csvLen = (Get-Item -LiteralPath $GapCsv).Length
    Write-Host ("Local source: CSV {0} ({1} bytes)" -f $GapCsv, $csvLen) -ForegroundColor DarkGray
    if ($csvLen -lt 50) {
        Write-Host '  CSV empty/tiny - likely from sandbox gap report. Will pull CBR via API.' -ForegroundColor Yellow
        $GapCsv = ''
    }
}
if ($GapCsv -and (Test-Path -LiteralPath $GapCsv)) {
    $rows = @(Import-Csv -LiteralPath $GapCsv)
    Write-Host ("  rows={0}" -f $rows.Count) -ForegroundColor DarkGray
    $nTake = [Math]::Min($MaxItems, $rows.Count)
    for ($ri = 0; $ri -lt $nTake; $ri++) {
        $r = $rows[$ri]
        try {
            $rawIid = Get-RowVal $r @('iid','Iid','IID','id','Id','issue_iid')
            if ($null -eq $rawIid) { continue }
            $iidNum = 0
            $ok = [int]::TryParse(("$rawIid").Trim(), [ref]$iidNum)
            if (-not $ok -or $iidNum -le 0) { continue }

            $rawTitle = Get-RowVal $r @('title','Title','name','Name','summary')
            $title = if ($null -eq $rawTitle) { '(no title)' } else { ("$rawTitle").Trim() }
            $title = ($title -replace '\s+', ' ')
            if ($title.Length -gt 200) { $title = $title.Substring(0, 200) + '...' }

            $rawLab = Get-RowVal $r @('labels','Labels','label')
            $labArr = @()
            if ($null -ne $rawLab) {
                $labArr = @("$rawLab" -split '[,;|]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
            }

            $rawState = Get-RowVal $r @('state','State')
            $state = if ($null -eq $rawState) { 'opened' } else { "$rawState" }

            $rawDesc = Get-RowVal $r @('description','Description','desc')
            $desc = if ($null -eq $rawDesc) { '' } else { "$rawDesc" }
            if ($desc.Length -gt 400) { $desc = $desc.Substring(0, 400) + '...' }

            if ($iidNum -le 0) { continue }
            if ([string]::IsNullOrWhiteSpace($title) -or $title -eq '(no title)') {
                # still allow if title missing but has iid - use placeholder
                $title = "Issue !$iidNum"
            }
            $items += [pscustomobject]@{
                iid         = $iidNum
                title       = $title
                state       = $state
                labels      = $labArr
                description = $desc
            }
        } catch {
            Write-Host ("  skip row {0}: {1}" -f $ri, $_.Exception.Message) -ForegroundColor DarkYellow
        }
    }
    Write-Host ("  parsed work items from CSV: {0}" -f $items.Count) -ForegroundColor DarkGray
}

# --- GitLab API if empty ---
if ($items.Count -eq 0) {
    Write-Host 'No CSV items - fetching issues via GitLab API...' -ForegroundColor Yellow

    $readPid = 0
    if ($SourceProjectId -gt 0) { $readPid = [int]$SourceProjectId }
    if ($readPid -le 0 -and $env:GITLAB_SOURCE_PROJECT_ID) {
        $tmp = 0
        if ([int]::TryParse(("$($env:GITLAB_SOURCE_PROJECT_ID)").Trim(), [ref]$tmp)) { $readPid = $tmp }
    }
    if ($readPid -le 0) { $readPid = 16629 }
    if ($readPid -eq 52999) {
        Write-Host '  SOURCE was 52999 (sandbox) - forcing CBR 16629' -ForegroundColor Yellow
        $readPid = 16629
    }

    $writePid = 0
    if ($env:GITLAB_PROJECT_ID) {
        $tmpW = 0
        if ([int]::TryParse(("$($env:GITLAB_PROJECT_ID)").Trim(), [ref]$tmpW)) { $writePid = $tmpW }
    }
    # Sandbox is write-only - never inventory it
    if ($writePid -gt 0 -and $readPid -eq $writePid -and $readPid -ne 16629) {
        Write-Host ("  READ id {0} is sandbox WRITE target - forcing CBR 16629" -f $readPid) -ForegroundColor Yellow
        $readPid = 16629
    }

    Write-Host ("  GitLab READ project={0}" -f $readPid) -ForegroundColor Cyan
    Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
    $url = 'https://web.git.mil'
    if ($env:GITLAB_URL) { $url = ("$($env:GITLAB_URL)").TrimEnd('/') }
    $tok = Get-CascadeGitLabToken -AllowPrompt
    $headers = @{ 'PRIVATE-TOKEN' = "$tok"; 'Accept' = 'application/json' }
    $base = "$url/api/v4/projects/$readPid"
    $page = 1
    $fetched = 0
    while ($fetched -lt $MaxItems -and $page -le 10) {
        $uri = "$base/issues?state=opened&per_page=50&page=$page&order_by=created_at&sort=desc"
        Write-Host ("  GET page {0}..." -f $page) -ForegroundColor DarkGray
        $raw = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -TimeoutSec 120
        $batch = @($raw)
        if ($batch.Count -eq 0) { break }
        foreach ($iss in $batch) {
            if ($fetched -ge $MaxItems) { break }
            try {
                $iidNum = 0
                $iidRaw = $iss.iid
                if ($null -eq $iidRaw) { continue }
                if (-not [int]::TryParse(("$iidRaw").Trim(), [ref]$iidNum)) { continue }
                if ($iidNum -le 0) { continue }

                $title = ''
                try { $title = [string]$iss.title } catch { $title = '' }
                if ([string]::IsNullOrWhiteSpace($title)) { $title = "Issue !$iidNum" }
                $title = ($title -replace '\s+', ' ').Trim()
                if ($title.Length -gt 200) { $title = $title.Substring(0, 200) + '...' }

                $labArr = @()
                try {
                    if ($null -ne $iss.labels) {
                        foreach ($lb in @($iss.labels)) {
                            $s = [string]$lb
                            if (-not [string]::IsNullOrWhiteSpace($s)) { $labArr += $s }
                        }
                    }
                } catch { }

                $state = 'opened'
                try { if ($iss.state) { $state = [string]$iss.state } } catch { }

                $desc = ''
                try {
                    if ($iss.description) {
                        $desc = [string]$iss.description
                        if ($desc.Length -gt 400) { $desc = $desc.Substring(0, 400) + '...' }
                    }
                } catch { }

                $items += [pscustomobject]@{
                    iid         = $iidNum
                    title       = $title
                    state       = $state
                    labels      = $labArr
                    description = $desc
                }
                $fetched++
            } catch {
                Write-Host ("  skip issue: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
            }
        }
        if ($batch.Count -lt 50) { break }
        $page++
    }
    Write-Host ("  Fetched {0} issues from project {1}" -f $items.Count, $readPid) -ForegroundColor DarkGray
}


# Drop garbage rows (iid 0 / empty)
$items = @($items | Where-Object { $_.iid -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$_.title) })
if ($items.Count -eq 0) {
    throw @"
No work items in package.

Cause: gap CSV was from empty sandbox (52999) or parse failed.
Fix:
  1) cascade.env: GITLAB_SOURCE_PROJECT_ID=16629  GITLAB_PROJECT_ID=52999
  2) Run [R] again (now reads CBR)
  3) Run [C]

Sandbox stays empty until [K] creates dups there.
"@
}

# JSON package for API (not CSV)
$package = @{
    package_type    = 'cascade.cluster_inventory.v1'
    work_item_count = $items.Count
    work_items      = @($items | ForEach-Object {
        @{
            iid         = $_.iid
            title       = $_.title
            state       = $_.state
            labels      = @($_.labels)
            description = $_.description
        }
    })
}
$packageJson = $package | ConvertTo-Json -Depth 6
$packageJson | Set-Content -LiteralPath (Join-Path $outDir 'cluster_work_items_package.json') -Encoding UTF8

Write-Host ''
Write-Host ("JSON package work_items: {0}" -f $items.Count) -ForegroundColor Green
Write-Host 'Saved: output/cluster_work_items_package.json' -ForegroundColor DarkGray
$previewN = [Math]::Min(5, $items.Count)
for ($i = 0; $i -lt $previewN; $i++) {
    Write-Host ("  !{0} {1}" -f $items[$i].iid, $items[$i].title) -ForegroundColor DarkGray
}
if ($items.Count -gt $previewN) {
    Write-Host ("  ... +{0} more" -f ($items.Count - $previewN)) -ForegroundColor DarkGray
}

# Compact catalog (ids + titles only) - full v2 confuses models into ignoring work_items
$catalogPath = Join-Path $RepoRoot 'fixtures\hierarchy_catalog.json'
$catalogCompact = @{ epics = @(); features = @() }
if (Test-Path -LiteralPath $catalogPath) {
    try {
        $cat = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
        if ($cat.epics) {
            foreach ($e in @($cat.epics)) {
                $eid = $e.id; if (-not $eid) { $eid = $e.Id }
                $etit = $e.title; if (-not $etit) { $etit = $e.name }
                if ($eid) { $catalogCompact.epics += @{ id = "$eid"; title = "$etit" } }
            }
        }
        if ($cat.features) {
            foreach ($f in @($cat.features)) {
                $fid = $f.id; if (-not $fid) { $fid = $f.Id }
                $ftit = $f.title; if (-not $ftit) { $ftit = $f.name }
                $fpar = $f.parent; if (-not $fpar) { $fpar = $f.epic }
                if ($fid) { $catalogCompact.features += @{ id = "$fid"; title = "$ftit"; parent = "$fpar" } }
            }
        }
        # alternate shapes
        if ($cat.hierarchy) { }
    } catch {
        Write-Host ("Catalog compact failed: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
    }
}
$catalogJson = $catalogCompact | ConvertTo-Json -Depth 4

# Ensure package file is what we send - re-read from disk
$pkgPath = Join-Path $outDir 'cluster_work_items_package.json'
$pkgForLlm = Get-Content -LiteralPath $pkgPath -Raw -Encoding UTF8
if ($pkgForLlm -notmatch '"work_item_count"\s*:\s*[1-9]') {
    throw "Package file missing work items - abort before LLM. File: $pkgPath"
}
Write-Host ("Package file bytes={0} work_item_count check OK" -f $pkgForLlm.Length) -ForegroundColor DarkGray

# WORK ITEMS FIRST so truncation cannot drop them
$system = @'
You are Cascade. Cluster GitLab work items into Features under Epics.
The USER message starts with work_items_package_json which has work_item_count and work_items[].
Each work item has iid, title, state, labels, description.
You MUST cluster those iids. Never say work items are empty when work_item_count >= 1.
CRITICAL: Do NOT write markdown tables or portfolio essays. OUTPUT JSON only between markers:
***<<< JSON PAYLOAD >>>***
{"analysis":"short","clusters":[{"cluster_id":"C1","feature_id":"FEATURE-X","feature_title":"FEATURE :: X","epic_id":"EPIC-Y","epic_title":"EPIC :: Y","story_iids":[1,2],"rationale":"why","effort":"Medium"}]}
***<<< END JSON >>>***
effort: Small|Medium|Large|Extra Large. Prefer catalog feature/epic ids when they fit.
'@

$user = @"
=== WORK ITEMS PACKAGE (primary input - cluster these) ===
$pkgForLlm

=== COMPACT CATALOG (parent id hints only) ===
$catalogJson

Cluster every iid from work_items into clusters. work_item_count=$($items.Count).
"@

# Debug: save exact prompt
$user | Set-Content -LiteralPath (Join-Path $outDir 'cluster_llm_user_prompt.txt') -Encoding UTF8
Write-Host ("User prompt chars={0} (saved output/cluster_llm_user_prompt.txt)" -f $user.Length) -ForegroundColor DarkGray
Write-Host ("  work_items in prompt: {0}" -f ([regex]::Matches($user, '"iid"\s*:')).Count) -ForegroundColor DarkGray

Write-Host ''
Write-Host ("Sending JSON package to {0} ({1} items)..." -f $Profile, $items.Count) -ForegroundColor Cyan
$msgs = @(
    @{ role = 'system'; content = $system },
    @{ role = 'user'; content = $user }
)
$chat = Invoke-CascadeChat -Profile $Profile -Messages $msgs -MaxTokens 20000 -Temperature 0.1
$text = ''
if ($null -ne $chat) {
    if ($chat.PSObject.Properties['Content'] -and $chat.Content) { $text = [string]$chat.Content }
    else { $text = [string]$chat }
}
if ([string]::IsNullOrWhiteSpace($text)) { throw 'Empty LLM response' }

$text | Set-Content -LiteralPath (Join-Path $outDir 'cluster_inventory_raw.txt') -Encoding UTF8
Write-Host ("Raw response chars={0} (output/cluster_inventory_raw.txt)" -f $text.Length) -ForegroundColor DarkGray

$outPath = Join-Path $outDir 'cluster_inventory.json'
$parsed = $null
try {
    $parsed = ConvertFrom-CascadeLlmJson -Text $text
} catch {
    Write-Host ("JSON parse failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    Write-Host 'Trying markdown-table fallback (Gemini often returns tables not JSON)...' -ForegroundColor Cyan
    try {
        $parsed = ConvertFrom-CascadeClusterMarkdown -Text $text
        Write-Host ("Markdown fallback OK: {0} clusters" -f @($parsed.clusters).Count) -ForegroundColor Green
    } catch {
        Write-Host ("Markdown fallback failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        Write-Host 'Second-pass: ask model to emit JSON only from its own report...' -ForegroundColor Cyan
        $sys2 = @'
Convert the clustering report into JSON only between markers.
***<<< JSON PAYLOAD >>>***
{"analysis":"...","clusters":[{"cluster_id":"C1","feature_id":"FEATURE-x","feature_title":"FEATURE :: x","epic_id":"EPIC-y","epic_title":"EPIC :: y","story_iids":[1,2],"rationale":"...","effort":"Medium"}]}
***<<< END JSON >>>***
No markdown. No tables. Include every iid mentioned.
'@
        $user2 = $text
        if ($user2.Length -gt 100000) { $user2 = $user2.Substring(0, 100000) }
        $chat2 = Invoke-CascadeChat -Profile $Profile -Messages @(
            @{ role = 'system'; content = $sys2 },
            @{ role = 'user'; content = $user2 }
        ) -MaxTokens 16000 -Temperature 0.0
        $text2 = if ($chat2.Content) { [string]$chat2.Content } else { [string]$chat2 }
        $text2 | Set-Content -LiteralPath (Join-Path $outDir 'cluster_inventory_raw_pass2.txt') -Encoding UTF8
        try {
            $parsed = ConvertFrom-CascadeLlmJson -Text $text2
        } catch {
            try {
                $parsed = ConvertFrom-CascadeClusterMarkdown -Text $text
            } catch {
                Write-Host '=== TROUBLESHOOT ===' -ForegroundColor Red
                Write-Host 'Package and prompt were good; model refused JSON. See cluster_inventory_raw.txt'
                throw
            }
        }
    }
}
($parsed | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $outPath -Encoding UTF8
Write-Host "Wrote $outPath" -ForegroundColor Green

Write-Host ''
Write-Host '=== CLUSTER SUMMARY ===' -ForegroundColor Cyan
if ($parsed.analysis) { Write-Host ("Analysis: {0}" -f $parsed.analysis) -ForegroundColor DarkGray }
$n = 0
$clusterList = @()
if ($parsed.clusters) { $clusterList = @($parsed.clusters) }
foreach ($c in $clusterList) {
    $n++
    $iidsJoin = ''
    if ($c.story_iids) { $iidsJoin = (@($c.story_iids) -join ', ') }
    Write-Host ''
    Write-Host ("[{0}] {1}" -f $n, $c.cluster_id) -ForegroundColor Yellow
    $fShow = if ($c.feature_id) { ($c.feature_id -replace '^FEATURE-','') } else { $c.feature_title }
    Write-Host ("  Feature : {0}" -f $fShow)
    $eShow = if ($c.epic_id) { ($c.epic_id -replace '^EPIC-','') } else { $c.epic_title }
    Write-Host ("  Epic    : {0}" -f $eShow)
    Write-Host ("  Effort  : {0}" -f $c.effort)
    Write-Host ("  Stories : {0}" -f $iidsJoin)
    if ($c.rationale) { Write-Host ("  Why     : {0}" -f $c.rationale) }
}
Write-Host ''
Write-Host ("Total clusters: {0}" -f $n) -ForegroundColor Green
if ($n -eq 0) {
    Write-Host 'Zero clusters - see output/cluster_inventory_raw.txt' -ForegroundColor Yellow
} else {
    Write-Host 'Next: [K] cluster TEST apply -> sandbox' -ForegroundColor Cyan
}
