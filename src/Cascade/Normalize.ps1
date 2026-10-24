# Normalize GitLab issues into Cascade-shaped objects (metadata-ready for hierarchy + leadership dashboard).

function Get-CascadeLabelList {
    param($Issue)
    $labs = @()
    try {
        if ($Issue.labels -is [System.Array]) {
            $labs = @($Issue.labels | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
        } elseif ($Issue.labels) {
            $labs = @([string]$Issue.labels -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    } catch { }
    return , $labs
}

function Get-CascadeLabelValue {
    <#
    .SYNOPSIS
      Extract value after prefix:: from labels (e.g. status::In Progress -> In Progress).
    #>
    param(
        [string[]]$Labels,
        [string]$Prefix
    )
    if (-not $Labels) { return $null }
    $p = $Prefix.ToLowerInvariant().TrimEnd(':')
    foreach ($l in $Labels) {
        $s = [string]$l
        if ($s -match ("(?i)^{0}::\s*(.+)$" -f [regex]::Escape($p))) {
            return $Matches[1].Trim()
        }
    }
    return $null
}

function Get-CascadeInferredTypeFromIssue {
    param($Issue)
    $title = [string]$Issue.title
    $labs = Get-CascadeLabelList -Issue $Issue
    $joined = ($labs -join ' ').ToLowerInvariant()

    if ($joined -match 'type::epic|\bepic\b' -or $title -match '^\s*\[?\s*EPIC') { return 'EPIC' }
    if ($joined -match 'type::feature|\bfeature\b' -or $title -match '^\s*\[?\s*FEATURE') { return 'FEATURE' }
    if ($joined -match 'type::ops|\bops\b' -or $title -match '(?i)^INC\d+') { return 'STORY' }
    if ($joined -match 'type::task' -or $title -match '^\s*\[?\s*TASK') { return 'STORY' }
    if ($joined -match 'type::story|\bstory\b') { return 'STORY' }

    if ($title -match '(?i)\b(platform|foundation|initiative|program)\b') { return 'FEATURE' }
    return 'STORY'
}

function Convert-CascadeEffortBandToPoints {
    <#
    .SYNOPSIS
      Map SMPG effort_band (XS..XL) to story points. Unknown -> 0.
    #>
    param([string]$Band)
    switch -Regex ("$Band".Trim().ToUpperInvariant()) {
        '^(XS|1)$' { return 1 }
        '^(S|2)$'  { return 2 }
        '^(M|3)$'  { return 3 }
        '^(L|5)$'  { return 5 }
        '^(XL|8)$' { return 8 }
        '^\d+$'    { return [int]$Band }
        default    { return 0 }
    }
}

function Get-CascadeBusinessDayAge {
    <#
    .SYNOPSIS
      Count Mon-Fri days between a start datetime and now (UTC-ish). Weekends skipped.
    #>
    param(
        [datetime]$From,
        [datetime]$To = (Get-Date)
    )
    if ($To -lt $From) { return 0 }
    $days = 0
    $cursor = $From.Date
    $end = $To.Date
    while ($cursor -lt $end) {
        $cursor = $cursor.AddDays(1)
        if ($cursor.DayOfWeek -ne [DayOfWeek]::Saturday -and $cursor.DayOfWeek -ne [DayOfWeek]::Sunday) {
            $days++
        }
    }
    return $days
}

function Get-CascadeMilestoneTitle {
    param($Issue)
    try {
        if ($null -eq $Issue.milestone) { return '' }
        if ($Issue.milestone -is [string]) { return [string]$Issue.milestone }
        if ($Issue.milestone.PSObject.Properties['title']) { return [string]$Issue.milestone.title }
        return [string]$Issue.milestone
    } catch { return '' }
}

function Get-CascadeOwnerName {
    param($Issue)
    try {
        $a = $Issue.assignees
        if (-not $a) { return '' }
        if ($a -is [System.Array]) {
            $names = @($a | ForEach-Object {
                    if ($_.username) { [string]$_.username }
                    elseif ($_.name) { [string]$_.name }
                    else { "$_" }
                } | Where-Object { $_ })
            return ($names -join ', ')
        }
        if ($a.username) { return [string]$a.username }
        return [string]$a
    } catch { return '' }
}

function Test-CascadeIsBlocker {
    param(
        [string]$State,
        [string[]]$Labels,
        [string]$Title,
        [hashtable]$Meta
    )
    $s = "$State".ToUpperInvariant()
    if ($s -eq 'BLOCKED') { return $true }
    if ($Meta -and "$($Meta['blocked'])" -match '^(?i)1|true|yes$') { return $true }
    $joined = (($Labels | ForEach-Object { "$_" }) -join ' ').ToLowerInvariant()
    if ($joined -match 'status::blocked|blocker|type::ops|dependency|blocked') { return $true }
    if ($Title -match '(?i)\b(blocked|waiting on|dependency)\b') { return $true }
    return $false
}

function Get-CascadeBlockerType {
    param(
        [string[]]$Labels,
        [string]$Title,
        [hashtable]$Meta
    )
    if ($Meta -and $Meta['blocker_type']) { return ("{0}" -f $Meta['blocker_type']).ToUpper() }
    $joined = (($Labels | ForEach-Object { "$_" }) -join ' ').ToLowerInvariant()
    if ($joined -match 'type::ops|\bops\b' -or $Title -match '(?i)^INC\d+') { return 'OPS' }
    if ($joined -match 'external|dependency') { return 'External' }
    if ($joined -match 'tech::|infra::') { return 'Tech' }
    return 'OPS'
}

function Convert-CascadeGitLabIssueToNormalized {
    <#
    .SYNOPSIS
      Map one GitLab issue to a Cascade-normalized work item (no Git write).
      Adds proposed Type/Id/Parent, dashboard fields, and a draft metadata block.
    .DESCRIPTION
      Leadership-dashboard fields:
        status_label, priority, area, tech, owner, effort_band, story_points,
        target, sprint, age_business_days, is_blocker, blocker_type
      Meta keys written when missing: type, id, parent, parent_iid, state,
      and optional effort_band / story_points / target / sprint / area when known.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Issue,
        [switch]$AsDuplicate,
        [string]$DuplicateSuffix = 'CASCADE-TEST'
    )

    $iid = 0
    try { $iid = [int]$Issue.iid } catch { }
    $title = [string]$Issue.title
    $desc = ''
    try { $desc = [string]$Issue.description } catch { }
    $state = 'OPEN'
    try {
        $gs = [string]$Issue.state
        if ($gs -match '(?i)^closed$') { $state = 'DONE' }
    } catch { }

    $labs = @(Get-CascadeLabelList -Issue $Issue)
    $existingMeta = $null
    if ($desc) {
        try { $existingMeta = Get-CascadeMeta -Body $desc } catch { }
    }

    $type = 'STORY'
    $id = $null
    $parent = $null
    if ($existingMeta) {
        if ($existingMeta['type']) { $type = ("{0}" -f $existingMeta['type']).ToUpper() }
        if ($existingMeta['id']) { $id = [string]$existingMeta['id'] }
        if ($existingMeta['parent']) { $parent = [string]$existingMeta['parent'] }
        if ($existingMeta['state']) { $state = ("{0}" -f $existingMeta['state']).ToUpper() }
        elseif ($existingMeta['status']) { $state = ("{0}" -f $existingMeta['status']).ToUpper() }
    } else {
        $type = Get-CascadeInferredTypeFromIssue -Issue $Issue
    }

    # Label-driven status (does not override Cascade state DONE)
    $statusLabel = Get-CascadeLabelValue -Labels $labs -Prefix 'status'
    if ($statusLabel -and $state -ne 'DONE') {
        $sl = $statusLabel.ToUpperInvariant()
        if ($sl -match 'IN\s*PROGRESS|DOING|ACTIVE') { $state = 'IN_PROGRESS' }
        elseif ($sl -match 'READY|TO\s*DO') { $state = 'READY' }
        elseif ($sl -match 'BLOCK') { $state = 'BLOCKED' }
        elseif ($sl -match 'BACKLOG') { $state = 'OPEN' }
        elseif ($sl -match 'DONE|CLOSED|COMPLETE') { $state = 'DONE' }
    }

    if (-not $id) {
        $slug = ($title -replace '[^a-zA-Z0-9]+', '-').Trim('-').ToLowerInvariant()
        if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40).TrimEnd('-') }
        $id = '{0}-{1}-{2}' -f $type.ToLowerInvariant(), $iid, $slug
    }

    if ($AsDuplicate) {
        $title = '[CASCADE-DUP] {0}' -f $title
        $id = '{0}-{1}' -f $id, $DuplicateSuffix.ToLowerInvariant()
        $iid = 0
    }

    $priority = Get-CascadeLabelValue -Labels $labs -Prefix 'priority'
    $area = Get-CascadeLabelValue -Labels $labs -Prefix 'area'
    $tech = Get-CascadeLabelValue -Labels $labs -Prefix 'tech'
    $owner = Get-CascadeOwnerName -Issue $Issue
    $milestone = Get-CascadeMilestoneTitle -Issue $Issue

    $effortBand = $null
    $storyPoints = 0
    $target = $null
    $sprint = $null
    if ($existingMeta) {
        if ($existingMeta['effort_band']) { $effortBand = [string]$existingMeta['effort_band'] }
        elseif ($existingMeta['effort']) { $effortBand = [string]$existingMeta['effort'] }
        if ($existingMeta['story_points']) {
            try { $storyPoints = [int]$existingMeta['story_points'] } catch { $storyPoints = 0 }
        }
        if ($existingMeta['points']) {
            try { $storyPoints = [int]$existingMeta['points'] } catch { }
        }
        if ($existingMeta['target']) { $target = [string]$existingMeta['target'] }
        if ($existingMeta['sprint']) { $sprint = [string]$existingMeta['sprint'] }
        if (-not $area -and $existingMeta['area']) { $area = [string]$existingMeta['area'] }
    }
    if (-not $effortBand) {
        $effortBand = Get-CascadeLabelValue -Labels $labs -Prefix 'effort'
    }
    if ($storyPoints -le 0 -and $effortBand) {
        $storyPoints = Convert-CascadeEffortBandToPoints -Band $effortBand
    }
    # points::N label fallback
    if ($storyPoints -le 0) {
        $ptsLabel = Get-CascadeLabelValue -Labels $labs -Prefix 'points'
        if ($ptsLabel -match '^\d+$') { $storyPoints = [int]$ptsLabel }
    }

    $createdAt = $null
    try {
        if ($Issue.created_at) { $createdAt = [datetime]$Issue.created_at }
    } catch { }
    $ageBiz = 0
    if ($createdAt) { $ageBiz = Get-CascadeBusinessDayAge -From $createdAt }

    $isBlocker = Test-CascadeIsBlocker -State $state -Labels $labs -Title $title -Meta $existingMeta
    $blockerType = if ($isBlocker) { Get-CascadeBlockerType -Labels $labs -Title $title -Meta $existingMeta } else { '' }

    $metaMap = [ordered]@{
        type       = $type
        id         = $id
        parent     = $(if ($parent) { $parent } else { '' })
        parent_iid = $(if ($existingMeta -and $existingMeta['parent_iid']) { [string]$existingMeta['parent_iid'] } else { '' })
        state      = $state
    }
    if ($effortBand) { $metaMap['effort_band'] = $effortBand }
    if ($storyPoints -gt 0) { $metaMap['story_points'] = "$storyPoints" }
    if ($target) { $metaMap['target'] = $target }
    if ($sprint) { $metaMap['sprint'] = $sprint }
    if ($area) { $metaMap['area'] = $area }
    if ($isBlocker) { $metaMap['blocked'] = 'true'; $metaMap['blocker_type'] = $blockerType }

    $deficiencies = @(Get-CascadeWorkItemDeficiencies -Meta $(if ($existingMeta) { $existingMeta } else { $metaMap }) -Body $desc -Title $title -InferredType $type)
    if ($type -eq 'FEATURE') {
        $deficiencies = @($deficiencies + @('feature_needs_story_coverage_check') | Select-Object -Unique)
    }

    $newDesc = if ($existingMeta -and $existingMeta['type']) {
        if ($desc -notmatch '(?m)^###\s*Cascade') {
            Build-CascadeDescriptionWithMirror -Meta $metaMap -BodyMarkdown $desc
        } else { $desc }
    } else {
        Build-CascadeDescriptionWithMirror -Meta $metaMap -BodyMarkdown $desc
    }

    return [pscustomobject]@{
        schema             = 'cascade.normalized_issue.v1'
        source_iid         = $(try { [int]$Issue.iid } catch { 0 })
        source_id          = $(try { [string]$Issue.id } catch { '' })
        is_duplicate       = [bool]$AsDuplicate
        title              = $title
        description        = $newDesc
        state              = $state
        labels             = $labs
        web_url            = $(try { [string]$Issue.web_url } catch { '' })
        milestone          = $milestone
        proposed_type      = $type
        proposed_id        = $id
        proposed_parent    = $parent
        already_has_meta   = [bool]$existingMeta
        deficiencies       = @($deficiencies)
        iid                = $(if ($AsDuplicate) { 0 } else { $(try { [int]$Issue.iid } catch { 0 }) })
        # dashboard / leadership fields
        status_label       = $(if ($statusLabel) { $statusLabel } else { $state })
        priority           = $(if ($priority) { $priority } else { '' })
        area               = $(if ($area) { $area } else { '' })
        tech               = $(if ($tech) { $tech } else { '' })
        owner              = $owner
        effort_band        = $(if ($effortBand) { $effortBand } else { '' })
        story_points       = [int]$storyPoints
        target             = $(if ($target) { $target } else { '' })
        sprint             = $(if ($sprint) { $sprint } else { $milestone })
        created_at         = $(if ($createdAt) { $createdAt.ToString('o') } else { '' })
        age_business_days  = [int]$ageBiz
        is_blocker         = [bool]$isBlocker
        blocker_type       = $blockerType
        source_issue       = $Issue
    }
}

function Convert-CascadeGitLabIssuesToNormalized {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Issues,
        [switch]$AsDuplicate,
        [string]$OutPath
    )
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($iss in $Issues) {
        if ($null -eq $iss) { continue }
        if ($iss -is [System.Array]) {
            foreach ($inner in $iss) {
                if ($null -ne $inner) {
                    [void]$list.Add((Convert-CascadeGitLabIssueToNormalized -Issue $inner -AsDuplicate:$AsDuplicate))
                }
            }
        } else {
            [void]$list.Add((Convert-CascadeGitLabIssueToNormalized -Issue $iss -AsDuplicate:$AsDuplicate))
        }
    }
    $arr = $list.ToArray()
    if ($OutPath) {
        $dir = Split-Path -Parent $OutPath
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        , $arr | ConvertTo-Json -Depth 10 | Set-Content -Path $OutPath -Encoding UTF8
        Write-Host ("Wrote {0} normalized issue(s) -> {1}" -f $arr.Count, $OutPath)
    }
    return , $arr
}

function Convert-CascadeNormalizedToPlanDoc {
    <#
    .SYNOPSIS
      Normalized issues -> cascade.plan.v1 document (same shape as migrate_plan JSON).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Normalized,
        [string]$OutPath
    )
    $items = foreach ($n in $Normalized) {
        [pscustomobject]@{
            iid              = [int]$n.source_iid
            current_title    = [string]$n.title
            proposed_title   = [string]$n.title
            proposed_type    = [string]$n.proposed_type
            proposed_id      = [string]$n.proposed_id
            proposed_parent  = [string]$n.proposed_parent
            already_has_meta = [bool]$n.already_has_meta
            milestone        = [string]$n.milestone
            labels           = @($n.labels)
            deficiencies     = @($n.deficiencies)
            story_points     = [int]$n.story_points
            effort_band      = [string]$n.effort_band
            owner            = [string]$n.owner
            is_blocker       = [bool]$n.is_blocker
            priority         = [string]$n.priority
            area             = [string]$n.area
        }
    }
    $doc = [pscustomobject]@{
        schema     = 'cascade.plan.v1'
        generated  = (Get-Date).ToString('o')
        source     = 'gitlab_normalized'
        count      = @($items).Count
        items      = @($items)
    }
    if ($OutPath) {
        $doc | ConvertTo-Json -Depth 8 | Set-Content -Path $OutPath -Encoding UTF8
        Write-Host "Wrote $OutPath"
    }
    return $doc
}
