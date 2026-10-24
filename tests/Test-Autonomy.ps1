$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src\Cascade\Cascade.psd1') -Force
$activity=@(
    [pscustomobject]@{Repository='C:\src\api';Subject='Add authentication';EvidenceId='git:111';Timestamp='2026-10-20T09:00:00Z';Files=@('src/auth.ps1')},
    [pscustomobject]@{Repository='C:\src\api';Subject='Add authentication';EvidenceId='git:222';Timestamp='2026-10-20T11:00:00Z';Files=@('tests/auth.ps1')}
)
$clusters=@(Get-CascadeWorkClusters -Activity $activity)
if ($clusters.Count -ne 1) { throw 'Related commits should form one cluster' }
if ($clusters[0].Completion -ne 'UNVERIFIED' -or $null -ne $clusters[0].EstimatedHours) { throw 'Unsupported completion or hours inference' }
$actions=@(New-CascadeReconciliationPlan -Clusters $clusters -Issues @())
if ($actions.Count -ne 1 -or $actions[0].Action -ne 'PROPOSE_CREATE') { throw 'Expected one proposed issue' }
if (-not (Test-CascadeReconciliationPlan -Actions $actions).Valid) { throw 'Plan validation failed' }
$issues=@([pscustomobject]@{iid=12;description=$actions[0].Description})
$again=@(New-CascadeReconciliationPlan -Clusters $clusters -Issues $issues)
if ($again[0].Action -ne 'EXISTS') { throw 'Existing evidence marker must prevent duplicate proposal' }
$brief=New-CascadeDeliveryBrief -Activity $activity -Clusters $clusters -Actions $actions
if ($brief -notmatch 'not verified') { throw 'Report must warn about evidence limits' }
Write-Host 'Autonomy tests passed.'
