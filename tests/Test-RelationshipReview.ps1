#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src/Cascade/Cascade.psd1') -Force
function New-RelationshipFixture {
 param([int]$Iid,[string]$Title,[string]$Type,[string]$Id,[string]$Parent,
       [string]$Theme,[string[]]$Labels=@('team::Data-Crucible'),[int]$ProjectId=16629)
 $meta=@('~~~cascade-meta',('type: '+$Type),('id: '+$Id),('parent: '+$Parent),
   ('theme: '+$Theme),'state: open','area: data-crucible','~~~') -join [Environment]::NewLine
 [pscustomobject]@{
  id=18000+$Iid;iid=$Iid;project_id=$ProjectId;title=$Title
  description=$meta;state='opened';labels=@($Labels)
  web_url=('https://web.git.mil/fixture/issues/'+$Iid);assignees=@()
 }
}
$theme=New-RelationshipFixture 1 'Reliable ingestion portfolio' 'THEME' 'THEME-ingestion' '' ''
$epic=New-RelationshipFixture 2 'Reliable ingestion operations' 'EPIC' 'EPIC-ingestion' '' 'THEME-ingestion'
$feature=New-RelationshipFixture 3 'Batch ingestion validation' 'FEATURE' 'FEATURE-batch-ingestion' 'EPIC-ingestion' 'THEME-ingestion' @('team::Data-Crucible','component::batch')
$story=New-RelationshipFixture 4 'Batch ingestion retry validation' 'STORY' 'STORY-batch-retry' '' 'THEME-ingestion' @('team::Data-Crucible','component::batch')
$unrelated=New-RelationshipFixture 5 'Payroll reconciliation' 'STORY' 'STORY-payroll' '' 'THEME-other'
$otherProject=New-RelationshipFixture 6 'Batch ingestion retry verification' 'FEATURE' 'FEATURE-other-project' '' 'THEME-ingestion' @('team::Data-Crucible','component::batch') 17777
$issues=@($theme,$epic,$feature,$story,$unrelated,$otherProject)
$original=$issues|ConvertTo-Json -Depth 14 -Compress
$result=Get-CascadeRelationshipReview -Issues $issues
if($result.Mode -ne 'READ_ONLY' -or $result.Summary.ReviewedChildIssues -lt 4){throw 'Missing relationship engine or read-only mode.'}
$storyRow=@($result.Relationships|Where-Object {$_.Key -eq 'gitlab:16629:4'})
if($storyRow.Count -ne 1 -or $storyRow[0].ExpectedParentType -ne 'FEATURE'){throw 'A Story must link to a Feature.'}
$matches=@($storyRow[0].Candidates|Where-Object {$_.ParentId -eq 'FEATURE-batch-ingestion'})
if($matches.Count -ne 1 -or $matches[0].Status -ne 'SUGGESTION_ONLY' -or
   $matches[0].Score -le 0 -or @($matches[0].Evidence).Count -lt 1){
 throw 'Evidence-backed, nonbinding Feature recommendation missing.'
}
if(@($storyRow[0].Candidates|Where-Object {$_.ParentId -eq 'FEATURE-other-project'}).Count){
 throw 'Candidate leaked from another project.'
}
if($storyRow[0].DeclaredParent -or $storyRow[0].Decision -ne 'REQUIRES_HUMAN_REVIEW'){
 throw 'Relationship incorrectly auto-filled a missing parent.'
}
$epicRow=@($result.Relationships|Where-Object {$_.Key -eq 'gitlab:16629:2'})
if(@($epicRow[0].Candidates|Where-Object {$_.ParentId -eq 'THEME-ingestion'}).Count -ne 1){
 throw 'The explicit theme association was not considered.'
}
$notLinked=@($result.Relationships|Where-Object {$_.Key -eq 'gitlab:16629:5'})
if(@($notLinked[0].Candidates).Count -ne 0){
 throw 'An unrelated Story received an arbitrary first-Feature fallback suggestion.'
}
$filtered=@($issues|Where-Object {$_.project_id -eq 16629 -and $_.labels -contains 'team::Data-Crucible'})
$filteredResult=Get-CascadeRelationshipReview -Issues $filtered
if(@($filteredResult.ParentCatalog|Where-Object {$_.ProjectId -eq 17777}).Count){
 throw 'Parent catalog disclosed an excluded project.'
}
if(($issues|ConvertTo-Json -Depth 14 -Compress) -cne $original){throw 'Relationship analysis mutated Git.mil source issues.'}
$duplicate=New-RelationshipFixture 8 'Another Batch Feature' 'FEATURE' 'FEATURE-batch-ingestion' '' 'THEME-ingestion'
$ambiguous=Get-CascadeRelationshipReview -Issues @($theme,$epic,$feature,$story,$duplicate)
if(@($ambiguous.ParentCatalog|Where-Object {$_.Id -eq 'FEATURE-batch-ingestion'}).Count){
 throw 'Duplicate canonical IDs must not appear as eligible parent candidates.'
}
Write-Host 'PASS: hierarchy types, evidence, theme links, no arbitrary fallback, no cross-project links, duplicate ID protection and immutable source.' -ForegroundColor Green
