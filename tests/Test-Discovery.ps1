$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src\Cascade\Cascade.psd1') -Force
$plan = @(ConvertTo-CascadeDiscoveryPlan -Activity @(
    [pscustomobject]@{ Repository = 'C:\repo'; EvidenceId = 'git:abc'; Timestamp = '2026-10-20T10:00:00Z'; Subject = 'Add feature'; Files = @('src/a.ps1') }
))
if ($plan.Count -ne 1) { throw 'Expected one proposal' }
if ($plan[0].Completion -ne 'UNVERIFIED') { throw 'Never assume completion' }
if ($null -ne $plan[0].EstimatedHours) { throw 'Never invent hours' }
$oldMode = $env:CASCADE_WRITE_MODE
try {
    $env:CASCADE_WRITE_MODE = 'sandbox'
    $ids = Get-CascadeProjectIds
    $blocked = $false
    try { Assert-CascadeWriteTarget -ProjectId ($ids.SandboxProjectId + 1) | Out-Null }
    catch { $blocked = $true }
    if (-not $blocked) { throw 'Non-sandbox write was not blocked' }
} finally { $env:CASCADE_WRITE_MODE = $oldMode }
Write-Host 'Discovery tests passed.'
