<#
.SYNOPSIS
  Cascade taxonomy + agent defaults (Themes, linking, sprint capacity).
#>

function Get-CascadeRepoRoot {
    $root = $null
    try {
        if ($PSScriptRoot) {
            $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
            if (-not (Test-Path (Join-Path $root 'config'))) {
                $root = Split-Path $PSScriptRoot -Parent
            }
        }
    } catch {}
    if (-not $root) { $root = (Get-Location).Path }
    return $root
}

function Get-CascadeTaxonomy {
    [CmdletBinding()]
    param([string]$Path)
    if (-not $Path) {
        $Path = Join-Path (Get-CascadeRepoRoot) 'config\taxonomy.json'
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "taxonomy.json not found: $Path"
    }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-CascadeAgentDefaults {
    [CmdletBinding()]
    param([string]$Path)
    if (-not $Path) {
        $Path = Join-Path (Get-CascadeRepoRoot) 'config\agent_defaults.json'
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "agent_defaults.json not found: $Path"
    }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-CascadeThemeById {
    param([Parameter(Mandatory)][string]$ThemeId, $Taxonomy)
    if (-not $Taxonomy) { $Taxonomy = Get-CascadeTaxonomy }
    return @($Taxonomy.themes | Where-Object { $_.id -eq $ThemeId } | Select-Object -First 1)
}

function Format-CascadeTaxonomyTitle {
    param(
        [Parameter(Mandatory)][ValidateSet('THEME','EPIC','FEATURE','STORY')][string]$Type,
        [Parameter(Mandatory)][string]$Name
    )
    $n = $Name.Trim()
    $n = $n -replace '^(THEME|EPIC|FEATURE|STORY)\s*::\s*',''
    # The item type belongs in cascade-meta and type:: GitLab labels, not a
    # duplicated title prefix. Strip legacy prefixes but never rename
    # existing GitLab issues automatically.
    return $n
}

function New-CascadeSlug {
    param([string]$Text, [int]$Max = 40)
    $s = ($Text -replace '[^A-Za-z0-9]+','-').Trim('-').ToLowerInvariant()
    if ($s.Length -gt $Max) { $s = $s.Substring(0, $Max).Trim('-') }
    if (-not $s) { $s = 'item' }
    return $s
}

function New-CascadeMetaBlockFromTaxonomy {
    <#
    .SYNOPSIS
      Build ~~~cascade-meta block for a work item type.
    #>
    param(
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$Id,
        [string]$Theme = '',
        [string]$Parent = '',
        [string]$ParentIid = '',
        [string]$State = 'open',
        [string]$Area = '',
        [string]$Effort = '',
        [string]$Points = '',
        [string]$Sprint = '',
        [string]$Milestone = '',
        [string]$Status = '',  # Theme Active|Sunset
        [hashtable]$Extra
    )
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('~~~cascade-meta')
    [void]$lines.Add("type: $($Type.ToUpperInvariant())")
    [void]$lines.Add("id: $Id")
    if ($Theme) { [void]$lines.Add("theme: $Theme") } else { [void]$lines.Add('theme:') }
    if ($Parent) { [void]$lines.Add("parent: $Parent") } else { [void]$lines.Add('parent:') }
    if ($ParentIid) { [void]$lines.Add("parent_iid: $ParentIid") } else { [void]$lines.Add('parent_iid:') }
    [void]$lines.Add("state: $State")
    if ($Status) { [void]$lines.Add("status: $Status") }
    if ($Area) { [void]$lines.Add("area: $Area") }
    # Always emit type-appropriate sizing keys (use TBD rather than omit)
    $tu = $Type.ToUpperInvariant()
    if ($tu -eq 'FEATURE') {
        $e = if ($Effort) { $Effort } else { 'TBD' }
        [void]$lines.Add("effort: $e")
        [void]$lines.Add("artifact_level: $(if ($Extra -and $Extra['artifact_level']) { $Extra['artifact_level'] } else { 'Proposal for Work' })")
    }
    if ($tu -eq 'STORY') {
        $pt = if ($Points) { $Points } else { 'TBD' }
        $sp = if ($Sprint) { $Sprint } else { 'TBD' }
        [void]$lines.Add("points: $pt")
        [void]$lines.Add("sprint: $sp")
    }
    if ($tu -eq 'THEME' -and -not $Status) {
        [void]$lines.Add('status: active')
    }
    if ($Milestone) { [void]$lines.Add("milestone: $Milestone") }
    else { [void]$lines.Add('milestone:') }
    if ($Extra) {
        foreach ($k in ($Extra.Keys | Sort-Object)) {
            [void]$lines.Add(("{0}: {1}" -f $k, $Extra[$k]))
        }
    }
    [void]$lines.Add('~~~')
    return ($lines -join "`n")
}

function Add-CascadeChildLinkAppendOnly {
    <#
    .SYNOPSIS
      Append a child reference line to a parent description without removing existing links.
      Returns new description text.
    #>
    param(
        [Parameter(Mandatory)][string]$ParentDescription,
        [Parameter(Mandatory)][string]$ChildTitle,
        [string]$ChildIid = '',
        [string]$SectionHeader = '## Child items'
    )
    $desc = $ParentDescription
    if ($null -eq $desc) { $desc = '' }
    $line = if ($ChildIid) { "- $ChildTitle (!${ChildIid})" } else { "- $ChildTitle" }

    # Already present?
    if ($desc -match [regex]::Escape($ChildTitle)) {
        return $desc
    }
    if ($ChildIid -and $desc -match [regex]::Escape("!$ChildIid")) {
        return $desc
    }

    if ($desc -match [regex]::Escape($SectionHeader)) {
        # Insert after header line
        $parts = $desc -split "`n"
        $out = New-Object System.Collections.Generic.List[string]
        $inserted = $false
        for ($i = 0; $i -lt $parts.Count; $i++) {
            [void]$out.Add($parts[$i])
            if (-not $inserted -and $parts[$i].Trim() -eq $SectionHeader) {
                [void]$out.Add($line)
                $inserted = $true
            }
        }
        if (-not $inserted) { [void]$out.Add($line) }
        return ($out -join "`n")
    }

    return ($desc.TrimEnd() + "`n`n$SectionHeader`n$line`n")
}

function Test-CascadeStorySprintSized {
    <#
    .SYNOPSIS
      Agent guard: story should be finishable in one sprint (heuristic).
    #>
    param(
        [string]$Title,
        [string[]]$AcceptanceCriteria,
        [int]$Points = 0
    )
    $defaults = $null
    try { $defaults = Get-CascadeAgentDefaults } catch {}
    $warns = New-Object System.Collections.Generic.List[string]
    if (-not $AcceptanceCriteria -or $AcceptanceCriteria.Count -eq 0) {
        [void]$warns.Add('Story has no acceptance criteria - split or add testable AC for one sprint')
    }
    if ($Points -gt 13) {
        [void]$warns.Add('Points > 13 - likely larger than one sprint; split')
    }
    if ($Title -match '(?i)\b(modernize|entire|full platform|all pipelines|end-to-end system)\b') {
        [void]$warns.Add('Title suggests Epic/Feature scope - keep long bake on Epic; Story must be a finished slice')
    }
    if ($defaults) {
        [void]$warns.Add(("Sprint length default: {0} days; progress = finished slices not full Epic bake" -f $defaults.sprint.length_days))
    }
    return , $warns.ToArray()
}

function Get-CascadePlanningCapacityAdvice {
    param([int]$TeamSize = 6, [double]$CommittedPct = 0)
    $d = Get-CascadeAgentDefaults
    if ($CommittedPct -le 0) { $CommittedPct = [double]$d.capacity.default_committed_pct }
    $daysPerPerson = [double]$d.sprint.length_days * ($CommittedPct / 100.0)
    # rough: exclude ~2 meeting days equivalent already in pct
    $teamDays = [math]::Round($daysPerPerson * $TeamSize, 1)
    return [pscustomobject]@{
        SprintDays            = $d.sprint.length_days
        CommittedPct          = $CommittedPct
        InterruptBufferPct    = $d.capacity.interrupt_buffer_pct
        TeamSize              = $TeamSize
        ApproxCommittedPersonDays = $teamDays
        Advice                = @(
            "Commit about ${CommittedPct}% so multi-domain interrupts do not break the board",
            'Long technical bake is tracked on Epic + Milestone; each sprint still finishes slices',
            'Do not plan into the interrupt buffer at sprint start',
            "Epic review cadence: $($d.cadence.epic_review)"
        )
    }
}
