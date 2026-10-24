<#
.SYNOPSIS
  Quick probe: can this machine call GitLab GraphQL with the Cascade PAT?
  Read-only. Does not create or delete anything.
#>
param(
    [string]$GitLabUrl = '',
    [switch]$VerboseQuery
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

Write-Host '=== Cascade GitLab GraphQL probe (read-only) ===' -ForegroundColor Cyan

$url = $GitLabUrl
if (-not $url) {
    if ($env:GITLAB_URL) { $url = $env:GITLAB_URL.TrimEnd('/') }
    else { $url = 'https://web.git.mil' }
}
$graphqlUri = "$url/api/graphql"
Write-Host ("Endpoint: {0}" -f $graphqlUri) -ForegroundColor Yellow

$tok = $null
try {
    $tok = Get-CascadeGitLabToken -AllowPrompt
} catch {
    Write-Host ("Token resolve failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    throw
}
if ([string]::IsNullOrWhiteSpace($tok)) { throw 'Empty GITLAB_TOKEN' }
Write-Host ("Token: present (len={0})" -f $tok.Length) -ForegroundColor DarkGray

$headers = @{
    'PRIVATE-TOKEN' = $tok
    'Content-Type'  = 'application/json'
    'Accept'        = 'application/json'
}

# Minimal read-only query
$query = @'
query CascadeGraphqlProbe {
  currentUser {
    id
    username
    name
  }
}
'@

$body = @{ query = $query } | ConvertTo-Json -Depth 5
Write-Host 'Sending currentUser query...' -ForegroundColor Cyan

$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$rawPath = Join-Path $outDir 'graphql_probe_raw.json'

try {
    $resp = Invoke-RestMethod -Method Post -Uri $graphqlUri -Headers $headers `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' `
        -TimeoutSec 60
    ($resp | ConvertTo-Json -Depth 10) | Set-Content $rawPath -Encoding UTF8
} catch {
    Write-Host 'FAIL: HTTP call threw' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
        Write-Host $_.ErrorDetails.Message -ForegroundColor DarkYellow
        $_.ErrorDetails.Message | Set-Content (Join-Path $outDir 'graphql_probe_error.txt') -Encoding UTF8
    }
    Write-Host ''
    Write-Host 'Possible causes:' -ForegroundColor Yellow
    Write-Host '  - Nautilus cannot reach web.git.mil /api/graphql'
    Write-Host '  - GraphQL disabled on this GitLab'
    Write-Host '  - PAT invalid or missing api scope'
    Write-Host '  - Proxy/TLS interception'
    exit 1
}

if ($resp.errors) {
    Write-Host 'GraphQL returned errors (endpoint reachable, query rejected):' -ForegroundColor Yellow
    $resp.errors | ForEach-Object { Write-Host ("  - {0}" -f $_.message) -ForegroundColor Red }
    Write-Host ("Raw: {0}" -f $rawPath) -ForegroundColor DarkGray
    exit 2
}

$user = $null
try { $user = $resp.data.currentUser } catch { }
if (-not $user) {
    Write-Host 'Unexpected response shape (no data.currentUser).' -ForegroundColor Yellow
    Write-Host ("Raw: {0}" -f $rawPath)
    Get-Content $rawPath -TotalCount 40
    exit 3
}

Write-Host ''
Write-Host 'SUCCESS: GraphQL is reachable and authenticated.' -ForegroundColor Green
Write-Host ("  username : {0}" -f $user.username)
Write-Host ("  name     : {0}" -f $user.name)
Write-Host ("  id       : {0}" -f $user.id)
Write-Host ("  raw      : {0}" -f $rawPath)

# Optional second probe: work items types (still read-only)
$q2 = @'
query CascadeWorkItemTypes {
  namespace(fullPath: "nmmes_bip/sm-dats-sagemaker/cascade-test-sandbox") {
    id
    fullPath
    workItemTypes {
      nodes { name iconName }
    }
  }
}
'@
$body2 = @{ query = $q2 } | ConvertTo-Json -Depth 5
Write-Host ''
Write-Host 'Follow-up: workItemTypes on cascade-test-sandbox (read-only)...' -ForegroundColor Cyan
try {
    $resp2 = Invoke-RestMethod -Method Post -Uri $graphqlUri -Headers $headers `
        -Body ([Text.Encoding]::UTF8.GetBytes($body2)) -ContentType 'application/json; charset=utf-8' `
        -TimeoutSec 60
    ($resp2 | ConvertTo-Json -Depth 12) | Set-Content (Join-Path $outDir 'graphql_probe_workitems.json') -Encoding UTF8
    if ($resp2.errors) {
        Write-Host '  workItemTypes: errors (GraphQL works, but this field may be unavailable)' -ForegroundColor Yellow
        $resp2.errors | Select-Object -First 3 | ForEach-Object { Write-Host ("    {0}" -f $_.message) -ForegroundColor DarkYellow }
    } elseif ($resp2.data.namespace) {
        Write-Host ("  namespace: {0}" -f $resp2.data.namespace.fullPath) -ForegroundColor Green
        $types = @($resp2.data.namespace.workItemTypes.nodes)
        Write-Host ("  work item types: {0}" -f $types.Count) -ForegroundColor Green
        $types | Select-Object -First 12 | ForEach-Object { Write-Host ("    - {0}" -f $_.name) -ForegroundColor DarkGray }
        if ($types.Count -eq 0) {
            Write-Host '  (empty types list - hierarchy may need REST/cascade-meta only)' -ForegroundColor Yellow
        }
    } else {
        Write-Host '  namespace null - path missing or no access' -ForegroundColor Yellow
    }
} catch {
    Write-Host ("  follow-up soft-fail: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
}

Write-Host ''
Write-Host 'Summary:' -ForegroundColor Cyan
Write-Host '  GraphQL endpoint : OK'
Write-Host '  Auth             : OK'
Write-Host '  Hierarchy next   : only if workItemTypes / parent mutations exist on this instance'
Write-Host '  Nautilus         : this run succeeded from THIS machine; re-run [Y] on Nautilus to confirm there.'
