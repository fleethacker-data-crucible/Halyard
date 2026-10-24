# Leadership dashboard metrics + HTML export (SMPG Leadership Dashboard layout).

function Get-CascadeDefaultLeadershipConfig {
    <#
    .SYNOPSIS
      Default sprint/capacity/velocity settings used when live history is missing.
    #>
    [CmdletBinding()]
    param()
    return [pscustomobject]@{
        schema              = 'cascade.leadership_config.v1'
        sprint_label        = 'Sprint 14'
        sprint_end          = '08 Oct 2026'
        velocity_history    = @(18, 21, 19)   # last 3 sprints, oldest -> newest
        capacity_points     = 25              # planned capacity this sprint
        committed_points    = $null           # if null, derived from open story points
        available_points    = $null
        healthy_backlog_min = 1.0             # multiples of velocity
        healthy_backlog_max = 1.5
        blocker_age_warn    = 5               # business days
        blocker_age_critical = 7
        # Optional explicit feature rows for manual Monday board (overrides tree when present)
        feature_rows        = @()
        # Optional explicit blockers for manual board
        blockers            = @()
    }
}

function Get-CascadeDemoLeadershipBoard {
    <#
    .SYNOPSIS
      Reference board matching the attached SMPG Leadership Dashboard image (for Monday delivery).
    #>
    [CmdletBinding()]
    param()
    $cfg = Get-CascadeDefaultLeadershipConfig
    $cfg.sprint_label = 'Sprint 14'
    $cfg.sprint_end = '08 Oct 2026'
    $cfg.velocity_history = @(18, 21, 19)
    $cfg.capacity_points = 25
    $cfg.committed_points = 19
    $cfg.available_points = 6
    $cfg.feature_rows = @(
        [pscustomobject]@{ Feature = 'Full-volume ingest'; Epic = 'Bronze'; PctDone = 82; Status = 'On Track'; Target = 'Nov 2026' }
        [pscustomobject]@{ Feature = 'Catalog & Lake Formation'; Epic = 'Bronze'; PctDone = 25; Status = 'At Risk'; Target = 'Dec 2026' }
        [pscustomobject]@{ Feature = 'Metric catalog Phase 1'; Epic = 'Gold'; PctDone = 48; Status = 'On Track'; Target = 'Jan 2027' }
        [pscustomobject]@{ Feature = 'SC Foundation + CICD'; Epic = 'On-Prem'; PctDone = 70; Status = 'On Track'; Target = 'Oct 2026' }
        [pscustomobject]@{ Feature = 'Silver evaluate & decide'; Epic = 'Silver'; PctDone = 20; Status = 'Parked'; Target = 'post-Bronze' }
        [pscustomobject]@{ Feature = 'Operationalize Bronze'; Epic = 'Silver'; PctDone = 15; Status = 'On Track'; Target = 'Feb 2027' }
        [pscustomobject]@{ Feature = 'Enable core AWS services'; Epic = 'AWS'; PctDone = 55; Status = 'On Track'; Target = 'Nov 2026' }
        [pscustomobject]@{ Feature = 'Postgres Z-Drive load'; Epic = 'Bronze'; PctDone = 40; Status = 'On Track'; Target = 'Dec 2026' }
        [pscustomobject]@{ Feature = 'External integrations'; Epic = 'Integration'; PctDone = 30; Status = 'At Risk'; Target = 'Jan 2027' }
        [pscustomobject]@{ Feature = 'Security / STIG baseline'; Epic = 'Security'; PctDone = 60; Status = 'On Track'; Target = 'Oct 2026' }
        [pscustomobject]@{ Feature = 'Team tooling & KB'; Epic = 'Enablement'; PctDone = 45; Status = 'At Risk'; Target = 'Nov 2026' }
    )
    $cfg.blockers = @(
        [pscustomobject]@{ Id = 'OPS-882'; Description = 'Cross-account LF permission for NMD-reader'; Type = 'OPS'; Age = 6; Owner = 'PG'; Impact = 'Blocks F2 stories' }
        [pscustomobject]@{ Id = 'EXT-14'; Description = 'DATABRICKS network update for new SaaS route'; Type = 'External'; Age = 8; Owner = 'FO'; Impact = 'Delays catalog' }
        [pscustomobject]@{ Id = 'OPS-901'; Description = 'TEST account data-lake missing S3 write'; Type = 'OPS'; Age = 3; Owner = 'Tech Lead'; Impact = 'Impedes ingest' }
    )
    return $cfg
}

function Convert-CascadeStatusToTrackLabel {
    param(
        [string]$State,
        [int]$PctDone = -1,
        [string]$StatusLabel
    )
    $s = "$State".ToUpperInvariant()
    if ($s -match 'PARK|HOLD|DEFER') { return 'Parked' }
    if ($s -eq 'BLOCKED') { return 'At Risk' }
    if ($StatusLabel -match '(?i)at\s*risk|risk') { return 'At Risk' }
    if ($StatusLabel -match '(?i)park') { return 'Parked' }
    if ($PctDone -ge 0 -and $PctDone -lt 30 -and $s -in @('OPEN', 'READY')) { return 'At Risk' }
    if ($s -in @('DONE', 'CLOSED')) { return 'On Track' }
    return 'On Track'
}

function Get-CascadeLeadershipMetrics {
    <#
    .SYNOPSIS
      Compute SMPG Leadership Dashboard KPIs from normalized (or raw) issues.
    .PARAMETER Issues
      GitLab issues or cascade.normalized_issue.v1 objects.
    .PARAMETER Config
      Optional leadership config (velocity history, capacity, manual feature rows).
    .PARAMETER UseDemoBoard
      If set, return the reference board matching the attached image (Monday-ready).
    #>
    [CmdletBinding()]
    param(
        [object[]]$Issues,
        $Config,
        [switch]$UseDemoBoard
    )

    if (-not $Config) { $Config = Get-CascadeDefaultLeadershipConfig }
    if ($UseDemoBoard) { $Config = Get-CascadeDemoLeadershipBoard }

    $normalized = @()
    if ($Issues -and $Issues.Count -gt 0) {
        $first = $Issues[0]
        if ($first.PSObject.Properties['schema'] -and ("$($first.schema)" -match 'normalized')) {
            $normalized = @($Issues)
        } else {
            $normalized = @(Convert-CascadeGitLabIssuesToNormalized -Issues $Issues)
        }
    }

    # Build tree metrics when we have real items (for feature % from hierarchy)
    $treeMetrics = $null
    $featureRows = @()
    if ($Config.feature_rows -and @($Config.feature_rows).Count -gt 0) {
        $featureRows = @($Config.feature_rows | ForEach-Object {
                [pscustomobject]@{
                    Feature = [string]$_.Feature
                    Epic    = [string]$_.Epic
                    PctDone = [int]$_.PctDone
                    Status  = [string]$_.Status
                    Target  = [string]$_.Target
                }
            })
    } elseif ($normalized.Count -gt 0) {
        # Prefer issues that already have Cascade meta in description for rollup
        $forTree = foreach ($n in $normalized) {
            [pscustomobject]@{
                iid         = $n.iid
                title       = $n.title
                description = $n.description
                state       = $n.state
            }
        }
        $treeMetrics = Get-CascadeMetrics -Issues $forTree
        $catalog = $null
        try { $catalog = Get-CascadeHierarchyCatalog } catch { }

        $epicByFeature = @{}
        if ($catalog) {
            foreach ($f in $catalog.features) { $epicByFeature[$f.id] = $f.epic }
        }

        foreach ($kv in $treeMetrics.FeatureCompletion.GetEnumerator()) {
            $fid = [string]$kv.Key
            $pct = [int]$kv.Value
            $node = $null
            if ($treeMetrics.Tree.Nodes.Contains($fid)) { $node = $treeMetrics.Tree.Nodes[$fid] }
            $epic = ''
            if ($node -and $node.Parent) { $epic = [string]$node.Parent }
            elseif ($epicByFeature.ContainsKey($fid)) { $epic = $epicByFeature[$fid] }
            $title = if ($node) { $node.Title } else { $fid }
            $st = if ($node) { Convert-CascadeStatusToTrackLabel -State $node.State -PctDone $pct } else { 'On Track' }
            $tgt = ''
            if ($node -and $node.Meta -and $node.Meta['target']) { $tgt = [string]$node.Meta['target'] }
            $featureRows += [pscustomobject]@{
                Feature = $title
                Epic    = $epic
                PctDone = $pct
                Status  = $st
                Target  = $tgt
            }
        }
        # Features inferred but not yet in tree (normalized proposed FEATURE without meta parse)
        $normFeatures = @($normalized | Where-Object { $_.proposed_type -eq 'FEATURE' })
        foreach ($nf in $normFeatures) {
            $exists = $featureRows | Where-Object { $_.Feature -eq $nf.title -or $_.Feature -eq $nf.proposed_id }
            if (-not $exists) {
                $st = Convert-CascadeStatusToTrackLabel -State $nf.state -StatusLabel $nf.status_label
                $featureRows += [pscustomobject]@{
                    Feature = $nf.title
                    Epic    = $(if ($nf.proposed_parent) { $nf.proposed_parent } else { '' })
                    PctDone = $(if ($nf.state -eq 'DONE') { 100 } else { 0 })
                    Status  = $st
                    Target  = [string]$nf.target
                }
            }
        }
    }

    $onTrack = @($featureRows | Where-Object { $_.Status -eq 'On Track' }).Count
    $atRisk = @($featureRows | Where-Object { $_.Status -eq 'At Risk' }).Count
    $parked = @($featureRows | Where-Object { $_.Status -eq 'Parked' }).Count
    $featureTotal = $featureRows.Count

    # Velocity
    $velHist = @($Config.velocity_history | ForEach-Object { [int]$_ })
    if ($velHist.Count -eq 0) { $velHist = @(0) }
    $velLatest = [int]$velHist[-1]
    $velAvg = [math]::Round((($velHist | Measure-Object -Average).Average), 1)

    # Points remaining = open story points (or remaining stories count when points unknown)
    $openStories = @($normalized | Where-Object {
            $_.proposed_type -in @('STORY', 'TASK') -and ("$($_.state)".ToUpper() -notin @('DONE', 'CLOSED'))
        })
    $pointsRemaining = 0
    $storiesWithPoints = 0
    foreach ($s in $openStories) {
        $pts = 0
        try { $pts = [int]$s.story_points } catch { $pts = 0 }
        if ($pts -gt 0) {
            $pointsRemaining += $pts
            $storiesWithPoints++
        }
    }
    $openStoryCount = $openStories.Count
    # If almost no points estimated, report story count as "points remaining" proxy label will clarify
    $pointsRemainingDisplay = if ($storiesWithPoints -gt 0) { $pointsRemaining } else { $openStoryCount }
    $pointsIsStoryCount = ($storiesWithPoints -eq 0)

    $healthyMin = [double]$Config.healthy_backlog_min
    $healthyMax = [double]$Config.healthy_backlog_max
    $healthyLo = [math]::Round($velLatest * $healthyMin)
    $healthyHi = [math]::Round($velLatest * $healthyMax)
    $backlogHealth = 'unknown'
    if ($velLatest -gt 0 -and -not $pointsIsStoryCount) {
        if ($pointsRemaining -ge $healthyLo -and $pointsRemaining -le $healthyHi) { $backlogHealth = 'healthy' }
        elseif ($pointsRemaining -lt $healthyLo) { $backlogHealth = 'low' }
        else { $backlogHealth = 'high' }
    }

    # Capacity
    $cap = [int]$Config.capacity_points
    if ($cap -le 0) { $cap = 1 }
    $committed = $Config.committed_points
    if ($null -eq $committed) {
        $committed = ($openStories | Where-Object {
                $_.status_label -match '(?i)in\s*progress|to\s*do|ready' -or $_.state -in @('IN_PROGRESS', 'READY')
            } | Measure-Object -Property story_points -Sum).Sum
        if (-not $committed) { $committed = 0 }
        $committed = [int]$committed
    } else { $committed = [int]$committed }
    $available = $Config.available_points
    if ($null -eq $available) { $available = [math]::Max(0, $cap - $committed) } else { $available = [int]$available }
    $overcommit = [math]::Max(0, $committed - $cap)
    $capacityUsedPct = [math]::Round(([double]$committed / $cap) * 100)

    # Blockers
    $blockers = @()
    if ($Config.blockers -and @($Config.blockers).Count -gt 0) {
        $blockers = @($Config.blockers | ForEach-Object {
                [pscustomobject]@{
                    Id          = [string]$_.Id
                    Description = [string]$_.Description
                    Type        = [string]$_.Type
                    Age         = [int]$_.Age
                    Owner       = [string]$_.Owner
                    Impact      = [string]$_.Impact
                }
            })
    } else {
        $blockItems = @($normalized | Where-Object { $_.is_blocker })
        foreach ($b in $blockItems) {
            $impact = 'Blocks delivery'
            if ($b.proposed_parent) { $impact = "Blocks $($b.proposed_parent)" }
            $blockers += [pscustomobject]@{
                Id          = $(if ($b.proposed_id) { $b.proposed_id } else { "IID-$($b.iid)" })
                Description = $b.title
                Type        = $(if ($b.blocker_type) { $b.blocker_type } else { 'OPS' })
                Age         = [int]$b.age_business_days
                Owner       = $(if ($b.owner) { $b.owner } else { '' })
                Impact      = $impact
            }
        }
        # Escalate aged open OPS without explicit blocked flag: priority::critical + INC*
        if ($blockers.Count -eq 0) {
            $aged = @($normalized | Where-Object {
                    $_.age_business_days -ge [int]$Config.blocker_age_warn -and
                    ("$($_.state)".ToUpper() -notin @('DONE', 'CLOSED')) -and
                    ($_.title -match '(?i)^INC\d+' -or $_.priority -match '(?i)critical|now')
                } | Select-Object -First 10)
            foreach ($b in $aged) {
                $blockers += [pscustomobject]@{
                    Id          = "IID-$($b.iid)"
                    Description = $b.title
                    Type        = 'OPS'
                    Age         = [int]$b.age_business_days
                    Owner       = $(if ($b.owner) { $b.owner } else { '' })
                    Impact      = 'Aged open ticket'
                }
            }
        }
    }

    $blockerCount = $blockers.Count
    $avgAge = 0.0
    if ($blockerCount -gt 0) {
        $avgAge = [math]::Round((($blockers | Measure-Object -Property Age -Average).Average), 1)
    }
    $criticalBlockers = @($blockers | Where-Object { $_.Age -ge [int]$Config.blocker_age_critical }).Count

    return [pscustomobject]@{
        schema                 = 'cascade.leadership_metrics.v1'
        generated              = (Get-Date).ToString('o')
        sprint_label           = [string]$Config.sprint_label
        sprint_end             = [string]$Config.sprint_end
        features_on_track      = $onTrack
        features_total         = $featureTotal
        features_at_risk       = $atRisk
        features_parked        = $parked
        feature_rows           = $featureRows
        velocity_history       = $velHist
        velocity_latest        = $velLatest
        velocity_avg           = $velAvg
        points_remaining       = $pointsRemainingDisplay
        points_is_story_count  = [bool]$pointsIsStoryCount
        open_story_count       = $openStoryCount
        backlog_health         = $backlogHealth
        healthy_backlog_range  = "$healthyLo-$healthyHi"
        capacity_points        = $cap
        committed_points       = $committed
        available_points       = $available
        overcommit_points      = $overcommit
        capacity_used_pct      = $capacityUsedPct
        blockers               = $blockers
        blocker_count          = $blockerCount
        blocker_avg_age        = $avgAge
        blocker_critical_count = $criticalBlockers
        normalized_count       = $normalized.Count
        tree_metrics           = $treeMetrics
        is_demo                = [bool]$UseDemoBoard
    }
}

function Export-CascadeLeadershipHtml {
    <#
    .SYNOPSIS
      Render the SMPG Leadership Dashboard HTML (Feature Progress / Velocity / Capacity / Blockers).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Metrics,
        [string]$Path = (Join-Path (Get-Location) 'output\leadership_dashboard.html'),
        [string]$Title = 'SMPG Leadership Dashboard'
    )
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

    $m = $Metrics
    $velArrow = ($m.velocity_history -join ' - ')
    $pointsLabel = if ($m.points_is_story_count) {
        "{0} stories" -f $m.points_remaining
    } else {
        "{0} pts - {1} stories" -f $m.points_remaining, $m.open_story_count
    }
    $healthyNote = if ($m.backlog_health -eq 'healthy') {
        "healthy - $($m.healthy_backlog_range) (1-1.5- vel)"
    } elseif ($m.backlog_health -eq 'low') {
        "below healthy range $($m.healthy_backlog_range)"
    } elseif ($m.backlog_health -eq 'high') {
        "above healthy range $($m.healthy_backlog_range)"
    } else {
        "healthy - 1-1.5- velocity"
    }

    $statusClass = {
        param($st)
        switch -Regex ("$st") {
            'At Risk' { return 'status-risk' }
            'Parked'  { return 'status-parked' }
            default   { return 'status-track' }
        }
    }

    $featureTableRows = foreach ($r in $m.feature_rows) {
        $sc = & $statusClass $r.Status
        $pct = [int]$r.PctDone
        $barColor = if ($pct -ge 70) { '#2e7d32' } elseif ($pct -ge 40) { '#f9a825' } else { '#c62828' }
        @"
<tr>
  <td class="feat">$([System.Net.WebUtility]::HtmlEncode($r.Feature))</td>
  <td>$([System.Net.WebUtility]::HtmlEncode($r.Epic))</td>
  <td class="pct">
    <div class="bar-wrap"><div class="bar" style="width:${pct}%;background:$barColor"></div></div>
    <span>${pct}%</span>
  </td>
  <td><span class="pill $sc">$([System.Net.WebUtility]::HtmlEncode($r.Status))</span></td>
  <td>$([System.Net.WebUtility]::HtmlEncode($r.Target))</td>
</tr>
"@
    }

    # Velocity bars (CSS)
    $maxVel = 1
    foreach ($v in $m.velocity_history) { if ([int]$v -gt $maxVel) { $maxVel = [int]$v } }
    $velBars = for ($i = 0; $i -lt @($m.velocity_history).Count; $i++) {
        $v = [int]$m.velocity_history[$i]
        $h = [math]::Max(8, [math]::Round(($v / $maxVel) * 120))
        $label = "S$($i + 1)"
        if (@($m.velocity_history).Count -eq 3) {
            $label = @('S12', 'S13', 'S14')[$i]
        }
        @"
<div class="vbar"><div class="vfill" style="height:${h}px"></div><div class="vval">$v</div><div class="vlbl">$label</div></div>
"@
    }

    # Capacity pie via conic-gradient
    $cap = [math]::Max(1, [int]$m.capacity_points)
    $cPct = [math]::Min(100, [math]::Round(([double]$m.committed_points / $cap) * 100))
    $aPct = [math]::Min(100 - $cPct, [math]::Round(([double]$m.available_points / $cap) * 100))
    $oPct = [math]::Max(0, 100 - $cPct - $aPct)
    if ($m.overcommit_points -gt 0) {
        $oPct = [math]::Round(([double]$m.overcommit_points / $cap) * 100)
        $cPct = [math]::Min(100, $cPct)
        $aPct = [math]::Max(0, 100 - $cPct)
    }
    $pieStyle = "background:conic-gradient(#1F4E79 0% ${cPct}%, #90caf9 ${cPct}% $([math]::Min(100,$cPct+$aPct))%, #e53935 $([math]::Min(100,$cPct+$aPct))% 100%)"

    $blockerRows = foreach ($b in $m.blockers) {
        $ageClass = if ([int]$b.Age -ge 7) { 'age-crit' } elseif ([int]$b.Age -ge 5) { 'age-warn' } else { '' }
        @"
<tr>
  <td class="mono">$([System.Net.WebUtility]::HtmlEncode($b.Id))</td>
  <td>$([System.Net.WebUtility]::HtmlEncode($b.Description))</td>
  <td>$([System.Net.WebUtility]::HtmlEncode($b.Type))</td>
  <td class="$ageClass">$([int]$b.Age)</td>
  <td>$([System.Net.WebUtility]::HtmlEncode($b.Owner))</td>
  <td>$([System.Net.WebUtility]::HtmlEncode($b.Impact))</td>
</tr>
"@
    }
    if (-not $blockerRows) {
        $blockerRows = '<tr><td colspan="6" class="muted">No open OPS/dependency blockers above threshold.</td></tr>'
    }

    $demoBadge = if ($m.is_demo) { '<span class="demo">DEMO / REFERENCE BOARD</span>' } else { '' }
    $atRiskNote = if ($m.features_at_risk -gt 0) { "$($m.features_at_risk) at risk" } else { 'all clear' }

    $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>$Title</title>
<style>
  :root {
    --navy: #1F4E79;
    --navy-dark: #163A5F;
    --bg: #f4f6f9;
    --card: #ffffff;
    --text: #1a1a1a;
    --muted: #5f6b7a;
    --border: #d8dee6;
    --risk: #c62828;
    --track: #2e7d32;
    --parked: #6d4c41;
    --warn: #ef6c00;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 20px 24px 40px;
    font-family: "Segoe UI", system-ui, -apple-system, Arial, sans-serif;
    background: var(--bg); color: var(--text);
  }
  header {
    display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between;
    gap: 8px 16px; margin-bottom: 16px;
  }
  header h1 { margin: 0; font-size: 1.35rem; color: var(--navy); letter-spacing: 0.02em; }
  .tabs { display: flex; gap: 6px; flex-wrap: wrap; }
  .tab {
    font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.04em;
    padding: 4px 10px; border-radius: 999px; background: #e8eef5; color: var(--navy);
  }
  .tab.active { background: var(--navy); color: #fff; }
  .sprint { color: var(--muted); font-size: 0.9rem; }
  .demo {
    background: #fff3cd; color: #856404; font-size: 0.7rem; font-weight: 600;
    padding: 2px 8px; border-radius: 4px; margin-left: 8px;
  }
  .kpi-row {
    display: grid; grid-template-columns: repeat(5, 1fr); gap: 12px; margin-bottom: 16px;
  }
  @media (max-width: 1100px) { .kpi-row { grid-template-columns: repeat(3, 1fr); } }
  @media (max-width: 700px) { .kpi-row { grid-template-columns: 1fr 1fr; } }
  .kpi {
    background: var(--card); border: 1px solid var(--border); border-radius: 10px;
    padding: 12px 14px; box-shadow: 0 1px 2px rgba(0,0,0,0.04);
  }
  .kpi .label { font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--muted); }
  .kpi .value { font-size: 1.55rem; font-weight: 700; color: var(--navy-dark); margin-top: 4px; line-height: 1.15; }
  .kpi .sub { font-size: 0.78rem; color: var(--muted); margin-top: 4px; }
  .kpi.warn .value { color: var(--risk); }
  .main {
    display: grid; grid-template-columns: 1.4fr 1fr; gap: 14px; margin-bottom: 14px;
  }
  @media (max-width: 900px) { .main { grid-template-columns: 1fr; } }
  .card {
    background: var(--card); border: 1px solid var(--border); border-radius: 10px;
    padding: 14px 16px; box-shadow: 0 1px 2px rgba(0,0,0,0.04);
  }
  .card h2 {
    margin: 0 0 10px; font-size: 0.95rem; color: var(--navy);
    display: flex; align-items: center; gap: 8px;
  }
  .card h2 .hint { font-weight: 400; font-size: 0.75rem; color: var(--muted); }
  table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
  th {
    text-align: left; padding: 8px 6px; border-bottom: 2px solid var(--navy);
    color: var(--navy); font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.04em;
  }
  td { padding: 8px 6px; border-bottom: 1px solid var(--border); vertical-align: middle; }
  tr:last-child td { border-bottom: none; }
  td.feat { font-weight: 600; }
  td.pct { white-space: nowrap; min-width: 110px; }
  .bar-wrap {
    display: inline-block; width: 64px; height: 8px; background: #e9edf2;
    border-radius: 4px; overflow: hidden; margin-right: 6px; vertical-align: middle;
  }
  .bar { height: 100%; border-radius: 4px; }
  .pill {
    display: inline-block; padding: 2px 8px; border-radius: 999px;
    font-size: 0.72rem; font-weight: 600;
  }
  .status-track { background: #e8f5e9; color: var(--track); }
  .status-risk { background: #ffebee; color: var(--risk); }
  .status-parked { background: #efebe9; color: var(--parked); }
  .charts {
    display: grid; grid-template-columns: 1fr 1fr; gap: 12px;
  }
  @media (max-width: 600px) { .charts { grid-template-columns: 1fr; } }
  .vchart {
    display: flex; align-items: flex-end; justify-content: space-around;
    height: 160px; padding: 8px 4px 0; gap: 8px;
  }
  .vbar { display: flex; flex-direction: column; align-items: center; justify-content: flex-end; flex: 1; }
  .vfill {
    width: 70%; max-width: 48px; background: linear-gradient(180deg, #42a5f5, var(--navy));
    border-radius: 6px 6px 2px 2px;
  }
  .vval { font-size: 0.85rem; font-weight: 700; color: var(--navy-dark); margin-top: 4px; }
  .vlbl { font-size: 0.7rem; color: var(--muted); }
  .pie-wrap { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; }
  .pie {
    width: 120px; height: 120px; border-radius: 50%;
    $pieStyle;
    box-shadow: inset 0 0 0 1px rgba(0,0,0,0.06);
  }
  .legend { list-style: none; margin: 0; padding: 0; font-size: 0.82rem; }
  .legend li { margin: 6px 0; display: flex; align-items: center; gap: 8px; }
  .swatch { width: 12px; height: 12px; border-radius: 3px; display: inline-block; }
  .mono { font-family: Consolas, "Courier New", monospace; font-size: 0.8rem; }
  .age-warn { color: var(--warn); font-weight: 700; }
  .age-crit { color: var(--risk); font-weight: 700; }
  .muted { color: var(--muted); }
  footer {
    margin-top: 16px; padding-top: 12px; border-top: 1px solid var(--border);
    font-size: 0.75rem; color: var(--muted); line-height: 1.45;
  }
  footer strong { color: #333; }
</style>
</head>
<body>
<header>
  <div>
    <h1>$Title $demoBadge</h1>
    <div class="tabs">
      <span class="tab active">Feature Progress</span>
      <span class="tab">Velocity</span>
      <span class="tab">Capacity</span>
      <span class="tab">Blockers</span>
    </div>
  </div>
  <div class="sprint">$([System.Net.WebUtility]::HtmlEncode($m.sprint_label)) - $([System.Net.WebUtility]::HtmlEncode($m.sprint_end))</div>
</header>

<section class="kpi-row">
  <div class="kpi">
    <div class="label">Features On Track</div>
    <div class="value">$($m.features_on_track) / $($m.features_total)</div>
    <div class="sub">$atRiskNote</div>
  </div>
  <div class="kpi">
    <div class="label">Velocity (last 3)</div>
    <div class="value" style="font-size:1.15rem">$velArrow</div>
    <div class="sub">pts / sprint - avg $($m.velocity_avg)</div>
  </div>
  <div class="kpi">
    <div class="label">Points Remaining</div>
    <div class="value">$($m.points_remaining)</div>
    <div class="sub">$pointsLabel - $healthyNote</div>
  </div>
  <div class="kpi">
    <div class="label">Capacity Used</div>
    <div class="value">$($m.capacity_used_pct)%</div>
    <div class="sub">Committed $($m.committed_points) / $($m.capacity_points) pts</div>
  </div>
  <div class="kpi$(if ($m.blocker_count -gt 0) { ' warn' } else { '' })">
    <div class="label">Open OPS Blockers</div>
    <div class="value">$($m.blocker_count) <span style="font-size:0.9rem;font-weight:600">(avg $($m.blocker_avg_age)d)</span></div>
    <div class="sub">$($m.blocker_critical_count) - 7 days</div>
  </div>
</section>

<section class="main">
  <div class="card">
    <h2>Feature % Complete <span class="hint">(primary leadership view)</span></h2>
    <table>
      <thead>
        <tr><th>Feature</th><th>Epic</th><th>% Done</th><th>Status</th><th>Target</th></tr>
      </thead>
      <tbody>
$($featureTableRows -join "`n")
      </tbody>
    </table>
  </div>
  <div class="card">
    <h2>Velocity &amp; Capacity</h2>
    <div class="charts">
      <div>
        <div class="label muted" style="margin-bottom:6px">Velocity (story points completed)</div>
        <div class="vchart">
$($velBars -join "`n")
        </div>
      </div>
      <div>
        <div class="label muted" style="margin-bottom:6px">$([System.Net.WebUtility]::HtmlEncode($m.sprint_label)) Capacity Allocation</div>
        <div class="pie-wrap">
          <div class="pie" title="Committed / Available / Overcommit"></div>
          <ul class="legend">
            <li><span class="swatch" style="background:#1F4E79"></span> Committed $($m.committed_points)</li>
            <li><span class="swatch" style="background:#90caf9"></span> Available $($m.available_points)</li>
            <li><span class="swatch" style="background:#e53935"></span> Overcommit $($m.overcommit_points)</li>
          </ul>
        </div>
      </div>
    </div>
  </div>
</section>

<section class="card">
  <h2>Open Blockers (OPS / Dependencies) <span class="hint">- escalated when age &gt; 5 business days</span></h2>
  <table>
    <thead>
      <tr><th>ID</th><th>Description</th><th>Type</th><th>Age</th><th>Owner</th><th>Impact</th></tr>
    </thead>
    <tbody>
$($blockerRows -join "`n")
    </tbody>
  </table>
</section>

<footer>
  <strong>Primary epic - feature - story cascade.</strong>
  Secondary: Epic status, velocity trend, Ready backlog health, capacity ratio, OPS blocker age.<br/>
  Do not surface story-level checklists on the leadership board - those live on the item in Git.<br/>
  Generated $(Get-Date -Format u) - Cascade leadership export - Normalized items: $($m.normalized_count)
  $(if ($m.is_demo) { '- Demo board (replace with live normalized data when meta + points are applied).' } else { '' })
</footer>
</body>
</html>
"@

    $html | Set-Content -Path $Path -Encoding UTF8
    Write-Host "Wrote $Path"
    return $Path
}

function Export-CascadeNormalizedJson {
    <#
    .SYNOPSIS
      Write normalized work-item array for offline review / adapter input.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Normalized,
        [string]$Path = (Join-Path (Get-Location) 'output\normalized_work_items.json')
    )
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $doc = [pscustomobject]@{
        schema    = 'cascade.normalized_batch.v1'
        generated = (Get-Date).ToString('o')
        count     = @($Normalized).Count
        items     = @($Normalized)
    }
    $doc | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding UTF8
    Write-Host "Wrote $Path ($($doc.count) items)"
    return $Path
}
