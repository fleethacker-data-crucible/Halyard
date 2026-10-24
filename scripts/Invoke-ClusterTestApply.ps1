<#
.SYNOPSIS
  Apply cluster inventory into sandbox with nested hierarchy:
    Epic (once per epic_id)
      Feature (once per feature_id under that epic)
        Story dups (from CBR source iids)
#>
param(
    [string]$ClusterId = '',
    [switch]$AllClusters,
    [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
    [int]$MaxStories = 40,
    [switch]$Force,
    [switch]$SkipAi
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

$srcPid = 16629
$tmp = 0
if ($env:GITLAB_SOURCE_PROJECT_ID -and [int]::TryParse($env:GITLAB_SOURCE_PROJECT_ID.Trim(), [ref]$tmp) -and $tmp -ne 52999) {
    $srcPid = $tmp
}
$tgtPid = 52999
if ($env:GITLAB_PROJECT_ID -and [int]::TryParse($env:GITLAB_PROJECT_ID.Trim(), [ref]$tmp) -and $tmp -ne 16629) {
    $tgtPid = $tmp
}
if ($tgtPid -eq 16629) { throw 'REFUSED: WRITE target cannot be CBR 16629' }

Write-Host ("Cluster apply READ={0} WRITE={1}" -f $srcPid, $tgtPid) -ForegroundColor Cyan

$invPath = Join-Path $RepoRoot 'output\cluster_inventory.json'
if (-not (Test-Path $invPath)) { throw 'Run [C] first - missing output/cluster_inventory.json' }
$invRaw = Get-Content $invPath -Raw
try { $inv = $invRaw | ConvertFrom-Json } catch {
    $inv = ConvertFrom-CascadeLlmJson -Text $invRaw
}
$clusters = @($inv.clusters)
if ($clusters.Count -eq 0) { throw 'No clusters in inventory' }

function Get-Prop($obj, [string[]]$names) {
    foreach ($n in $names) {
        if ($null -ne $obj -and $obj.PSObject.Properties[$n] -and $null -ne $obj.$n -and "$($obj.$n)".Trim() -ne '') {
            return "$($obj.$n)".Trim()
        }
    }
    return $null
}

function Format-CascadeTypeTitle([string]$Type, [string]$IdOrTitle) {
    $t = "$IdOrTitle".Trim()
    $t = $t -replace '^\[CASCADE-DUP\]\s*', ''
    $t = $t -replace '^EPIC\s*::\s*', '' -replace '^FEATURE\s*::\s*', '' -replace '^STORY\s*::\s*', ''
    $t = $t -replace '^EPIC-', '' -replace '^FEATURE-', '' -replace '^STORY-', ''
    $t = $t.Trim()
    # humanize: db-shared-utils -> Db Shared Utils
    if ($t -match '[-_]') {
        $parts = $t -split '[-_]+' | Where-Object { $_ }
        $t = ($parts | ForEach-Object {
            if ($_.Length -le 3 -and $_ -cmatch '^[A-Z0-9]+$') { $_.ToUpper() }
            else { $_.Substring(0,1).ToUpper() + $_.Substring(1).ToLower() }
        }) -join ' '
    }
    if ($Type -eq 'EPIC') { return "EPIC :: $t" }
    if ($Type -eq 'FEATURE') { return "FEATURE :: $t" }
    if ($Type -eq 'STORY') { return "STORY :: $t" }
    return $t
}

function Get-CascadeTitleKey([string]$Title) {
    $t = "$Title".ToLowerInvariant()
    $t = $t -replace '^\[cascade-dup\]\s*', ''
    $t = $t -replace '^epic\s*::\s*', '' -replace '^feature\s*::\s*', '' -replace '^story\s*::\s*', ''
    $t = $t -replace '[^a-z0-9]+', ''
    return $t
}

function Short-Id([string]$Id) {
    $t = "$Id".Trim()
    $t = $t -replace '^EPIC-', '' -replace '^FEATURE-', '' -replace '^STORY-', ''
    $t = $t -replace '^EPIC\s*::\s*', '' -replace '^FEATURE\s*::\s*', ''
    return $t.Trim()
}

$tree = @{}

function Add-ToTree {
    param($EpicId, $EpicTitle, $FeatureId, $FeatureTitle, $StoryIids)
    if (-not $EpicId) { $EpicId = 'EPIC-unknown' }
    if (-not $FeatureId) { $FeatureId = 'FEATURE-unknown' }
    if (-not $tree.ContainsKey($EpicId)) {
        $tree[$EpicId] = @{
            title    = $(if ($EpicTitle) { $EpicTitle } else { $EpicId })
            features = @{}
        }
    }
    $ef = $tree[$EpicId].features
    if (-not $ef.ContainsKey($FeatureId)) {
        $ef[$FeatureId] = @{
            title      = $(if ($FeatureTitle) { $FeatureTitle } else { $FeatureId })
            story_iids = New-Object System.Collections.Generic.List[int]
        }
    }
    foreach ($s in @($StoryIids)) {
        $n = 0
        if ([int]::TryParse("$s", [ref]$n) -and $n -gt 0) {
            if (-not $ef[$FeatureId].story_iids.Contains($n)) {
                [void]$ef[$FeatureId].story_iids.Add($n)
            }
        }
    }
}

$selected = @()
if ($AllClusters) {
    $selected = $clusters
} else {
    Write-Host ''
    Write-Host '=== Cluster tree (from inventory) ===' -ForegroundColor Cyan
    $byEpic = @{}
    foreach ($c in $clusters) {
        $eid = Get-Prop $c @('epic_id','EpicId')
        if (-not $eid) { $eid = 'EPIC-unknown' }
        if (-not $byEpic.ContainsKey($eid)) { $byEpic[$eid] = @() }
        $byEpic[$eid] += $c
    }
    foreach ($eid in ($byEpic.Keys | Sort-Object)) {
        Write-Host ("Epic: {0}" -f (Short-Id $eid)) -ForegroundColor Yellow
        foreach ($c in $byEpic[$eid]) {
            $fid = Get-Prop $c @('feature_id','FeatureId')
            $iids = @($c.story_iids) -join ', '
            Write-Host ("----- Feature: {0}  [{1}]" -f (Short-Id $fid), $c.cluster_id) -ForegroundColor Green
            Write-Host ("---------- Stories: {0}" -f $iids) -ForegroundColor Cyan
        }
    }
    Write-Host ''
    Write-Host 'Enter cluster id (C1), EPIC-gold, or ALL' -ForegroundColor DarkGray
    if (-not $ClusterId) {
        $ClusterId = Read-Host 'Selection'
    }
    if ($ClusterId -eq 'ALL' -or $ClusterId -eq 'all') {
        $selected = $clusters
        $AllClusters = $true
    } elseif ($ClusterId -match '^EPIC-') {
        $selected = @($clusters | Where-Object { (Get-Prop $_ @('epic_id','EpicId')) -eq $ClusterId })
        if ($selected.Count -eq 0) { throw "No clusters under $ClusterId" }
    } else {
        $chosen = $clusters | Where-Object { $_.cluster_id -eq $ClusterId } | Select-Object -First 1
        if (-not $chosen) { throw "Cluster not found: $ClusterId" }
        $selected = @($chosen)
    }
}

foreach ($c in $selected) {
    Add-ToTree -EpicId (Get-Prop $c @('epic_id','EpicId')) `
        -EpicTitle (Get-Prop $c @('epic_title','EpicTitle')) `
        -FeatureId (Get-Prop $c @('feature_id','FeatureId')) `
        -FeatureTitle (Get-Prop $c @('feature_title','FeatureTitle')) `
        -StoryIids $c.story_iids
}

$totalStories = 0
foreach ($eid in $tree.Keys) {
    foreach ($fid in $tree[$eid].features.Keys) {
        $totalStories += $tree[$eid].features[$fid].story_iids.Count
    }
}

Write-Host ''
Write-Host '=== APPLY TREE (deduped) ===' -ForegroundColor Cyan
foreach ($eid in ($tree.Keys | Sort-Object)) {
    Write-Host ("Epic: {0}" -f (Short-Id $eid)) -ForegroundColor Yellow
    foreach ($fid in ($tree[$eid].features.Keys | Sort-Object)) {
        $st = @($tree[$eid].features[$fid].story_iids)
        Write-Host ("----- Feature: {0}" -f (Short-Id $fid)) -ForegroundColor Green
        Write-Host ("---------- Stories: {0}" -f ($st -join ', ')) -ForegroundColor Cyan
    }
}
Write-Host ("Total unique epics={0} features={1} story-sources={2}" -f $tree.Count, (($tree.Values | ForEach-Object { $_.features.Count } | Measure-Object -Sum).Sum), $totalStories) -ForegroundColor Cyan
Write-Host ("Creates shells + story dups in sandbox project {0} only." -f $tgtPid) -ForegroundColor Yellow

if (-not $Force) {
    $yn = Read-Host 'Type YES to apply nested hierarchy to sandbox'
    if ($yn -ne 'YES') { Write-Host 'Cancelled.'; exit 0 }
}

Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
$url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$tok = Get-CascadeGitLabToken -AllowPrompt
$headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }
$base = "$url/api/v4/projects/$tgtPid"
$srcBase = "$url/api/v4/projects/$srcPid"

function Find-SandboxIssueByTitle([string]$TitlePrefix, [string]$CatalogId = '') {
    try {
        $list = @(Invoke-RestMethod -Method Get -Uri "$base/issues?state=opened&per_page=100" -Headers $headers -TimeoutSec 60)
        $want = Get-CascadeTitleKey $TitlePrefix
        $cat = "$CatalogId".Trim().ToLowerInvariant()
        foreach ($h in $list) {
            $ht = Get-CascadeTitleKey ([string]$h.title)
            if ($want -and $ht -eq $want) { return $h }
        }
        if ($cat) {
            foreach ($h in $list) {
                $body = [string]$h.description
                if ($body -match ("(?im)^id:\s*{0}\s*$" -f [regex]::Escape($CatalogId))) { return $h }
                if ($body -match [regex]::Escape($CatalogId)) {
                    # prefer type line nearby
                    if ($body -match ('id:\s*' + [regex]::Escape($CatalogId))) { return $h }
                }
            }
        }
    } catch { }
    return $null
}

function New-SandboxIssue {
    param(
        [string]$Title,
        [string]$Description,
        [string[]]$Labels,
        [int]$ParentIid = 0
    )
    $lab = @('cascade-test') + @($Labels | Where-Object { $_ -and $_ -ne 'cascade-test' })
    $lab = @($lab | Select-Object -Unique)
    $body = @{
        title       = $Title
        description = $Description
        labels      = ($lab -join ',')
    }
    $json = $body | ConvertTo-Json -Depth 5
    $created = Invoke-RestMethod -Method Post -Uri "$base/issues" -Headers $headers `
        -Body ([Text.Encoding]::UTF8.GetBytes($json)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
    Write-Host ("  created !{0} {1}" -f $created.iid, $created.title) -ForegroundColor Green

    if ($ParentIid -gt 0) {
        try {
            $linkBody = @{ target_project_id = $tgtPid; target_issue_iid = $ParentIid; link_type = 'relates_to' } | ConvertTo-Json
            Invoke-RestMethod -Method Post -Uri "$base/issues/$($created.iid)/links" -Headers $headers `
                -Body ([Text.Encoding]::UTF8.GetBytes($linkBody)) -ContentType 'application/json' -TimeoutSec 60 | Out-Null
        } catch {
            Write-Host ("  parent link soft-fail: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
        }
    }
    return $created
}

function Get-SourceIssue([int]$Iid) {
    return Invoke-RestMethod -Method Get -Uri "$srcBase/issues/$Iid" -Headers $headers -TimeoutSec 120
}

function New-CascadeMetaBlock {
    param([hashtable]$Map)
    # Canonical keys only - AI + tools parse one schema
    # type, id, parent, parent_iid, state, area  (+ source_* on stories)
    $order = @('type','id','parent','parent_iid','epic','epic_iid','source_project','source_iid','state','area')
    $lines = @('~~~cascade-meta')
    $seen = @{}
    foreach ($k in $order) {
        if ($Map.ContainsKey($k) -and $null -ne $Map[$k] -and "$($Map[$k])".Trim() -ne '') {
            $lines += ("{0}: {1}" -f $k, $Map[$k])
            $seen[$k] = $true
        }
    }
    foreach ($k in $Map.Keys) {
        if (-not $seen.ContainsKey($k) -and $k -ne 'status') {
            $lines += ("{0}: {1}" -f $k, $Map[$k])
        }
    }
    $lines += '~~~'
    return ($lines -join "`n")
}

$epicMap = @{}
$featureMap = @{}
$results = @()
$storyCount = 0

foreach ($eid in ($tree.Keys | Sort-Object)) {
    $epicTitle = Format-CascadeTypeTitle 'EPIC' $(if ($tree[$eid].title) { $tree[$eid].title } else { $eid })
    Write-Host ''
    Write-Host (">> Epic: {0}" -f (Short-Id $eid)) -ForegroundColor Yellow

    $existing = Find-SandboxIssueByTitle $epicTitle $eid
    if ($existing) {
        $epicMap[$eid] = [int]$existing.iid
        Write-Host ("  reuse epic !{0}" -f $existing.iid) -ForegroundColor DarkGray
    } else {
        $meta = New-CascadeMetaBlock @{
            type = 'EPIC'; id = $eid; parent = ''; state = 'open'; area = 'data-crucible'
        }
        $desc = @"
$meta

## Goal
Epic shell for catalog **$eid** (sandbox test).

## Scope
Owns Features created under this Epic during cluster apply. SAFE test - wipe with [X].

## Hierarchy
- Children: FEATURE shells with parent_epic=$eid
- label: cascade-test
"@
        $created = New-SandboxIssue -Title $epicTitle -Description $desc -Labels @('cascade-test','area::data-crucible')
        $epicMap[$eid] = [int]$created.iid
    }
    $epicIid = $epicMap[$eid]

    foreach ($fid in ($tree[$eid].features.Keys | Sort-Object)) {
        $ft = $tree[$eid].features[$fid]
        $featureTitle = Format-CascadeTypeTitle 'FEATURE' $(if ($ft.title) { $ft.title } else { $fid })
        Write-Host ("  >> Feature: {0}" -f (Short-Id $fid)) -ForegroundColor Green

        $existingF = Find-SandboxIssueByTitle $featureTitle $fid
        if ($existingF) {
            $featureMap[$fid] = [int]$existingF.iid
            Write-Host ("    reuse feature !{0}" -f $existingF.iid) -ForegroundColor DarkGray
        } else {
            $meta = New-CascadeMetaBlock @{
                type = 'FEATURE'; id = $fid; parent = $eid; parent_iid = "$epicIid"; state = 'open'; area = 'data-crucible'
            }
            $desc = @"
$meta

## Goal
Feature shell for catalog **$fid** under Epic **$eid**.

## Scope
Stories under this Feature are CASCADE-DUP test copies from CBR. SAFE - wipe with [X].

## Hierarchy
- Parent Epic: $eid (sandbox !$epicIid)
- Children: STORY dups with parent_feature=$fid
"@
            $createdF = New-SandboxIssue -Title $featureTitle -Description $desc -Labels @('cascade-test','area::data-crucible') -ParentIid $epicIid
            $featureMap[$fid] = [int]$createdF.iid
        }
        $featureIid = $featureMap[$fid]

        foreach ($sid in @($ft.story_iids)) {
            if ($storyCount -ge $MaxStories) { break }
            Write-Host ("    >> Story source !{0}" -f $sid) -ForegroundColor Cyan
            try {
                # Per-story AI response (avoid !356 AC on !312)
                $respJson = Join-Path $RepoRoot ("output\ai_response_iid_{0}.json" -f $sid)
                $respShared = Join-Path $RepoRoot 'output\ai_response_one_dup.json'
                if (Test-Path $respShared) { Remove-Item $respShared -Force -ErrorAction SilentlyContinue }

                if (-not $SkipAi) {
                    try {
                        & (Join-Path $PSScriptRoot 'Invoke-AiRestructureTest.ps1') -Profile $Profile -SourceIid $sid -SourceProjectId $srcPid
                    } catch {
                        Write-Host ("    AI soft-fail: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
                    }
                }
                if (-not (Test-Path $respJson) -and (Test-Path $respShared)) {
                    Copy-Item $respShared $respJson -Force
                }

                $applied = $false
                if (Test-Path $respJson) {
                    try {
                        & (Join-Path $PSScriptRoot 'Invoke-ApplyAiProposalsToDup.ps1') -SourceIid $sid -SourceProjectId $srcPid -TargetProjectId $tgtPid -ResponseJson $respJson -Force
                        $last = Join-Path $RepoRoot 'output\last_dup_iid.txt'
                        $newIid = if (Test-Path $last) { (Get-Content $last -Raw).Trim() } else { '?' }
                        if ($newIid -match '^\d+$') {
                            try {
                                $dup = Invoke-RestMethod -Method Get -Uri "$base/issues/$newIid" -Headers $headers -TimeoutSec 60
                                $stamp = New-CascadeMetaBlock @{
                                    type = 'STORY'; parent = $fid; parent_iid = "$featureIid"
                                    epic = $eid; epic_iid = "$epicIid"
                                    source_project = "$srcPid"; source_iid = "$sid"
                                }
                                $newDesc = [string]$dup.description + "`n`n" + $stamp
                                $ttl = Format-CascadeTypeTitle 'STORY' ([string]$dup.title)
                                if ($ttl -notmatch '^\[CASCADE-DUP\]') { $ttl = "[CASCADE-DUP] $ttl" }
                                $patch = @{ title = $ttl; description = $newDesc } | ConvertTo-Json -Depth 5
                                Invoke-RestMethod -Method Put -Uri "$base/issues/$newIid" -Headers $headers `
                                    -Body ([Text.Encoding]::UTF8.GetBytes($patch)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 60 | Out-Null
                                try {
                                    $linkBody = @{ target_project_id = $tgtPid; target_issue_iid = $featureIid; link_type = 'relates_to' } | ConvertTo-Json
                                    Invoke-RestMethod -Method Post -Uri "$base/issues/$newIid/links" -Headers $headers `
                                        -Body ([Text.Encoding]::UTF8.GetBytes($linkBody)) -ContentType 'application/json' -TimeoutSec 60 | Out-Null
                                } catch { }
                            } catch {
                                Write-Host ("    stamp soft-fail: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
                            }
                        }
                        $applied = $true
                        $storyCount++
                        $results += [pscustomobject]@{
                            epic_id = $eid; epic_sandbox = $epicIid
                            feature_id = $fid; feature_sandbox = $featureIid
                            source_iid = $sid; story_sandbox = $newIid
                            status = 'ok-full-apply'
                        }
                    } catch {
                        Write-Host ("    full-apply fail, falling back to shell dup: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
                    }
                }

                if (-not $applied) {
                    $src = Get-SourceIssue -Iid $sid
                    $stitle = Format-CascadeTypeTitle 'STORY' ([string]$src.title)
                    $stitle = "[CASCADE-DUP] $stitle"
                    $labs = @('cascade-test')
                    try {
                        foreach ($lb in @($src.labels)) {
                            if ($lb) { $labs += [string]$lb }
                        }
                    } catch { }
                    $meta = New-CascadeMetaBlock @{
                        type = 'STORY'; parent = $fid; parent_iid = "$featureIid"
                        epic = $eid; epic_iid = "$epicIid"
                        source_project = "$srcPid"; source_iid = "$sid"; state = 'open'
                    }
                    $srcTitle = [string]$src.title
                    $srcDesc = [string]$src.description
                    $desc = @"
$meta

## User story
As a stakeholder, I need **$srcTitle** so that Feature **$fid** outcomes are met.

## Acceptance criteria
- [ ] (AI package missing - fill on next pass)
- [ ] Linked under Feature sandbox !$featureIid / Epic !$epicIid

## Source
$srcDesc
"@
                    if ($desc.Length -gt 900000) { $desc = $desc.Substring(0, 900000) }
                    $createdS = New-SandboxIssue -Title $stitle -Description $desc -Labels $labs -ParentIid $featureIid
                    $storyCount++
                    $results += [pscustomobject]@{
                        epic_id = $eid; epic_sandbox = $epicIid
                        feature_id = $fid; feature_sandbox = $featureIid
                        source_iid = $sid; story_sandbox = [int]$createdS.iid
                        status = 'ok-fallback-shell'
                    }
                }
            } catch {
                Write-Host ("    FAIL story !{0}: {1}" -f $sid, $_.Exception.Message) -ForegroundColor Red
                $results += [pscustomobject]@{
                    epic_id = $eid; feature_id = $fid; source_iid = $sid
                    status = $_.Exception.Message
                }
            }
        }
    }
}

Write-Host ''
Write-Host '=== NESTED APPLY RESULTS ===' -ForegroundColor Green
Write-Host ("Epics created/reused: {0}" -f $epicMap.Count)
Write-Host ("Features created/reused: {0}" -f $featureMap.Count)
Write-Host ("Stories created: {0}" -f $storyCount)
$results | Format-Table -AutoSize
$out = Join-Path $RepoRoot 'output\cluster_test_results.json'
$results | ConvertTo-Json -Depth 5 | Set-Content $out -Encoding UTF8
Write-Host "Wrote $out"
Write-Host ("Sandbox: {0}/nmmes_bip/sm-dats-sagemaker/cascade-test-sandbox/-/work_items" -f $url) -ForegroundColor Cyan
