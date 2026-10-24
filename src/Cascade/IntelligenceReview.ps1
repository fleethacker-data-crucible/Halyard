#requires -Version 5.1
<#
.SYNOPSIS
  Deterministic, read-only normalization preview for existing Git.mil issues.
.DESCRIPTION
  Wraps the existing Normalize.ps1 and Schema.ps1 intelligence functions,
  taxonomy requirements and topology audit. Never invokes GitLab write APIs,
  AI endpoints, or migration/apply commands. Unknown type/parent remain unknown.
#>
function Get-CascadeIntelligencePreview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Snapshot,
        [AllowEmptyString()][string]$IssueLabel='',
        [ValidateRange(1,500)][int]$MaxItems=200
    )
    $selectedLabel=Assert-CascadeIssueLabelFilter -Label $IssueLabel
    $filtered=Get-CascadeScopedGitMilSnapshot -Snapshot $Snapshot -Label $selectedLabel
    $visible=@($filtered.Issues | Where-Object {$null -ne $_})
    $sourceIssues=@($Snapshot.Issues | Where-Object {$null -ne $_})
    $audit=Get-CascadeTopologyAudit -Issues $visible -SourceIssues $sourceIssues -IssueLabel $selectedLabel
    $taxonomy=Get-CascadeTaxonomy
    $selected=@($visible|Select-Object -First $MaxItems)
    $organization=Get-CascadeRelationshipReview -Issues $selected
    $relationshipMap=@{}
    foreach($relationship in @($organization.Relationships)){$relationshipMap[[string]$relationship.Key]=$relationship}
    $results=New-Object System.Collections.ArrayList
    $unclassified=0
    $missingMetadata=0
    $missingParents=0
    $needsReview=0
    $existingTypeLabels=@('THEME','EPIC','FEATURE','STORY','TASK','BUG')
    foreach($issue in $selected){
        $meta=$null
        try{$meta=Get-CascadeMeta -Body ([string]$issue.description)}catch{}
        $norm=Convert-CascadeGitLabIssueToNormalized -Issue $issue
        $record=Get-CascadeTopologyRecord -Issue $issue
        $type=[string]$record.Type
        $knownType=($type -in $existingTypeLabels)
        if(-not $knownType){$unclassified++}
        if(-not $meta){$missingMetadata++}
        $required=@()
        if($knownType -and $null -ne $taxonomy.required_meta.PSObject.Properties[$type]){
            $required=@($taxonomy.required_meta.PSObject.Properties[$type].Value)
        }elseif(-not $knownType){$required=@('type','id')}
        $missing=New-Object System.Collections.ArrayList
        foreach($key in $required){
            if(-not $meta -or [string]::IsNullOrWhiteSpace([string]$meta[[string]$key])){
                [void]$missing.Add([string]$key)
            }
        }
        if($knownType -and $type -in @('EPIC','FEATURE','STORY','TASK') -and
           [string]::IsNullOrWhiteSpace([string]$record.ParentId)){
            $missingParents++
            if(-not ($missing -contains 'parent')){[void]$missing.Add('parent')}
        }
        # Pull only this issue's findings. Excluded parent's identity is not
        # available to the filtered reviewer.
        $issueFindings=@($audit.Findings | Where-Object {$_.Key -eq $record.Key})
        $issueDeficiencies=@($norm.deficiencies|Where-Object {$_})
        $relationship=$null
        if($relationshipMap.ContainsKey($record.Key)){$relationship=$relationshipMap[$record.Key]}
        $allCodes=@($issueDeficiencies+@($issueFindings|ForEach-Object {$_.Code})|Select-Object -Unique)
        $review=($missing.Count -gt 0 -or $allCodes.Count -gt 0 -or -not $knownType)
        if($review){$needsReview++}
        # The existing normalizer may default to STORY or calculate points
        # from effort bands; surface those only as unverified suggestions.
        $result=[pscustomobject]@{
            Key=$record.Key
            ProjectId=[int]$issue.project_id
            Iid=$issue.iid
            Title=[string]$issue.title
            Url=[string]$issue.web_url
            Type=if($knownType){$type}else{'UNCLASSIFIED'}
            CandidateType=if($knownType){$type}else{[string]$norm.proposed_type}
            TypeEvidence=if($meta -and $meta['type']){'cascade-meta'}
                         elseif(@($record.TypeLabels).Count -eq 1){'type:: label'}
                         else{'heuristic / review required'}
            StableId=[string]$record.Id
            DeclaredParent=[string]$record.ParentId
            ParentReview=$relationship
            MissingMetadata=@($missing.ToArray())
            RequiredMetadata=@($required)
            DeficiencyCodes=@($allCodes)
            Findings=@($issueFindings)
            Labels=@($issue.labels)
            RequiresReview=[bool]$review
            ReviewState=if($review){'NEEDS_HUMAN_REVIEW'}else{'NO_FLAGGED_DEFICIENCIES'}
            # Never call this ready-to-apply; Normalize may include guessed
            # values and a mirrored body that needs individual human review.
            DraftDescription=[string]$norm.description
            DraftWarning='Unapproved preview, not a GitLab patch. Candidate type, story points and parent must be verified by an owner. No write API is called.'
        }
        [void]$results.Add($result)
    }
    return [pscustomobject]@{
        Schema='cascade.intelligence_review.v1'
        Mode='READ_ONLY'
        Source='Existing imported Git.mil snapshot'
        Label=$selectedLabel
        CreatedUtc=[datetime]::UtcNow.ToString('o')
        Summary=[pscustomobject]@{
            ImportedIssues=$sourceIssues.Count
            LabelMatchingIssues=$visible.Count
            AnalyzedIssues=$results.Count
            Truncated=($visible.Count -gt $selected.Count)
            RequiresReview=$needsReview
            NoMetadata=$missingMetadata
            UnclassifiedType=$unclassified
            MissingDeclaredParent=$missingParents
            TopologyErrorCount=[int]$audit.Summary.ErrorCount
            TopologyWarningCount=[int]$audit.Summary.WarningCount
        }
        Items=@($results.ToArray())
        RelationshipSummary=$organization.Summary
        ParentCatalog=@($organization.ParentCatalog)
        RelationshipSafety=$organization.Safety
        TopologyCoverageNote=[string]$audit.CoverageNote
        Safety='Dry-run only. No AI request, token export, Git.mil write, approval or automatic migration.'
    }
}
