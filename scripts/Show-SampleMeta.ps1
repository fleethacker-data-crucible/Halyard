# Demonstrate metadata round-trip offline
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue

$meta = @{
    type   = 'STORY'
    id     = 'STORY-175'
    parent = 'FEATURE-postgres-zdrive'
    state  = 'IN_PROGRESS'
    effort = 'M'
    assignee = '@danny.m.nguyen3.ctr'
}
$desc = Build-CascadeDescription -Meta $meta -BodyMarkdown @"
## Story Text
Review SQLite database files on Z Drive and document schemas.

## Acceptance Criteria
- [ ] Inventory of SQLite files complete
- [ ] Schemas documented
"@

Write-Host "=== Generated description ==="
Write-Host $desc
Write-Host ""
Write-Host "=== Parsed back ==="
Get-CascadeMeta -Body $desc | Format-List
