<#
.SYNOPSIS
  Delete (or close) issues ONLY in the Cascade test sandbox.
  Hard allowlist + path must contain cascade-test-sandbox.
  Lists every issue for review BEFORE any delete/close.
  NEVER touches CBR 16629.
#>
param(
    [int]$ProjectId = 0,
    [switch]$CloseOnly,
    [switch]$Force,
    [int]$MaxDelete = 500,
    [switch]$PreviewOnly
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^=#]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"')
        }
    }
}

$cbrProtected = 16629
$sandboxAllowedIds = @(52999)
$sandboxPathMustMatch = 'cascade-test-sandbox'

$sandboxPid = $ProjectId
if ($sandboxPid -le 0) {
    $envPid = 0
    if ($env:GITLAB_PROJECT_ID) {
        $t = 0
        if ([int]::TryParse($env:GITLAB_PROJECT_ID.Trim(), [ref]$t)) { $envPid = $t }
    }
    if ($envPid -in $sandboxAllowedIds) {
        $sandboxPid = $envPid
    } else {
        $sandboxPid = 52999
        if ($envPid -gt 0 -and $envPid -notin $sandboxAllowedIds) {
            Write-Host ("GITLAB_PROJECT_ID={0} not on allowlist - wipe target defaults to 52999" -f $envPid) -ForegroundColor Yellow
        }
    }
}

if ($sandboxPid -eq $cbrProtected) {
    throw "REFUSED: project $sandboxPid is CBR. Wipe will NEVER run against 16629."
}

Write-Host '=== Cascade SANDBOX WIPE ===' -ForegroundColor Cyan
Write-Host ("Candidate project id={0}" -f $sandboxPid) -ForegroundColor Yellow
Write-Host ("Mode: {0}" -f $(if ($CloseOnly) { 'CLOSE only' } else { 'DELETE (API)' })) -ForegroundColor Yellow

Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
$url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$tok = Get-CascadeGitLabToken -AllowPrompt
$headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }
$base = "$url/api/v4/projects/$sandboxPid"

$proj = Invoke-RestMethod -Method Get -Uri "$base" -Headers $headers -TimeoutSec 60
$path = [string]$proj.path_with_namespace
$name = [string]$proj.name
Write-Host ("GitLab path: {0}" -f $path) -ForegroundColor Cyan
Write-Host ("GitLab name: {0}" -f $name) -ForegroundColor Cyan

$pathOk = ($path -match [regex]::Escape($sandboxPathMustMatch)) -or ($name -match [regex]::Escape($sandboxPathMustMatch))
if (-not $pathOk) {
    throw @"
REFUSED: project path '$path' does not contain '$sandboxPathMustMatch'.
Wipe only runs against the Cascade test sandbox.
"@
}
if ($path -match 'aws-infrastructure$' -and $path -notmatch 'cascade-test') {
    throw "REFUSED: path looks like production backlog ($path)."
}
if ($sandboxPid -eq 16629) {
    throw "REFUSED: CBR 16629."
}

Write-Host 'Safety checks PASSED (sandbox path verified).' -ForegroundColor Green

# --- Query what would be wiped ---
$issues = @()
$page = 1
while ($page -le 20) {
    $batch = @(Invoke-RestMethod -Method Get -Uri "$base/issues?state=all&per_page=100&page=$page" -Headers $headers -TimeoutSec 120)
    if ($batch.Count -eq 0) { break }
    $issues += $batch
    if ($batch.Count -lt 100) { break }
    $page++
}

$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$previewPath = Join-Path $outDir 'sandbox_wipe_preview.csv'

$rows = @()
foreach ($iss in ($issues | Sort-Object iid)) {
    $labs = ''
    try { if ($iss.labels) { $labs = @($iss.labels) -join ',' } } catch {}
    $rows += [pscustomobject]@{
        iid         = [int]$iss.iid
        state       = [string]$iss.state
        title       = [string]$iss.title
        labels      = $labs
        author      = $(try { [string]$iss.author.username } catch { '' })
        web_url     = $(try { [string]$iss.web_url } catch { '' })
        created_at  = $(try { [string]$iss.created_at } catch { '' })
    }
}
$rows | Export-Csv -Path $previewPath -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host '=== REVIEW - items that would be wiped ===' -ForegroundColor Cyan
Write-Host ("Project: {0} (id={1})" -f $path, $sandboxPid)
Write-Host ("Count:   {0}" -f $rows.Count) -ForegroundColor Yellow
Write-Host ("Preview: {0}" -f $previewPath) -ForegroundColor DarkGray
Write-Host ''

if ($rows.Count -eq 0) {
    Write-Host 'Nothing to wipe.' -ForegroundColor Green
    exit 0
}

# Console table (truncate titles)
$show = @($rows | Select-Object -First 50)
foreach ($r in $show) {
    $t = $r.title
    if ($t.Length -gt 70) { $t = $t.Substring(0, 70) + '...' }
    Write-Host ("  !{0,-5} [{1,-6}] {2}" -f $r.iid, $r.state, $t)
}
if ($rows.Count -gt 50) {
    Write-Host ("  ... +{0} more (see CSV)" -f ($rows.Count - 50)) -ForegroundColor DarkGray
}

# Summary by title prefix
$epics = @($rows | Where-Object { $_.title -match 'EPIC\s*::' }).Count
$feats = @($rows | Where-Object { $_.title -match 'FEATURE\s*::' }).Count
$stories = @($rows | Where-Object { $_.title -match 'STORY\s*::|\[CASCADE-DUP\]' }).Count
Write-Host ''
Write-Host ("Summary: EPIC-like={0}  FEATURE-like={1}  STORY/DUP-like={2}  other={3}" -f `
    $epics, $feats, $stories, ($rows.Count - $epics - $feats - $stories)) -ForegroundColor Cyan

if ($PreviewOnly) {
    Write-Host ''
    Write-Host 'PreviewOnly - no changes made. Review CSV then re-run [X] without -PreviewOnly.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host ("Action: {0} all {1} issue(s) listed above in sandbox ONLY." -f $(if ($CloseOnly) { 'CLOSE' } else { 'DELETE' }), $rows.Count) -ForegroundColor Red

if (-not $Force) {
    Write-Host ''
    Write-Host 'Confirm 1/2: type the project path EXACTLY' -ForegroundColor Red
    $confirmPath = Read-Host '  Path'
    if ($confirmPath.Trim() -ne $path) {
        Write-Host 'Path mismatch - cancelled. Nothing deleted.' -ForegroundColor Yellow
        exit 0
    }
    Write-Host 'Confirm 2/2: type WIPE to proceed, or REVIEW to open count only and abort' -ForegroundColor Red
    $ans = Read-Host '  WIPE / REVIEW / anything else cancels'
    if ($ans -eq 'REVIEW') {
        Write-Host ("Review file: {0}" -f $previewPath) -ForegroundColor Green
        Write-Host 'Cancelled - nothing deleted.' -ForegroundColor Yellow
        exit 0
    }
    if ($ans -ne 'WIPE') {
        Write-Host 'Cancelled - nothing deleted.' -ForegroundColor Yellow
        exit 0
    }
}

$n = 0
$fail = 0
foreach ($iss in ($issues | Sort-Object iid -Descending)) {
    if ($n -ge $MaxDelete) { break }
    $iid = [int]$iss.iid
    try {
        if ($CloseOnly) {
            if ($iss.state -ne 'closed') {
                Invoke-RestMethod -Method Put -Uri "$base/issues/$iid" -Headers $headers `
                    -Body (@{ state_event = 'close' } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 60 | Out-Null
                Write-Host ("  closed !{0}" -f $iid) -ForegroundColor DarkGray
            }
        } else {
            Invoke-RestMethod -Method Delete -Uri "$base/issues/$iid" -Headers $headers -TimeoutSec 60 | Out-Null
            Write-Host ("  deleted !{0} {1}" -f $iid, $iss.title) -ForegroundColor DarkGray
        }
        $n++
    } catch {
        $fail++
        Write-Host ("  FAIL !{0}: {1}" -f $iid, $_.Exception.Message) -ForegroundColor Red
    }
}
Write-Host ''
Write-Host ("Done. processed={0} fail={1}" -f $n, $fail) -ForegroundColor Green
Write-Host ("Browse: {0}/{1}/-/work_items" -f $url, $path) -ForegroundColor Cyan
