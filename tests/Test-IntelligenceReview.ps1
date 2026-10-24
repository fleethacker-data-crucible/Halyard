#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src/Cascade/Cascade.psd1') -Force

$parentDescription=@'
~~~cascade-meta
type: FEATURE
id: FEATURE-example
parent: EPIC-not-imported
theme: THEME-example
state: ready
area: data-crucible
effort: Medium
~~~
## Feature Done-When
- [ ] A sample output can be reviewed.
## Short Description
A tested source contract is available for authorized users.
'@
$source=[pscustomobject]@{
 SchemaVersion=2;Source='fixture';GeneratedUtc='2026-10-23T00:00:00Z';SinceDays=30
 User=[pscustomobject]@{username='synthetic'}
 ArcLoaded=$false;ReadOnly=$true;TruncatedPotentially=$false
 Projects=@([pscustomobject]@{id=16629;path_with_namespace='example/shared'})
 Issues=@(
  [pscustomobject]@{id=1201;iid=1;project_id=16629;title='Source contracts';description=$parentDescription;state='opened';labels=@('team::Data-Crucible','type::feature');web_url='https://web.git.mil/example/1';assignees=@()},
  [pscustomobject]@{id=1202;iid=2;project_id=16629;title='Fix missing source ID';description='Just fix the missing ID.';state='opened';labels=@('team::Data-Crucible');web_url='https://web.git.mil/example/2';assignees=@()},
  [pscustomobject]@{id=1203;iid=3;project_id=16629;title='Other team';description='This issue is unrelated.';state='opened';labels=@('team::Other');web_url='https://web.git.mil/example/3';assignees=@()}
 )
 Activity=@();MergeRequests=@();Pipelines=@();Errors=@()
}
$before=$source|ConvertTo-Json -Depth 15 -Compress
$review=Get-CascadeIntelligencePreview -Snapshot $source -IssueLabel 'team::Data-Crucible'
if($review.Mode -ne 'READ_ONLY' -or $review.Summary.LabelMatchingIssues -ne 2 -or $review.Summary.ImportedIssues -ne 3){
 throw 'Invalid read-only review scope/count.'
}
if(@($review.Items).Count -ne 2){throw 'Review did not obey exact GitLab label.'}
$missing=@($review.Items|Where-Object {$_.Iid -eq 2})
if($missing.Count -ne 1 -or $missing[0].Type -ne 'UNCLASSIFIED' -or $missing[0].TypeEvidence -ne 'heuristic / review required'){
 throw 'Missing metadata incorrectly classified as an authoritative type.'
}
if($missing[0].DeclaredParent){throw 'Intelligence inferred a parent without source evidence.'}
if(@($missing[0].MissingMetadata).Count -lt 2 -or -not $missing[0].DraftDescription){
 throw 'Existing CLI normalizer and metadata validation were not used.'
}
if($review.Summary.NoMetadata -ne 1 -or $review.Summary.RequiresReview -lt 1){
 throw 'Required review counts are missing.'
}
if(@($review.Items|Where-Object {$_.Iid -eq 3}).Count){throw 'An unrelated team issue leaked into review.'}
if(($source|ConvertTo-Json -Depth 15 -Compress) -cne $before){throw 'Read-only normalization preview changed source.'}
$empty=Get-CascadeIntelligencePreview -Snapshot $source -IssueLabel 'never-used'
if(@($empty.Items).Count -ne 0 -or $empty.Summary.LabelMatchingIssues -ne 0){
 throw 'Zero-match label must produce an empty valid review.'
}
Write-Host 'PASS: actual normalization and taxonomy functions are exposed read-only; no guessed parents, cross-label leaks or source writes.' -ForegroundColor Green
