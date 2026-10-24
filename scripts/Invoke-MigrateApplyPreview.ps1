<#
.SYNOPSIS
  Show what would be written for one plan row (no GitLab write unless -Apply).
#>
param(
    [int]$Iid = 0,
    [switch]$Apply,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$planPath = Join-Path $RepoRoot 'output\migrate_plan.csv'
if (-not (Test-Path $planPath)) {
    Write-Host 'Run [2] then [8] then [M] merge first.' -ForegroundColor Yellow
    exit 1
}
$rows = @(Import-Csv $planPath)
if ($Iid -gt 0) {
    $rows = @($rows | Where-Object { [int]$_.Iid -eq $Iid })
} else {
    $rows = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.ProposedParent) } | Select-Object -First 1)
}
if ($rows.Count -eq 0) {
    Write-Host 'No matching plan row (need ProposedParent or pass -Iid).' -ForegroundColor Yellow
    exit 1
}
$row = $rows[0]
Write-Host '=== Migrate preview (one issue) ===' -ForegroundColor Cyan
Write-Host ("Iid            : {0}" -f $row.Iid)
Write-Host ("ProposedType   : {0}" -f $row.ProposedType)
Write-Host ("ProposedId     : {0}" -f $row.ProposedId)
Write-Host ("ProposedParent : {0}" -f $row.ProposedParent)
Write-Host ("ProposedTitle  : {0}" -f $row.ProposedTitle)

$meta = @{
    type   = [string]$row.ProposedType
    id     = [string]$row.ProposedId
    parent = [string]$row.ProposedParent
    state  = 'OPEN'
}
$block = New-CascadeMetaBlock -Meta $meta
Write-Host ''
Write-Host 'Metadata block that would be added:' -ForegroundColor Cyan
Write-Host $block
Write-Host ''
Write-Host 'Visible mirror:' -ForegroundColor Cyan
Write-Host (New-CascadeVisibleMirror -Meta $meta)

if (-not $Apply) {
    Write-Host ''
    Write-Host 'No GitLab write. To apply THIS one row only after review:' -ForegroundColor Yellow
    Write-Host ("  menu path or: Invoke-CascadeMigrateApply -PlanCsv output\migrate_plan.csv  (whole file)") -ForegroundColor DarkGray
    Write-Host 'Prefer editing CSV so only approved rows keep ProposedParent/Type before full apply.' -ForegroundColor DarkGray
    exit 0
}

Write-Host ''
Write-Host 'APPLY is whole-plan today. Filtering plan to this one iid for safety...' -ForegroundColor Yellow
$one = Join-Path $RepoRoot 'output\migrate_plan_one.csv'
@($row) | Export-Csv -Path $one -NoTypeInformation -Encoding UTF8
if ($Force) {
    Invoke-CascadeMigrateApply -PlanCsv $one -Force
} else {
    Invoke-CascadeMigrateApply -PlanCsv $one -Confirm
}
