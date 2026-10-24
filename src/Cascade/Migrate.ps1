function Invoke-CascadeMigrateDryRun {
    <#
    .SYNOPSIS
      Propose type/title/metadata for fixture or live issues without writing to Git.
    .DESCRIPTION
      Uses heuristics + optional mapping CSV. Writes a plan CSV to output/.
    #>
    [CmdletBinding()]
    param(
        [object[]]$Issues,
        [string]$FixturePath,
        [string]$MappingCsv,
        [string]$OutPath = (Join-Path (Get-Location) 'output\migrate_plan.csv')
    )
    if (-not $Issues) {
        if ($FixturePath) { $Issues = Get-CascadeIssuesFromFixture -Path $FixturePath }
        else { $Issues = Get-CascadeIssuesFromFixture }
    }

    $map = @{}
    if ($MappingCsv -and (Test-Path $MappingCsv)) {
        Import-Csv $MappingCsv | ForEach-Object {
            $map[[int]$_.Iid] = $_
        }
    }

    $rows = foreach ($iss in $Issues) {
        $iid = [int]$iss.iid
        $title = [string]$iss.title
        $labels = @()
        if ($iss.labels) { $labels = @($iss.labels) }
        $ms = $null
        if ($iss.PSObject.Properties['milestone'] -and $iss.milestone) {
            if ($iss.milestone -is [string]) { $ms = $iss.milestone }
            elseif ($iss.milestone.title) { $ms = $iss.milestone.title }
        }

        $existing = Get-CascadeMeta -Body ([string]$iss.description)
        $suggestedType = 'STORY'
        $parent = ''
        $newTitle = $title

        if ($map.ContainsKey($iid)) {
            $m = $map[$iid]
            if ($m.Type) { $suggestedType = $m.Type.ToUpper() }
            if ($m.Parent) { $parent = $m.Parent }
            if ($m.NewTitle) { $newTitle = $m.NewTitle }
        }
        else {
            $suggestedType = Get-CascadeSuggestedType -Title $title -Labels $labels -Milestone $ms
            if ($title -notmatch '^\[(EPIC|FEATURE|STORY|TASK)\]') {
                $newTitle = "[$suggestedType] $title"
            }
        }

        $id = if ($existing -and $existing['id']) { $existing['id'] } else { "$suggestedType-$iid" }
        $already = [bool]$existing

        [pscustomobject]@{
            Iid            = $iid
            CurrentTitle   = $title
            ProposedTitle  = $newTitle
            ProposedType   = $suggestedType
            ProposedId     = $id
            ProposedParent = $parent
            AlreadyHasMeta = $already
            Milestone      = $ms
            Labels         = ($labels -join ';')
        }
    }

    $dir = Split-Path $OutPath -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $rows | Export-Csv -Path $OutPath -NoTypeInformation -Encoding UTF8
    Write-Host "Dry-run plan written: $OutPath  ($($rows.Count) rows)"
    return $rows
}

function Invoke-CascadeMigrateApply {
    <#
    .SYNOPSIS
      Apply migrate plan to GitLab (title + description metadata). Requires -Confirm or -Force.
    .NOTES
      Parent linking via Work Items API may need a follow-on call depending on git.mil version.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory)]
        [string]$PlanCsv,
        [switch]$Force
    )
    if (-not (Test-Path $PlanCsv)) { throw "Plan not found: $PlanCsv" }
    $plan = Import-Csv $PlanCsv
    $live = Get-CascadeIssuesFromGitLab -State all
    $byIid = @{}
    foreach ($i in $live) { $byIid[[int]$i.iid] = $i }

    foreach ($row in $plan) {
        $iid = [int]$row.Iid
        if (-not $byIid.ContainsKey($iid)) {
            Write-Warning ("Issue !{0} not found on server; skip" -f $iid)
            continue
        }
        if ($row.AlreadyHasMeta -eq 'True' -or $row.AlreadyHasMeta -eq $true) {
            Write-Host ("Skip !{0} (already has metadata)" -f $iid)
            continue
        }
        $iss = $byIid[$iid]
        $meta = @{
            type   = $row.ProposedType
            id     = $row.ProposedId
            state  = 'OPEN'
        }
        if ($row.ProposedParent) { $meta['parent'] = $row.ProposedParent }
        $meta['parent_iid'] = $(if ($row.PSObject.Properties.Name -contains 'ProposedParentIid' -and $row.ProposedParentIid) { [string]$row.ProposedParentIid } else { '' })

        $newDesc = Build-CascadeDescriptionWithMirror -Meta $meta -BodyMarkdown ([string]$iss.description)
        $targetTitle = $row.ProposedTitle

        if ($Force -or $PSCmdlet.ShouldProcess(("!{0} {1}" -f $iid, $iss.title), "Apply Cascade metadata + title")) {
            Set-CascadeGitLabIssue -Iid $iid -Title $targetTitle -Description $newDesc
            # Align GitLab open/closed with Cascade state when provided
            $st = [string]$meta['state']
            if ($st -match '(?i)^DONE') {
                try {
                    $base = Get-CascadeGitLabBase
                    $headers = Get-CascadeGitLabHeaders
                    $body = '{"state_event":"close"}'
                    Invoke-RestMethod -Method Put -Uri "$base/issues/$iid" -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 30 | Out-Null
                    Write-Host ("  Closed !{0} (Cascade state DONE)" -f $iid) -ForegroundColor DarkGray
                } catch {
                    Write-Warning ("Could not close !{0}: {1}" -f $iid, $_.Exception.Message)
                }
            }
            Write-Host ("Updated !{0}" -f $iid)
        }
    }
}
