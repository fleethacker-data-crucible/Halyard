# Read-only ADO-style hierarchy projection from GitLab issues.
function Get-CascadeHierarchyProjection {
 [CmdletBinding()]param([Parameter(Mandatory)][object[]]$Issues)
 $nodes=New-Object System.Collections.ArrayList
 foreach($issue in $Issues){
  $meta=$null
  try { $meta=Get-CascadeMeta -Body ([string]$issue.description) } catch {}
  $type='UNCLASSIFIED'; $parent=$null; $stableId=$null
  if($meta){
   if($meta['type']){$type=([string]$meta['type']).ToUpperInvariant()}
   if($meta['parent']){$parent=[string]$meta['parent']}
   if($meta['id']){$stableId=[string]$meta['id']}
  }
  if($type -eq 'UNCLASSIFIED'){
   foreach($label in @($issue.labels)){
    if(([string]$label) -match '(?i)^type::(theme|epic|feature|story|task|bug)$'){$type=$Matches[1].ToUpperInvariant();break}
   }
  }
  $project=[int]$issue.project_id
  $id=if($stableId){$stableId}else{'gitlab:'+ $project+':'+[string]$issue.iid}
  [void]$nodes.Add([pscustomobject]@{
   Id=$id;ProjectId=$project;Iid=$issue.iid;Title=[string]$issue.title
   Type=$type;ParentId=$parent;State=[string]$issue.state;Url=[string]$issue.web_url
   HasMetadata=[bool]$meta;MetadataId=$stableId;UpdatedAt=$issue.updated_at
  })
 }
 # No fabricated parent links. Ambiguous IDs across projects remain roots.
 $counts=@{};foreach($n in $nodes){$key=[string]$n.Id;if(-not $counts.ContainsKey($key)){$counts[$key]=0};$counts[$key]++}
 foreach($n in $nodes){if($n.ParentId -and ((-not $counts.ContainsKey([string]$n.ParentId)) -or $counts[[string]$n.ParentId] -ne 1)){$n.ParentId=$null}}
 return @($nodes.ToArray())
}
