function Get-CascadeMeta {
    <#
    .SYNOPSIS
      Parse Cascade metadata block from a Git issue / work item description.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        $Body
    )
    if ($null -eq $Body) { return $null }
    if ($Body -is [System.Array]) { $Body = ($Body | ForEach-Object { "$_" }) -join "`n" }
    else { $Body = [string]$Body }
    if ([string]::IsNullOrWhiteSpace($Body)) { return $null }

    $block = $null
    if ($Body -match '(?s)~~~cascade-meta\s*(.*?)\s*~~~') {
        $block = $Matches[1]
    } elseif ($Body -match '(?s)<!--\s*(.*?)\s*-->') {
        $block = $Matches[1]
    } else {
        return $null
    }

    $meta = @{}
    foreach ($line in ($block -split "`r?`n")) {
        $trim = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trim)) { continue }
        if ($trim -notmatch '^\s*([^:]+):\s*(.*)\s*$') { continue }
        $key = $Matches[1].Trim().ToLower()
        $val = $Matches[2].Trim()
        $meta[$key] = $val
    }
    if (-not $meta.ContainsKey('type') -and -not $meta.ContainsKey('id')) { return $null }
    return $meta
}

function Test-CascadeMeta {
    [CmdletBinding()]
    param($Meta)
    if (-not $Meta) { return $false }
    $type = "$($Meta['type'])".ToUpper()
    if ($type -notin @('THEME','EPIC','FEATURE','STORY','TASK','PROPOSAL','SOW','PFW','DWI')) { return $false }
    if ([string]::IsNullOrWhiteSpace($Meta['id'])) { return $false }
    return $true
}

function New-CascadeMetaBlock {
    <#
    .SYNOPSIS
      Build the HTML comment metadata block string.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Meta
    )
    $lines = foreach ($k in ($Meta.Keys | Sort-Object)) {
        "$k`: $($Meta[$k])"
    }
    return "<!--`n$($lines -join "`n")`n-->"
}

function Build-CascadeDescription {
    <#
    .SYNOPSIS
      Compose description = metadata block + body markdown (strips any prior Cascade block).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Meta,
        [string]$BodyMarkdown = ''
    )
    # Strip existing cascade-looking first comment block
    $clean = $BodyMarkdown
    if ($clean -match '(?s)^\s*<!--\s*.*?\s*-->\s*') {
        $clean = $clean -replace '(?s)^\s*<!--\s*.*?\s*-->\s*', ''
    }
    $block = New-CascadeMetaBlock -Meta $Meta
    return "$block`n`n$($clean.Trim())"
}

function Get-CascadeSuggestedType {
    <#
    .SYNOPSIS
      Heuristic type suggestion from title/labels/milestone (for migration dry-run).
    #>
    [CmdletBinding()]
    param(
        [string]$Title,
        [string[]]$Labels,
        [string]$Milestone
    )
    $t = "$Title".ToLower()
    if ($t -match '^inc\d+') { return 'STORY' }
    if ($t -match 'research |poc |spike ') { return 'STORY' }
    if ($Milestone -and $Milestone -notmatch '^\s*$') {
        # Milestone children often act as stories/tasks under a feature named after the milestone
        return 'STORY'
    }
    if ($t -match 'build out |enable |establish |populate ') { return 'FEATURE' }
    return 'STORY'
}

function Get-CascadeDorRules {
    [CmdletBinding()]
    param([string]$Path)
    if (-not $Path) {
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $Path = Join-Path $root 'fixtures\dor_rules.json'
        if (-not (Test-Path $Path)) { $Path = Join-Path (Get-Location) 'fixtures\dor_rules.json' }
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Test-CascadeBodyHasAcceptanceCriteria {
    param([string]$Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return $false }
    # Heading or labeled section with at least one bullet-like line after
    if ($Body -match '(?im)acceptance\s*criteria') {
        # need some criterion content beyond the heading
        if ($Body -match '(?im)acceptance\s*criteria[\s\S]{0,200}(-\s+\S|\*\s+\S|\d+\.\s+\S)') { return $true }
        if ($Body -match '(?im)criterion') { return $true }
    }
    return $false
}

function Test-CascadeBodyHasDoneWhen {
    param([string]$Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return $false }
    if ($Body -match '(?im)(done-?\s*when|feature\s+done-?\s*when|completion\s+criteria)') {
        if ($Body -match '(?im)(done-?\s*when|completion\s+criteria)[\s\S]{0,200}(-\s+\S|\*\s+\S|\d+\.\s+\S)') { return $true }
    }
    return $false
}

function Test-CascadeBodyHasStoryText {
    param([string]$Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return $false }
    $clean = $Body -replace '(?s)<!--.*?-->', '' -replace '(?s)###\s*Cascade[\s\S]{0,400}', ''
    $clean = $clean.Trim()
    if ($clean.Length -lt 20) { return $false }
    return $true
}

function Get-CascadeWorkItemDeficiencies {
    <#
    .SYNOPSIS
      Evaluate an issue body + meta against SMPG template DoR rules.
    #>
    [CmdletBinding()]
    param(
        $Meta,
        [string]$Body,
        [string]$Title,
        [string]$InferredType
    )

    $codes = New-Object System.Collections.Generic.List[string]
    $type = $InferredType
    if ($Meta -and $Meta['type']) { $type = ("{0}" -f $Meta['type']).ToUpper() }
    if ([string]::IsNullOrWhiteSpace($type)) { $type = 'STORY' }

    if (-not $Meta -or -not (Test-CascadeMeta -Meta $Meta)) {
        [void]$codes.Add('missing_cascade_metadata')
    }

    $parent = ''
    if ($Meta -and $Meta['parent']) { $parent = [string]$Meta['parent'] }

    switch ($type) {
        'STORY' {
            if ([string]::IsNullOrWhiteSpace($parent)) { [void]$codes.Add('story_or_task_missing_feature_parent') }
            if (-not (Test-CascadeBodyHasAcceptanceCriteria -Body $Body)) {
                [void]$codes.Add('story_missing_acceptance_criteria')
            }
            if (-not (Test-CascadeBodyHasStoryText -Body $Body)) {
                [void]$codes.Add('story_missing_story_text')
            }
        }
        'TASK' {
            if ([string]::IsNullOrWhiteSpace($parent)) { [void]$codes.Add('task_missing_story_parent') }
        }
        'FEATURE' {
            if ([string]::IsNullOrWhiteSpace($parent)) { [void]$codes.Add('feature_missing_epic_parent') }
            if (-not (Test-CascadeBodyHasDoneWhen -Body $Body)) {
                [void]$codes.Add('feature_missing_done_when')
            }
            if (-not (Test-CascadeBodyHasStoryText -Body $Body)) {
                [void]$codes.Add('feature_missing_short_description')
            }
        }
        'EPIC' {
            if ($Body -notmatch '(?im)outcome') { [void]$codes.Add('epic_missing_outcome_statement') }
            if ($Body -notmatch '(?im)(success\s*picture|success\s*criterion)') {
                [void]$codes.Add('epic_missing_success_picture')
            }
        }
    }

    return , $codes.ToArray()
}

function New-CascadeVisibleMirror {
    <#
    .SYNOPSIS
      Human-visible markdown mirror of Cascade meta (templates + UI readability).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Meta)

    $lines = @(
        '### Cascade'
        ('- **Type:** {0}' -f $(if ($Meta['type']) { $Meta['type'] } else { '' }))
        ('- **Id:** {0}' -f $(if ($Meta['id']) { $Meta['id'] } else { '' }))
        ('- **Parent:** {0}' -f $(if ($Meta['parent']) { $Meta['parent'] } else { '(none)' }))
        ('- **Parent iid:** {0}' -f $(if ($Meta['parent_iid']) { $Meta['parent_iid'] } else { '(none)' }))
        ('- **State:** {0}' -f $(if ($Meta['state']) { $Meta['state'] } else { 'OPEN' }))
    )
    if ($Meta['artifact_level']) {
        $lines += ('- **Artifacts:** {0}' -f $Meta['artifact_level'])
    }
    if ($Meta['proposal_id'] -or $Meta['proposal_iid']) {
        $lines += ('- **Proposal:** {0} / iid={1}' -f $Meta['proposal_id'], $Meta['proposal_iid'])
    }
    if ($Meta['effort']) {
        $lines += ('- **Effort:** {0}' -f $Meta['effort'])
    }
    elseif ($Meta['effort_band']) {
        $lines += ('- **Effort:** {0}' -f $Meta['effort_band'])
    }
    if ($Meta['time_estimate_hours']) {
        $lines += ('- **Time estimate (h):** {0}' -f $Meta['time_estimate_hours'])
    }
    if ($Meta['fte_weeks']) {
        $lines += ('- **FTE weeks:** {0}' -f $Meta['fte_weeks'])
    }
    if ($Meta['story_points'] -or $Meta['points']) {
        $lines += ('- **Story points:** {0}' -f $(if ($Meta['story_points']) { $Meta['story_points'] } else { $Meta['points'] }))
    }
    if ($Meta['target']) {
        $lines += ('- **Target:** {0}' -f $Meta['target'])
    }
    if ($Meta['sprint']) {
        $lines += ('- **Sprint:** {0}' -f $Meta['sprint'])
    }
    if ($Meta['blocked'] -or $Meta['blocker_type']) {
        $lines += ('- **Blocked:** {0} ({1})' -f $(if ($Meta['blocked']) { $Meta['blocked'] } else { 'true' }), $(if ($Meta['blocker_type']) { $Meta['blocker_type'] } else { 'OPS' }))
    }
    if ($Meta['child_iids']) {
        $lines += ('- **Child iids:** {0}' -f $Meta['child_iids'])
    }
    if ($Meta['children']) {
        $lines += ('- **Children:** {0}' -f $Meta['children'])
    }
    if ($Meta['capacity_band']) {
        $lines += ('- **Capacity band:** {0}' -f $Meta['capacity_band'])
    }
    if ($Meta['product_owner']) { $lines += ('- **Product Owner:** {0}' -f $Meta['product_owner']) }
    if ($Meta['technical_focal']) { $lines += ('- **Technical Focal:** {0}' -f $Meta['technical_focal']) }
    if ($Meta['sme']) { $lines += ('- **SME:** {0}' -f $Meta['sme']) }
    return ($lines -join "`n")
}

function Build-CascadeDescriptionWithMirror {
    <#
    .SYNOPSIS
      Metadata HTML comment + visible Cascade mirror + body (strips prior cascade blocks/mirrors).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Meta,
        [string]$BodyMarkdown = ''
    )
    $clean = $BodyMarkdown
    if ($clean -match '(?s)^\s*<!--\s*.*?\s*-->\s*') {
        $clean = $clean -replace '(?s)^\s*<!--\s*.*?\s*-->\s*', ''
    }
    # strip prior visible mirror
    $clean = $clean -replace '(?ms)^\s*###\s*Cascade\s*\r?\n(?:-\s*\*\*[^\r\n]+\r?\n)+', ''
    $block = New-CascadeMetaBlock -Meta $Meta
    $mirror = New-CascadeVisibleMirror -Meta $Meta
    return "$block`n`n$mirror`n`n$($clean.Trim())"
}
