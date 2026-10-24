$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue

$failed = 0
function Assert-True($cond, $msg) {
    if (-not $cond) { Write-Host "FAIL: $msg" -ForegroundColor Red; $script:failed++ }
    else { Write-Host "PASS: $msg" -ForegroundColor Green }
}

$body = @"
<!--
type: STORY
id: STORY-175
parent: FEATURE-1
state: IN_PROGRESS
-->

Hello world
"@

$m = Get-CascadeMeta -Body $body
Assert-True ($null -ne $m) 'parses metadata'
Assert-True ($m['type'] -eq 'STORY') 'type STORY'
Assert-True ($m['id'] -eq 'STORY-175') 'id'
Assert-True (Test-CascadeMeta -Meta $m) 'Test-CascadeMeta true'

$built = Build-CascadeDescription -Meta @{ type='TASK'; id='TASK-9'; parent='STORY-175'; state='TODO' } -BodyMarkdown 'Do the thing'
$m2 = Get-CascadeMeta -Body $built
Assert-True ($m2['id'] -eq 'TASK-9') 'round-trip id'

# Fixture load
$issues = Get-CascadeIssuesFromFixture -Path (Join-Path $RepoRoot 'fixtures\work_items_fixture.json')
Assert-True ($issues.Count -ge 60) "fixture has issues ($($issues.Count))"

$plainTitle = & (Get-Module Cascade) { Format-CascadeTaxonomyTitle -Type FEATURE -Name 'FEATURE :: Source Feed Contracts' }
Assert-True ($plainTitle -eq 'Source Feed Contracts') 'new work items use readable unprefixed titles'
$taxonomy=Get-CascadeTaxonomy
Assert-True ($taxonomy.title_pattern.FEATURE -eq '{name}') 'taxonomy no longer mandates an embedded type title prefix'

$metrics = Get-CascadeMetrics -Issues $issues
Assert-True ($metrics.UnparsedCount -eq $issues.Count) 'all fixture issues unparsed before migration (expected)'

if ($failed -gt 0) { Write-Host "`n$failed test(s) failed"; exit 1 }
Write-Host "`nAll tests passed."
