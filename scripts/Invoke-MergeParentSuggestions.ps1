<#
.SYNOPSIS
  Merge output\parent_suggestions.json into output\migrate_plan.csv (ProposedParent column).
  Does NOT write to GitLab.
#>
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$planPath = Join-Path $RepoRoot 'output\migrate_plan.csv'
$sugPath  = Join-Path $RepoRoot 'output\parent_suggestions.json'
$outPath  = Join-Path $RepoRoot 'output\migrate_plan_with_parents.csv'

if (-not (Test-Path $planPath)) {
    Write-Host 'Missing output\migrate_plan.csv - run menu [2] first.' -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $sugPath)) {
    Write-Host 'Missing output\parent_suggestions.json - run menu [8] first.' -ForegroundColor Yellow
    exit 1
}

$plan = @(Import-Csv -LiteralPath $planPath)
$raw  = Get-Content -LiteralPath $sugPath -Raw | ConvertFrom-Json
$suggestions = @($raw)
if ($suggestions.Count -eq 1 -and $null -eq $suggestions[0].iid -and $raw -is [System.Array]) {
    $suggestions = @($raw)
}

$map = @{}
foreach ($s in $suggestions) {
    if ($null -eq $s) { continue }
    $iid = $null
    try { $iid = [int]$s.iid } catch { continue }
    $parent = [string]$s.parent
    if ([string]::IsNullOrWhiteSpace($parent)) { continue }
    $map[$iid] = $parent
}

$updated = 0
foreach ($row in $plan) {
    $iid = 0
    try { $iid = [int]$row.Iid } catch {
        try { $iid = [int]$row.iid } catch { continue }
    }
    if ($map.ContainsKey($iid)) {
        $row.ProposedParent = $map[$iid]
        $updated++
    }
}

$plan | Export-Csv -Path $outPath -NoTypeInformation -Encoding UTF8
# also overwrite main plan so apply can use it
$plan | Export-Csv -Path $planPath -NoTypeInformation -Encoding UTF8

Write-Host ("Merged {0} parent suggestion(s) into migrate_plan.csv" -f $updated) -ForegroundColor Green
Write-Host "  $planPath"
Write-Host "  copy: $outPath"
Write-Host ''
Write-Host 'Sample rows with parents:' -ForegroundColor Cyan
$plan | Where-Object { -not [string]::IsNullOrWhiteSpace($_.ProposedParent) } |
    Select-Object -First 12 Iid, ProposedType, ProposedParent, ProposedTitle |
    Format-Table -AutoSize | Out-String | Write-Host

Write-Host 'Next: review CSV, then apply to GitLab only when ready (menu [M] dry preview or apply).' -ForegroundColor DarkGray
