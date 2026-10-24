<#
.SYNOPSIS
  Clone a real GitLab issue as [CASCADE-DUP], then optionally apply Cascade metadata
  ONLY to the new issue. Never updates the source iid.
#>
param(
    [Parameter(Mandatory)][int]$SourceIid,
    [switch]$ApplyMeta,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or $line -eq '') { return }
        if ($line -match '^\s*([^=]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"').Trim("'")
        }
    }
}

Write-Host '=== Duplicate GitLab issue (safe write test) ===' -ForegroundColor Cyan
Write-Host ("Source iid : #{0}" -f $SourceIid) -ForegroundColor Yellow
Write-Host 'The SOURCE issue will NOT be modified.' -ForegroundColor Green

function Get-LocalGitLabBase {
    if (Get-Command Get-CascadeGitLabBase -ErrorAction SilentlyContinue) {
        return Get-CascadeGitLabBase
    }
    Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
    $url = $env:GITLAB_URL
    if ([string]::IsNullOrWhiteSpace($url)) { $url = 'https://web.git.mil' }
    $projectId = $env:GITLAB_PROJECT_ID
    if ([string]::IsNullOrWhiteSpace($projectId)) { throw 'GITLAB_PROJECT_ID not set' }
    if ($projectId -match '^\d+$') { return ($url.TrimEnd('/') + '/api/v4/projects/' + $projectId) }
    $enc = [uri]::EscapeDataString($projectId)
    return ($url.TrimEnd('/') + '/api/v4/projects/' + $enc)
}
function Get-LocalGitLabHeaders {
    if (Get-Command Get-CascadeGitLabHeaders -ErrorAction SilentlyContinue) {
        return Get-CascadeGitLabHeaders
    }
    $tok = Get-CascadeGitLabToken -AllowPrompt
    return @{
        'PRIVATE-TOKEN' = $tok
        'Content-Type'  = 'application/json'
        'Accept'        = 'application/json'
    }
}
$base = Get-LocalGitLabBase
$headers = Get-LocalGitLabHeaders

Write-Host 'GET source issue...' -ForegroundColor DarkGray
$src = Invoke-RestMethod -Method Get -Uri "$base/issues/$SourceIid" -Headers $headers -TimeoutSec 60
Write-Host ("  title : {0}" -f $src.title)
Write-Host ("  state : {0}" -f $src.state)
Write-Host ("  url   : {0}" -f $src.web_url)

$labs = @()
try {
    if ($src.labels -is [System.Array]) { $labs = @($src.labels | ForEach-Object { "$_" }) }
} catch { }
if ($labs -notcontains 'cascade-test') { $labs += 'cascade-test' }
if ($labs -notcontains 'area::data-crucible') {
    # keep area if source had it; otherwise leave as-is
}

$dupTitle = '[CASCADE-DUP] {0}' -f $src.title
$dupBody = @"
> **CASCADE TEST DUPLICATE** of !$SourceIid - safe to edit/delete.
> Original: $($src.web_url)

$($src.description)
"@

Write-Host ''
Write-Host 'Will CREATE a new issue:' -ForegroundColor Cyan
Write-Host ("  title  : {0}" -f $dupTitle)
Write-Host ("  labels : {0}" -f ($labs -join ', '))
if (-not $Force) {
    $yn = Read-Host '  Type YES to create the duplicate on web.git.mil'
    if ($yn -ne 'YES') {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        exit 0
    }
}

$payload = @{
    title       = $dupTitle
    description = $dupBody
    labels      = ($labs -join ',')
} | ConvertTo-Json -Depth 5

$created = Invoke-RestMethod -Method Post -Uri "$base/issues" -Headers $headers `
    -Body ([Text.Encoding]::UTF8.GetBytes($payload)) `
    -ContentType 'application/json; charset=utf-8' -TimeoutSec 60

$newIid = [int]$created.iid
Write-Host ("Created NEW issue !{0}" -f $newIid) -ForegroundColor Green
Write-Host ("  {0}" -f $created.web_url)

# Load plan row for source (if present) to build apply plan for NEW iid
$planPath = Join-Path $RepoRoot 'output\migrate_plan.csv'
$planRow = $null
if (Test-Path $planPath) {
    $planRow = @(Import-Csv -LiteralPath $planPath) | Where-Object {
        try { [int]$_.Iid -eq $SourceIid } catch { $false }
    } | Select-Object -First 1
}

if (-not $planRow) {
    Write-Host 'No migrate_plan.csv row for source; using defaults for apply meta.' -ForegroundColor Yellow
    $planRow = [pscustomobject]@{
        Iid            = $newIid
        ProposedType   = 'STORY'
        ProposedId     = "STORY-$newIid"
        ProposedParent = 'FEATURE-aws-core'
        ProposedTitle  = $dupTitle
        AlreadyHasMeta = 'False'
    }
} else {
    # remap plan to NEW iid
    $planRow = [pscustomobject]@{
        Iid            = $newIid
        ProposedType   = $planRow.ProposedType
        ProposedId     = ($planRow.ProposedId -replace [regex]::Escape([string]$SourceIid), [string]$newIid)
        ProposedParent = $planRow.ProposedParent
        ProposedTitle  = $dupTitle
        AlreadyHasMeta = 'False'
        CurrentTitle   = $dupTitle
        Milestone      = $planRow.Milestone
        Labels         = $planRow.Labels
    }
}

$onePlan = Join-Path $RepoRoot 'output\migrate_plan_dup_test.csv'
@($planRow) | Export-Csv -Path $onePlan -NoTypeInformation -Encoding UTF8
Write-Host ("Plan row for NEW iid written: {0}" -f $onePlan) -ForegroundColor DarkGray
Write-Host ("  Type={0} Id={1} Parent={2}" -f $planRow.ProposedType, $planRow.ProposedId, $planRow.ProposedParent)

$meta = @{
    type   = [string]$planRow.ProposedType
    id     = [string]$planRow.ProposedId
    parent = [string]$planRow.ProposedParent
    state  = 'OPEN'
}
$block = New-CascadeMetaBlock -Meta $meta
Write-Host ''
Write-Host 'Metadata that would be applied to the DUPLICATE only:' -ForegroundColor Cyan
Write-Host $block

if (-not $ApplyMeta) {
    Write-Host ''
    Write-Host 'Duplicate created. Metadata NOT applied yet.' -ForegroundColor Yellow
    Write-Host ("To apply meta to NEW !{0} only:" -f $newIid) -ForegroundColor Cyan
    Write-Host '  [M] -> 3 and enter the NEW iid above, OR re-run this with -ApplyMeta' -ForegroundColor DarkGray
    Write-Host ("  NEW_IID={0}" -f $newIid)
    # persist for host
    $newIid | Set-Content (Join-Path $RepoRoot 'output\last_dup_iid.txt') -Encoding UTF8
    exit 0
}

Write-Host ''
Write-Host ("Applying Cascade metadata to NEW !{0} only (source !{1} untouched)..." -f $newIid, $SourceIid) -ForegroundColor Cyan
Invoke-CascadeMigrateApply -PlanCsv $onePlan -Force
Write-Host 'Done. Open the NEW issue URL above to verify.' -ForegroundColor Green
$newIid | Set-Content (Join-Path $RepoRoot 'output\last_dup_iid.txt') -Encoding UTF8
