#requires -Version 5.1
<#
.SYNOPSIS
    Phase 2A: read-only topology and schema diagnostics for imported GitLab issues.
.DESCRIPTION
    Uses only explicit cascade-meta and type:: labels. No AI-generated types,
    fabricated relationships, production writes, or automatic migrations.
    If a referenced parent is in the raw snapshot but outside the selected
    label, disclose only that fact, never the excluded issue's title.
#>
function Get-CascadeTopologyRecord {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Issue)
    $meta=$null
    try{$meta=Get-CascadeMeta -Body ([string]$Issue.description)}catch{}
    $pid=[string]$Issue.project_id
    $iid=[string]$Issue.iid
    $key='gitlab:'+$pid+':'+$iid
    $explicitId=if($meta){([string]$meta['id']).Trim()}else{''}
    $typeValue=if($meta){([string]$meta['type']).Trim().ToUpperInvariant()}else{''}
    $labels=New-Object System.Collections.Generic.List[string]
    foreach($label in @($Issue.labels)){
        if([string]$label -match '(?i)^type::(theme|epic|feature|story|task|bug)$'){
            $candidate=$Matches[1].ToUpperInvariant()
            if(-not $labels.Contains($candidate)){[void]$labels.Add($candidate)}
        }
    }
    $inferred=if($typeValue){$typeValue}elseif($labels.Count -eq 1){$labels[0]}else{'UNCLASSIFIED'}
    return [pscustomobject]@{
        Key=$key
        Id=if($explicitId){$explicitId}else{$key}
        ExplicitId=$explicitId
        ProjectId=$Issue.project_id
        Iid=$Issue.iid
        Title=[string]$Issue.title
        Url=[string]$Issue.web_url
        Type=$inferred
        MetadataType=$typeValue
        TypeLabels=@($labels.ToArray())
        ParentId=if($meta){([string]$meta['parent']).Trim()}else{''}
        HasMetadata=($null -ne $meta)
    }
}
function New-CascadeTopologyFinding {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Record,[Parameter(Mandatory)][string]$Code,
          [ValidateSet('ERROR','WARNING','INFO')][string]$Severity='WARNING',
          [Parameter(Mandatory)][string]$Explanation)
    [pscustomobject]@{
        Key=$Record.Key
        ProjectId=$Record.ProjectId
        Iid=$Record.Iid
        Title=$Record.Title
        Url=$Record.Url
        Type=$Record.Type
        Code=$Code
        Severity=$Severity
        Explanation=$Explanation
    }
}
function Get-CascadeTopologyAudit {
    [CmdletBinding()]
    param([object[]]$Issues=@(),[object[]]$SourceIssues=@(),
          [AllowEmptyString()][string]$IssueLabel='')
    # Both collections may have been flattened by PowerShell's pipeline.
    $visible=@($Issues | Where-Object {$null -ne $_})
    $raw=if(@($SourceIssues).Count -gt 0){@($SourceIssues | Where-Object {$null -ne $_})}else{@($visible)}
    $recordList=New-Object System.Collections.ArrayList
    $rawRecords=New-Object System.Collections.ArrayList
    foreach($issue in $raw){[void]$rawRecords.Add((Get-CascadeTopologyRecord -Issue $issue))}
    foreach($issue in $visible){[void]$recordList.Add((Get-CascadeTopologyRecord -Issue $issue))}
    $index=@{}
    foreach($record in $rawRecords){
        # Canonical stable IDs and project-scoped GitLab fallback IDs both work.
        foreach($key in @($record.Id,$record.Key)|Select-Object -Unique){
            if(-not $index.ContainsKey([string]$key)){$index[[string]$key]=New-Object System.Collections.ArrayList}
            [void]$index[[string]$key].Add($record)
        }
    }
    $visibleKeys=@{}
    foreach($record in $recordList){$visibleKeys[$record.Key]=$true}
    $findings=New-Object System.Collections.ArrayList
    $edges=@{}
    $allowed=@('THEME','EPIC','FEATURE','STORY','TASK','BUG')
    $expectedParent=@{EPIC='THEME';FEATURE='EPIC';STORY='FEATURE';TASK='STORY'}
    foreach($record in $recordList){
        if($record.Type -notin $allowed){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'UNCLASSIFIED_TYPE' 'WARNING' 'No unambiguous supported type is declared in metadata or type:: labels.'))
        }
        if($record.MetadataType -and $record.MetadataType -notin $allowed){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'UNKNOWN_DECLARED_TYPE' 'WARNING' 'The metadata type is not one of Theme, Epic, Feature, Story, Task or Bug.'))
        }
        if(@($record.TypeLabels).Count -gt 1){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'CONFLICTING_TYPE_LABELS' 'WARNING' 'More than one type:: label was found; choose the intended work-item type.'))
        }
        if($record.MetadataType -and @($record.TypeLabels).Count -gt 0 -and
           @($record.TypeLabels | Where-Object {$_ -ne $record.MetadataType}).Count -gt 0){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'TYPE_METADATA_LABEL_CONFLICT' 'WARNING' 'The metadata type disagrees with at least one type:: label.'))
        }
        if(-not $record.ExplicitId){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'NO_CANONICAL_ID' 'INFO' 'No stable cascade-meta id; a project/IID fallback is used for display only.'))
        }elseif($index.ContainsKey($record.Id) -and @($index[$record.Id]).Count -gt 1){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'DUPLICATE_CANONICAL_ID' 'ERROR' 'This stable ID belongs to multiple imported work items and cannot identify a unique parent.'))
        }
        if(-not $record.ParentId){
            if($expectedParent.ContainsKey($record.Type)){
                [void]$findings.Add((New-CascadeTopologyFinding $record 'PARENT_NOT_DECLARED' 'WARNING' 'No parent is declared in cascade-meta; native GitLab relationships have not yet been imported.'))
            }
            continue
        }
        if($record.ParentId -eq $record.Id -or $record.ParentId -eq $record.Key){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'SELF_PARENT' 'ERROR' 'This work item refers to itself as its parent.'))
            continue
        }
        if(-not $index.ContainsKey($record.ParentId)){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'PARENT_UNRESOLVED' 'WARNING' 'The declared parent was not found in the imported source; this may reflect project access or paging limits.'))
            continue
        }
        $matches=@($index[$record.ParentId])
        if($matches.Count -ne 1){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'AMBIGUOUS_PARENT_ID' 'ERROR' 'Multiple imported work items match the declared parent ID.'))
            continue
        }
        $parent=$matches[0]
        if(-not $visibleKeys.ContainsKey($parent.Key)){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'PARENT_OUTSIDE_LABEL' 'INFO' 'The declared parent exists in imported data but is outside the currently selected issue-label filter.'))
            continue
        }
        $edges[$record.Key]=$parent.Key
        if($expectedParent.ContainsKey($record.Type) -and $parent.Type -ne $expectedParent[$record.Type]){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'PARENT_TYPE_MISMATCH' 'WARNING' ('A '+$record.Type+' normally belongs under a '+$expectedParent[$record.Type]+', but the referenced parent has a different declared type.')))
        }
    }
    # Report each true cycle member, not unrelated ancestors merely reaching it.
    $cycleNodes=@{}
    foreach($record in $recordList){
        $path=New-Object System.Collections.ArrayList
        $positions=@{}
        $cursor=$record.Key
        while($edges.ContainsKey($cursor)){
            if($positions.ContainsKey($cursor)){
                for($i=[int]$positions[$cursor];$i -lt $path.Count;$i++){
                    $cycleNodes[[string]$path[$i]]=$true
                }
                break
            }
            $positions[$cursor]=$path.Count
            [void]$path.Add($cursor)
            $cursor=[string]$edges[$cursor]
        }
    }
    foreach($record in $recordList){
        if($cycleNodes.ContainsKey($record.Key)){
            [void]$findings.Add((New-CascadeTopologyFinding $record 'PARENT_CYCLE' 'ERROR' 'The explicitly declared parent links form a circular chain.'))
        }
    }
    $array=@($findings.ToArray())
    $affected=@($array | ForEach-Object {$_.Key} | Select-Object -Unique)
    return [pscustomobject]@{
        Version=1;Mode='READ_ONLY';IssueLabel=$IssueLabel
        Summary=[pscustomobject]@{
            VisibleIssueCount=$recordList.Count
            SourceIssueCount=$rawRecords.Count
            ClassifiedIssueCount=@($recordList | Where-Object {$_.Type -ne 'UNCLASSIFIED'}).Count
            AffectedIssueCount=$affected.Count
            FindingsCount=$array.Count
            ErrorCount=@($array | Where-Object {$_.Severity -eq 'ERROR'}).Count
            WarningCount=@($array | Where-Object {$_.Severity -eq 'WARNING'}).Count
            InfoCount=@($array | Where-Object {$_.Severity -eq 'INFO'}).Count
        }
        Findings=$array
        CoverageNote='Read-only explicit metadata/label checks. Native GitLab parent relations are not yet imported; unresolved parents may reflect API paging, permissions, or an excluded project.'
    }
}
