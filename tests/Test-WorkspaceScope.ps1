#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src/Cascade/Cascade.psd1') -Force
function New-TestIssue([int]$ProjectId,[int]$Iid,[string[]]$Labels,[string]$Description=''){
    [pscustomobject]@{
        id=$ProjectId*1000+$Iid;iid=$Iid;project_id=$ProjectId
        title=('Issue '+$Iid);description=$Description;state='opened';labels=@($Labels)
        web_url=('https://web.git.mil/example/-/issues/'+$Iid)
    }
}
$areaMeta=@'
~~~cascade-meta
type: STORY
id: STORY-not-labeled
area: data-crucible
state: open
~~~
'@
$issues=@(
    (New-TestIssue 16629 1 @('team::Data-Crucible','type::story')),
    (New-TestIssue 16629 2 @('team::Other')),
    (New-TestIssue 16629 3 @() $areaMeta),
    (New-TestIssue 16629 4 @('Data-Crucible')),
    (New-TestIssue 16629 5 @()),
    (New-TestIssue 4001 6 @()), # Dedicated path must NOT imply label match
    (New-TestIssue 3000 7 @('TEAM::DATA-CRUCIBLE')) # Same label across source projects must match
)
$source=[pscustomobject]@{
    SchemaVersion=2;Source='web.git.mil';GeneratedUtc='2026-10-22T00:00:00Z';SinceDays=30
    User=[pscustomobject]@{username='tester'};ArcLoaded=$false;ReadOnly=$true;TruncatedPotentially=$false
    Projects=@(
        [pscustomobject]@{id=16629;path_with_namespace='navy/data-crucible/cbr'},
        [pscustomobject]@{id=4001;path_with_namespace='navy/data-crucible/engine'},
        [pscustomobject]@{id=3000;path_with_namespace='navy/another/engine'}
    )
    Issues=$issues
    Activity=@(
        [pscustomobject]@{ProjectId=16629;Commit='shared';Repository='navy/data-crucible/cbr'},
        [pscustomobject]@{ProjectId=4001;Commit='dedicated';Repository='navy/data-crucible/engine'},
        [pscustomobject]@{ProjectId=3000;Commit='other';Repository='navy/another/engine'}
    )
    MergeRequests=@([pscustomobject]@{project_id=16629;iid=3})
    Pipelines=@([pscustomobject]@{project_id=3000;id=1})
    Errors=@()
}
$before=$source|ConvertTo-Json -Depth 12 -Compress
$filtered=Get-CascadeScopedGitMilSnapshot -Snapshot $source -Label 'team::data-crucible'
if($filtered.Scope.Mode -ne 'label' -or $filtered.Scope.Label -ne 'team::data-crucible'){throw 'Label scope identity is incorrect.'}
if(@($filtered.Issues).Count -ne 2){throw 'Exact label must match only 2 issues in 2 source projects.'}
if(@($filtered.Issues | Where-Object {$_.iid -in @(3,4,6)}).Count -gt 0){
    throw 'Issue with metadata, a similar label, or a matching namespace must not bypass exact GitLab label matching.'
}
if(@($filtered.Activity).Count -ne 0 -or @($filtered.MergeRequests).Count -ne 0 -or @($filtered.Pipelines).Count -ne 0){
    throw 'Unattributable project-level activity must not be presented as evidence for label-matched work.'
}
if($filtered.Scope.SourceIssueCount -ne 7 -or $filtered.Scope.IncludedIssueCount -ne 2 -or $filtered.Scope.ExcludedIssueCount -ne 5){
    throw 'Incorrect label filter summary counts.'
}
$metaOnly=Get-CascadeScopedGitMilSnapshot -Snapshot $source -Label 'Data-Crucible'
if(@($metaOnly.Issues).Count -ne 1 -or $metaOnly.Issues[0].iid -ne 4){throw 'Literal distinct labels must not be conflated.'}
$none=Get-CascadeScopedGitMilSnapshot -Snapshot $source -Label 'label-not-used'
if(@($none.Issues).Count -ne 0){throw 'Missing label should produce zero matches, not inferred results.'}
$all=Get-CascadeScopedGitMilSnapshot -Snapshot $source -Label ''
if(@($all.Issues).Count -ne 7 -or @($all.Activity).Count -ne 3 -or @($all.MergeRequests).Count -ne 1){
    throw 'Cleared filter must restore the complete work-item and source-activity view.'
}
$plan=ConvertTo-CascadeGitMilPlan -Snapshot $source -IssueLabel 'team::data-crucible'
if(@($plan.Issues).Count -ne 2 -or @($plan.Hierarchy).Count -ne 2 -or @($plan.Activity).Count -ne 0){
    throw 'The Nexus live plan must consume the same exact-label-filtered issues.'
}
$emptyPlan=ConvertTo-CascadeGitMilPlan -Snapshot $source -IssueLabel 'missing'
if(@($emptyPlan.Actions).Count -ne 0 -or @($emptyPlan.Hierarchy).Count -ne 0){
    throw 'Zero-match plan must remain a valid read-only result.'
}
if(($source|ConvertTo-Json -Depth 12 -Compress) -cne $before){throw 'Exact filtering modified the original Git.mil snapshot.'}
$invalid=$false
$badLabel='unsafe'+[char]10+'label'
try{$null=Assert-CascadeIssueLabelFilter -Label $badLabel}catch{$invalid=$true}
if(-not $invalid){throw 'Multiline label was not rejected.'}
if((Assert-CascadeIssueLabelFilter -Label '  team::Data-Crucible  ') -cne 'team::Data-Crucible'){
    throw 'Label whitespace was not normalized.'
}
Write-Host 'PASS: exact issue label matching, similar labels distinct, metadata/namespace ignored, evidence safeguards and source preservation.' -ForegroundColor Green
