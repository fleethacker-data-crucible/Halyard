#requires -Version 5.1
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $repo 'src/Cascade/Cascade.psd1') -Force
function New-AuditIssue([int]$Iid,[string]$Id,[string]$Type,[string]$Parent,
                        [string[]]$Labels=@('team::Data-Crucible')){
    $nl=[Environment]::NewLine
    $block=if($Type -or $Id){
        (@('~~~cascade-meta',('type: '+$Type),('id: '+$Id),('parent: '+$Parent),'~~~') -join $nl)
    }else{''}
    [pscustomobject]@{
        id=16000+$Iid;iid=$Iid;project_id=16629
        title=('Audit fixture '+$Iid);state='opened'
        description=$block;labels=@($Labels)
        web_url=('https://web.git.mil/safe/-/issues/'+$Iid)
    }
}
$raw=@(
    (New-AuditIssue 1 'T' 'THEME' ''),
    (New-AuditIssue 2 'E' 'EPIC' 'T'),
    (New-AuditIssue 3 'F' 'FEATURE' 'E'),
    (New-AuditIssue 4 'S' 'STORY' 'F'),
    (New-AuditIssue 5 'K' 'TASK' 'S'),
    (New-AuditIssue 6 '' '' ''),
    (New-AuditIssue 7 '' '' '' @('team::Data-Crucible','type::Story')),
    (New-AuditIssue 8 'X' 'STORY' 'OUTSIDE'),
    (New-AuditIssue 9 'Y' 'STORY' 'MISSING'),
    (New-AuditIssue 10 'SELF' 'STORY' 'SELF'),
    (New-AuditIssue 11 'DUP' 'FEATURE' 'E'),
    (New-AuditIssue 12 'DUP' 'FEATURE' 'E'),
    (New-AuditIssue 13 'CONFLICT' 'FEATURE' 'E' @('team::Data-Crucible','type::Epic')),
    (New-AuditIssue 14 'A' 'EPIC' 'B'),
    (New-AuditIssue 15 'B' 'EPIC' 'A'),
    (New-AuditIssue 16 'MISMATCH' 'STORY' 'T'),
    (New-AuditIssue 17 'OUTSIDE' 'FEATURE' 'E' @('team::Other'))
)
$visible=@($raw | Where-Object { $_.labels -contains 'team::Data-Crucible' })
$original=$raw|ConvertTo-Json -Depth 12 -Compress
$audit=Get-CascadeTopologyAudit -Issues $visible -SourceIssues $raw -IssueLabel 'team::Data-Crucible'
if($audit.Mode -ne 'READ_ONLY' -or $audit.Summary.VisibleIssueCount -ne 16 -or $audit.Summary.SourceIssueCount -ne 17){
    throw 'Audit did not preserve visible-vs-source distinction.'
}
function Assert-Finding([int]$Iid,[string]$Code) {
    if(-not @($audit.Findings | Where-Object {$_.Iid -eq $Iid -and $_.Code -eq $Code}).Count){
        throw ('Missing topology finding '+$Code+' on issue #'+$Iid)
    }
}
Assert-Finding 6 'UNCLASSIFIED_TYPE'
Assert-Finding 7 'NO_CANONICAL_ID'
Assert-Finding 7 'PARENT_NOT_DECLARED'
Assert-Finding 8 'PARENT_OUTSIDE_LABEL'
Assert-Finding 9 'PARENT_UNRESOLVED'
Assert-Finding 10 'SELF_PARENT'
Assert-Finding 11 'DUPLICATE_CANONICAL_ID'
Assert-Finding 12 'DUPLICATE_CANONICAL_ID'
Assert-Finding 13 'TYPE_METADATA_LABEL_CONFLICT'
Assert-Finding 14 'PARENT_CYCLE'
Assert-Finding 15 'PARENT_CYCLE'
Assert-Finding 16 'PARENT_TYPE_MISMATCH'
if(@($audit.Findings | Where-Object {$_.Iid -eq 1 -and $_.Severity -eq 'ERROR'}).Count){
    throw 'Healthy Theme unexpectedly flagged as invalid.'
}
if(@($audit.Findings | Where-Object {$_.Iid -eq 17}).Count){
    throw 'Findings disclosed an excluded work item.'
}
if(($audit|ConvertTo-Json -Depth 10 -Compress) -match 'Audit fixture 17'){
    throw 'An out-of-label parent title leaked into filtered output.'
}
if(($raw|ConvertTo-Json -Depth 12 -Compress) -cne $original){
    throw 'Audit changed the source issues.'
}
$empty=Get-CascadeTopologyAudit -Issues @() -SourceIssues $raw -IssueLabel 'no-match'
if($empty.Summary.VisibleIssueCount -ne 0 -or @($empty.Findings).Count -ne 0){
    throw 'Empty label scope is not valid.'
}
$snapshot=[pscustomobject]@{
    SchemaVersion=2;Source='web.git.mil';GeneratedUtc='2026-10-23T00:00:00Z';SinceDays=30
    User=[pscustomobject]@{username='offline'};ArcLoaded=$false;ReadOnly=$true
    Projects=@([pscustomobject]@{id=16629;path_with_namespace='navy/shared-cbr'})
    Issues=$raw;Activity=@();MergeRequests=@();Pipelines=@();Errors=@();TruncatedPotentially=$false
}
$plan=ConvertTo-CascadeGitMilPlan -Snapshot $snapshot -IssueLabel 'team::Data-Crucible'
if(@($plan.Issues).Count -ne 16 -or $plan.TopologyAudit.Summary.VisibleIssueCount -ne 16){
    throw 'Audit not integrated into the same exact-label-filtered plan.'
}
$all=ConvertTo-CascadeGitMilPlan -Snapshot $snapshot -IssueLabel ''
if($all.TopologyAudit.Summary.VisibleIssueCount -ne 17){
    throw 'Clearing label did not restore complete topology audit.'
}
if(@($all.TopologyAudit.Findings | Where-Object {$_.Code -eq 'PARENT_OUTSIDE_LABEL'}).Count){
    throw 'Out-of-label reference must resolve when all imported issues are shown.'
}
Write-Host 'PASS: Phase 2A topology diagnostics, conflict and cycle detection, source preservation, exact label boundary and plan integration.' -ForegroundColor Green
