#requires -Version 5.1
<#
.SYNOPSIS
  Exact GitLab issue label filter. No inferred team membership, no writes.
.DESCRIPTION
  An empty filter includes every issue. A nonempty filter includes ONLY issues
  whose GitLab labels array contains the selected label (case-insensitive
  exact match). Titles, project paths and cascade-meta are never considered.
  Unattributed project-level commits, MRs and pipelines remain in the raw
  snapshot but are not shown in a label-specific delivery/evidence view.
#>
function Assert-CascadeIssueLabelFilter {
    [CmdletBinding()]
    param([AllowEmptyString()][string]$Label='')
    $value=([string]$Label).Trim()
    if($value.Length -gt 100 -or $value -match '[\x00-\x1f\x7f]'){
        throw 'Label must be 100 characters or fewer and contain no control characters.'
    }
    return $value
}
function Test-CascadeIssueHasLabel {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Issue,[AllowEmptyString()][string]$Label='')
    $wanted=Assert-CascadeIssueLabelFilter -Label $Label
    if($wanted.Length -eq 0){return $true}
    foreach($item in @($Issue.labels)){
        if([string]::Equals(([string]$item).Trim(),$wanted,[System.StringComparison]::OrdinalIgnoreCase)){
            return $true
        }
    }
    return $false
}
function Get-CascadeScopedGitMilSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Snapshot,[AllowEmptyString()][string]$Label='')
    $wanted=Assert-CascadeIssueLabelFilter -Label $Label
    $labelMode=($wanted.Length -gt 0)
    $sourceIssues=@($Snapshot.Issues | Where-Object {$null -ne $_})
    $included=New-Object System.Collections.ArrayList
    # Label suggestions are built from the full READ snapshot (not inferred).
    $availableLabels=New-Object System.Collections.ArrayList
    foreach($issue in $sourceIssues){
        foreach($item in @($issue.labels)){
            $name=[string]$item
            if(-not [string]::IsNullOrWhiteSpace($name) -and $name.Length -le 100){
                [void]$availableLabels.Add($name)
            }
        }
    }
    foreach($issue in $sourceIssues){
        if(Test-CascadeIssueHasLabel -Issue $issue -Label $wanted){
            [void]$included.Add($issue)
        }
    }
    # Do not claim any generic project commit belongs to issues matching a
    # label: there is no issue-level linkage in the current Git.mil snapshot.
    $activity=New-Object System.Collections.ArrayList
    $merges=New-Object System.Collections.ArrayList
    $pipelines=New-Object System.Collections.ArrayList
    if(-not $labelMode){
        foreach($item in @($Snapshot.Activity)){if($null -ne $item){[void]$activity.Add($item)}}
        foreach($item in @($Snapshot.MergeRequests)){if($null -ne $item){[void]$merges.Add($item)}}
        foreach($item in @($Snapshot.Pipelines)){if($null -ne $item){[void]$pipelines.Add($item)}}
    }
    $remote=$Snapshot.LabelQuery
    $remoteCurrent=($labelMode -and $null -ne $remote -and
        [string]::Equals([string]$remote.Label,$wanted,[StringComparison]::OrdinalIgnoreCase) -and
        [bool]$remote.Performed)
    $remoteErrors=if($remoteCurrent){[int]$remote.ProjectFailures}else{0}
    $zeroMessage=if($labelMode -and $included.Count -eq 0){
        if(-not $remoteCurrent){
            'No issue in the cached snapshot carries this exact label. Synchronize selected Git.mil projects to run a server-side label search.'
        }elseif($remoteErrors -gt 0){
            'The label query did not complete for every selected project. Review connection diagnostics and retry.'
        }else{
            'Git.mil returned no issue carrying this exact label from selected projects. Verify the label name and project selection. Native GitLab Epics may require a separate work-items API.'
        }
    }else{''}
    $counts=[pscustomobject]@{
        Mode=if($labelMode){'label'}else{'all'}
        Label=$wanted
        SourceIssueCount=$sourceIssues.Count
        IncludedIssueCount=$included.Count
        AvailableLabels=@($availableLabels.ToArray() | Sort-Object -Unique)
        ExcludedIssueCount=($sourceIssues.Count-$included.Count)
        ServerLabelQueryPerformed=$remoteCurrent
        ServerLabelQueryFailures=$remoteErrors
        ServerLabelMatches=if($remoteCurrent){[int]$remote.MatchesReturned}else{0}
        ServerLabelAddedIssues=if($remoteCurrent){[int]$remote.AdditionalIssuesImported}else{0}
        ServerLabelPageLimitReached=if($remoteCurrent){[bool]$remote.PageLimitPossiblyReached}else{$false}
        ZeroResultsExplanation=$zeroMessage
        SourceActivityCount=@($Snapshot.Activity).Count
        IncludedActivityCount=$activity.Count
        ExcludedActivityCount=(@($Snapshot.Activity).Count-$activity.Count)
        Warning=if($labelMode){
            'Exact GitLab issue label match, not inferred project membership. Unattributed project commits/MRs/pipelines are hidden. '+
            $(if($remoteCurrent){'A remote GitLab labels query was also performed for this label; page or permission limits may still apply.'}else{'The cached source may be incomplete for this label until a targeted Git.mil sync.'})
        }else{
            'Showing all issues and project-level activity from selected Git.mil projects. Project activity is not proof of work-item completion.'
        }
    }
    return [pscustomobject]@{
        SchemaVersion=$Snapshot.SchemaVersion
        Source=$Snapshot.Source
        GeneratedUtc=$Snapshot.GeneratedUtc
        SinceDays=$Snapshot.SinceDays
        User=$Snapshot.User
        ArcLoaded=$Snapshot.ArcLoaded
        Projects=@($Snapshot.Projects)
        Activity=@($activity.ToArray())
        Issues=@($included.ToArray())
        MergeRequests=@($merges.ToArray())
        Pipelines=@($pipelines.ToArray())
        Errors=@($Snapshot.Errors)
        TruncatedPotentially=$Snapshot.TruncatedPotentially
        ReadOnly=$true
        Scope=$counts
    }
}
