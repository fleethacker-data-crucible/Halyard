$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src/Cascade/Cascade.psd1') -Force
# Ensure integrity is available even if omitted from FunctionsToExport
if (-not (Get-Command Test-CascadeHierarchyIntegrity -ErrorAction SilentlyContinue)) {
    . (Join-Path $root 'src/Cascade/Integrity.ps1')
}
if (-not (Get-Command Test-CascadeHierarchyIntegrity -ErrorAction SilentlyContinue)) {
    throw 'Test-CascadeHierarchyIntegrity not found after module import and dot-source'
}
function New-Issue([string]$id,[string]$type,[string]$parent) {
    [pscustomobject]@{ description = "<!--`ntype: $type`nid: $id`nparent: $parent`n-->" }
}
$valid = @( (New-Issue 'EPIC-1' 'EPIC' ''), (New-Issue 'FEATURE-1' 'FEATURE' 'EPIC-1'), (New-Issue 'STORY-1' 'STORY' 'FEATURE-1') )
$r = Test-CascadeHierarchyIntegrity -Issues $valid
if (-not $r.IsValid) { throw 'Valid hierarchy was rejected' }
$bad = @(
 (New-Issue 'STORY-1' 'STORY' 'STORY-2'),
 (New-Issue 'STORY-2' 'STORY' 'STORY-1'),
 (New-Issue 'TASK-1' 'TASK' 'ABSENT'),
 (New-Issue 'TASK-1' 'TASK' 'ABSENT'),
 (New-Issue 'EPIC-3' 'EPIC' 'EPIC-3')
)
$r = Test-CascadeHierarchyIntegrity -Issues $bad
foreach ($code in @('CYCLE','MISSING_PARENT','DUPLICATE_ID','SELF_PARENT','INVALID_PARENT_TYPE')) {
    if ($code -notin @($r.Findings | ForEach-Object Code)) { throw "Missing detection: $code" }
}
Write-Host 'Integrity checks passed.'
