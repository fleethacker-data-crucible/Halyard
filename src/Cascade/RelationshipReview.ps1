#requires -Version 5.1
<#
.SYNOPSIS
  Evidence-ranked, read-only parent candidates for imported GitLab issues.
.DESCRIPTION
  Uses the existing Cascade Theme>Epic>Feature>Story>Task hierarchy
  and current canonical IDs from cascade-meta. Unlike the legacy CLI
  fallback, NEVER assigns the first available parent, never invents a
  placeholder parent, and never reads an out-of-label issue title.
#>
function Get-CascadeRelationshipKeywords {
    [CmdletBinding()]
    param([AllowEmptyString()][string]$Title='')
    $stop=@('a','an','the','to','for','of','in','on','and','or','by','with','as',
            'from','is','are','be','we','want','i','user','users','able',
            'feature','story','task','epic','theme','work','item','support',
            'create','new','update','fix','implement','enable','build','add',
            'improve','system','service','data','test','tests')
    $words=New-Object System.Collections.ArrayList
    foreach($match in [regex]::Matches($Title.ToLowerInvariant(),'[a-z0-9]{3,}')){
        $word=[string]$match.Value
        if($word.Length -gt 5 -and $word.EndsWith('s')){$word=$word.Substring(0,$word.Length-1)}
        if($word -notin $stop -and -not $words.Contains($word)){[void]$words.Add($word)}
    }
    return @($words.ToArray())
}
function Get-CascadeRelationshipReview {
    [CmdletBinding()]
    param([object[]]$Issues=@())
    $issueList=@($Issues|Where-Object {$null -ne $_})
    $records=New-Object System.Collections.ArrayList
    $idCount=@{}
    foreach($issue in $issueList){
        $record=Get-CascadeTopologyRecord -Issue $issue
        $meta=$null
        try{$meta=Get-CascadeMeta -Body ([string]$issue.description)}catch{}
        $data=[pscustomobject]@{
            Key=[string]$record.Key
            Id=[string]$record.ExplicitId
            Title=[string]$record.Title
            Type=[string]$record.Type
            # A tentative type only selects a possible relationship search space.
            # It never changes the source type or qualifies as a reviewed type.
            ReviewType=if([string]$record.Type -in @('EPIC','FEATURE','STORY','TASK')){[string]$record.Type}
                       elseif([string]$record.Type -eq 'UNCLASSIFIED'){Get-CascadeInferredTypeFromIssue -Issue $issue}
                       else{[string]$record.Type}
            ParentId=[string]$record.ParentId
            ProjectId=[int]$record.ProjectId
            Labels=@($issue.labels)
            Area=if($meta){[string]$meta['area']}else{''}
            ThemeId=if($meta){[string]$meta['theme']}else{''}
            Keywords=@(Get-CascadeRelationshipKeywords -Title ([string]$record.Title))
        }
        [void]$records.Add($data)
        if($data.Id){
            if(-not $idCount.ContainsKey($data.Id)){$idCount[$data.Id]=0}
            $idCount[$data.Id]++
        }
    }
    $uniqueById=@{}
    $parentCatalog=New-Object System.Collections.ArrayList
    foreach($record in $records){
        if(-not $record.Id -or $idCount[$record.Id] -ne 1 -or
            $record.Type -notin @('THEME','EPIC','FEATURE','STORY')){continue}
        $uniqueById[$record.Id]=$record
        [void]$parentCatalog.Add([pscustomobject]@{
            Key=$record.Key;Id=$record.Id;Type=$record.Type
            Title=$record.Title;ProjectId=$record.ProjectId
        })
    }
    $types=@{EPIC='THEME';FEATURE='EPIC';STORY='FEATURE';TASK='STORY'}
    $relationships=New-Object System.Collections.ArrayList
    $withCandidates=0
    $missingParent=0
    $withoutCandidates=0
    foreach($child in $records){
        if(-not $types.ContainsKey([string]$child.ReviewType)){continue}
        $expected=[string]$types[[string]$child.ReviewType]
        if(-not $child.ParentId){$missingParent++}
        $declaredStatus=if(-not $child.ParentId){'NOT_SET'}
            elseif(-not $uniqueById.ContainsKey($child.ParentId)){'NOT_VERIFIABLE_IN_CURRENT_VIEW'}
            elseif($uniqueById[$child.ParentId].Type -ne $expected){'TYPE_CONFLICT'}
            else{'PRESENT_IN_CURRENT_VIEW'}
        $recommendations=New-Object System.Collections.ArrayList
        foreach($candidate in $records){
            if(-not $candidate.Id -or -not $uniqueById.ContainsKey($candidate.Id) -or
                $candidate.Type -ne $expected -or $candidate.ProjectId -ne $child.ProjectId -or
                $candidate.Key -eq $child.Key){continue}
            # Never recommend a current descendant as the new parent.
            $wouldCycle=$false
            $current=$candidate
            $seen=@{}
            while($current -and $current.ParentId -and -not $seen.ContainsKey($current.Key)){
                $seen[$current.Key]=$true
                if($current.ParentId -eq $child.Id -and $child.Id){$wouldCycle=$true;break}
                if($uniqueById.ContainsKey($current.ParentId)){
                    $current=$uniqueById[$current.ParentId]
                }else{break}
            }
            if($wouldCycle){continue}
            $score=0
            $evidence=New-Object System.Collections.ArrayList
            $matched=@($child.Keywords|Where-Object {$_ -in $candidate.Keywords}|Select-Object -Unique)
            if($matched.Count -gt 0){
                $score+=30*[Math]::Min(3,$matched.Count)
                [void]$evidence.Add(('Shared specific title terms: '+($matched -join ', ')))
            }
            if($child.ThemeId -and $candidate.Type -eq 'THEME' -and $child.ThemeId -eq $candidate.Id){
                $score+=90
                [void]$evidence.Add('Explicit theme metadata points to this Theme ID')
            }
            $selectLabel={
                param($items)
                @($items|Where-Object {([string]$_) -match '(?i)^(domain|component|product|service)::'}|
                    ForEach-Object {([string]$_).ToLowerInvariant()}|Select-Object -Unique)
            }
            $childDomains=@(& $selectLabel $child.Labels)
            $candidateDomains=@(& $selectLabel $candidate.Labels)
            $sharedLabels=@($childDomains|Where-Object {$_ -in $candidateDomains})
            if($sharedLabels.Count -gt 0){
                $score+=35*[Math]::Min(2,$sharedLabels.Count)
                [void]$evidence.Add(('Shared scoped domain labels: '+($sharedLabels -join ', ')))
            }
            if($child.Area -and $candidate.Area -and
                [string]::Equals($child.Area,$candidate.Area,[StringComparison]::OrdinalIgnoreCase)){
                $score+=10
                [void]$evidence.Add('Same metadata area (supporting context only)')
            }
            # Shared area/team or same project alone never qualifies.
            if($matched.Count -eq 0 -and $sharedLabels.Count -eq 0 -and
               -not ($child.ThemeId -and $candidate.Type -eq 'THEME' -and $child.ThemeId -eq $candidate.Id)){continue}
            $confidence=if($score -ge 75){'STRONG'}elseif($score -ge 45){'MODERATE'}else{'WEAK'}
            [void]$recommendations.Add([pscustomobject]@{
                ParentId=$candidate.Id
                ParentKey=$candidate.Key
                ParentTitle=$candidate.Title
                ParentType=$candidate.Type
                ProjectId=$candidate.ProjectId
                Score=$score
                Confidence=$confidence
                Evidence=@($evidence.ToArray())
                Status='SUGGESTION_ONLY'
            })
        }
        $ranked=@($recommendations.ToArray()|Sort-Object -Property @{Expression='Score';Descending=$true},@{Expression='ParentId';Descending=$false}|Select-Object -First 3)
        if($ranked.Count -gt 0){$withCandidates++}
        elseif(-not $child.ParentId){$withoutCandidates++}
        [void]$relationships.Add([pscustomobject]@{
            Key=$child.Key
            ChildType=$child.Type
            CandidateChildType=$child.ReviewType
            TypeEvidence=if($child.Type -eq 'UNCLASSIFIED'){'HEURISTIC_TYPE_UNCONFIRMED'}else{'EXPLICIT_SOURCE_TYPE'}
            ExpectedParentType=$expected
            DeclaredParent=$child.ParentId
            DeclaredParentStatus=$declaredStatus
            Candidates=$ranked
            Decision='REQUIRES_HUMAN_REVIEW'
        })
    }
    return [pscustomobject]@{
        Schema='cascade.relationship_candidates.v1'
        Mode='READ_ONLY'
        Summary=[pscustomobject]@{
            ReviewedChildIssues=$relationships.Count
            MissingDeclaredParent=$missingParent
            WithCandidateEvidence=$withCandidates
            MissingParentWithoutCandidates=$withoutCandidates
            EligibleVisibleParentIds=$parentCatalog.Count
        }
        Relationships=@($relationships.ToArray())
        ParentCatalog=@($parentCatalog.ToArray())
        Safety='Candidate relationships are not verified. No first-parent defaults, invented parent IDs, cross-project guesses, or GitLab changes. Only the selected issue-label view is considered.'
    }
}
