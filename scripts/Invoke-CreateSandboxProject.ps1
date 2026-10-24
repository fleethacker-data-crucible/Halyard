<#
.SYNOPSIS
  Create or locate a Cascade test/sandbox GitLab project.
#>
param(
    [string]$GroupPath = 'nmmes_bip/sm-dats-sagemaker',
    [string]$ProjectName = 'cascade-test-sandbox',
    [string]$UseExistingPath = '',
    [switch]$SetCascadeEnv
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

$token = $env:GITLAB_TOKEN
if (-not $token -and (Get-Command Get-CascadeGitLabToken -ErrorAction SilentlyContinue)) {
    try { $token = Get-CascadeGitLabToken -AllowPrompt } catch {}
}
if (-not $token -and (Get-Command Select-CascadeArcSecret -ErrorAction SilentlyContinue)) {
    $token = Select-CascadeArcSecret -Purpose 'GitLab / web.git.mil' -EnvTarget 'GITLAB_TOKEN' -RememberKey 'GitLab' -AllowSkip
}
if (-not $token) { throw 'GITLAB_TOKEN required (ARC gitmil)' }

$baseUrl = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
$api = "$baseUrl/api/v4"
$headers = @{ 'PRIVATE-TOKEN' = $token; 'Accept' = 'application/json' }

function Save-ProjectId {
    param([int]$Id, [string]$PathWithNamespace)
    Write-Host ("Project id={0} path={1}" -f $Id, $PathWithNamespace) -ForegroundColor Green
    if (-not (Test-Path (Join-Path $RepoRoot 'output'))) {
        New-Item -ItemType Directory -Path (Join-Path $RepoRoot 'output') | Out-Null
    }
    $Id | Set-Content (Join-Path $RepoRoot 'output\sandbox_project_id.txt') -Encoding UTF8
    if ($SetCascadeEnv) {
        $line = "GITLAB_PROJECT_ID=$Id"
        $envPath = Join-Path $RepoRoot 'config\cascade.env'
        if (Test-Path $envPath) {
            $raw = Get-Content $envPath -Raw
            if ($raw -match '(?m)^GITLAB_PROJECT_ID=') {
                $raw = [regex]::Replace($raw, '(?m)^GITLAB_PROJECT_ID=.*$', $line)
            } else {
                $raw = $raw.TrimEnd() + "`r`n" + $line + "`r`n"
            }
            Set-Content $envPath $raw -Encoding UTF8
        } else {
            Set-Content $envPath "# cascade`r`n$line`r`n" -Encoding UTF8
        }
        $env:GITLAB_PROJECT_ID = "$Id"
        Write-Host "Updated config\cascade.env GITLAB_PROJECT_ID=$Id" -ForegroundColor Green
    }
    Write-Host ("Browse: {0}/{1}/-/work_items" -f $baseUrl, $PathWithNamespace) -ForegroundColor Cyan
}

function Get-ProjectByPath([string]$Path) {
    $Path = $Path.Trim().Trim('/')
    $enc = [uri]::EscapeDataString($Path)
    # EscapeDataString encodes / as %2F which GitLab expects for path lookup
    try {
        return Invoke-RestMethod -Method Get -Uri "$api/projects/$enc" -Headers $headers -TimeoutSec 60
    } catch {
        Write-Host ("  projects/{0} -> {1}" -f $Path, $_.Exception.Message) -ForegroundColor DarkYellow
        return $null
    }
}

function Search-Projects([string]$Search) {
    $q = [uri]::EscapeDataString($Search)
    try {
        return @(Invoke-RestMethod -Method Get -Uri "$api/projects?search=$q&simple=true&per_page=20" -Headers $headers -TimeoutSec 60)
    } catch {
        Write-Host ("  search failed: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
        return @()
    }
}

function Get-GroupByPath([string]$Path) {
    $Path = $Path.Trim().Trim('/')
    $enc = [uri]::EscapeDataString($Path)
    try {
        return Invoke-RestMethod -Method Get -Uri "$api/groups/$enc" -Headers $headers -TimeoutSec 60
    } catch {
        Write-Host ("  groups/{0} -> {1}" -f $Path, $_.Exception.Message) -ForegroundColor DarkYellow
        return $null
    }
}

function Get-GroupProjects([int]$GroupId) {
    try {
        return @(Invoke-RestMethod -Method Get -Uri "$api/groups/$GroupId/projects?per_page=50&include_subgroups=true" -Headers $headers -TimeoutSec 90)
    } catch {
        return @()
    }
}

if ($UseExistingPath) {
    $path = $UseExistingPath.Trim().Trim('/')
    Write-Host ("Resolving: {0}" -f $path) -ForegroundColor Cyan

    $proj = Get-ProjectByPath $path
    if ($proj) {
        Save-ProjectId -Id ([int]$proj.id) -PathWithNamespace $proj.path_with_namespace
        exit 0
    }

    # Maybe last segment is project under parent group
    $parts = $path -split '/'
    if ($parts.Count -ge 2) {
        $parent = ($parts[0..($parts.Count-2)] -join '/')
        $leaf = $parts[-1]
        Write-Host ("Trying parent group {0} for project named {1}..." -f $parent, $leaf) -ForegroundColor DarkGray
        $g = Get-GroupByPath $parent
        if ($g) {
            $plist = Get-GroupProjects ([int]$g.id)
            $hit = $plist | Where-Object { $_.path -eq $leaf -or $_.name -eq $leaf -or $_.path_with_namespace -eq $path } | Select-Object -First 1
            if ($hit) {
                Save-ProjectId -Id ([int]$hit.id) -PathWithNamespace $hit.path_with_namespace
                exit 0
            }
            Write-Host 'Projects under that group:' -ForegroundColor Yellow
            $i = 0
            foreach ($pr in ($plist | Select-Object -First 30)) {
                $i++
                Write-Host ("  [{0}] id={1}  {2}" -f $i, $pr.id, $pr.path_with_namespace)
            }
            if ($plist.Count -eq 0) {
                Write-Host '  (none visible - token may lack access)' -ForegroundColor Yellow
            } else {
                $pick = Read-Host '  Enter number to use, or project id, or blank cancel'
                if ($pick -match '^\d+$') {
                    $n = [int]$pick
                    if ($n -ge 1 -and $n -le $plist.Count) {
                        $sel = $plist[$n-1]
                        Save-ProjectId -Id ([int]$sel.id) -PathWithNamespace $sel.path_with_namespace
                        exit 0
                    }
                    # treat as project id
                    try {
                        $byId = Invoke-RestMethod -Method Get -Uri "$api/projects/$n" -Headers $headers -TimeoutSec 60
                        Save-ProjectId -Id ([int]$byId.id) -PathWithNamespace $byId.path_with_namespace
                        exit 0
                    } catch {}
                }
            }
        }
    }

    # Full path might BE a group (work_items at group level)
    $grp = Get-GroupByPath $path
    if ($grp) {
        Write-Host ("'{0}' is a GROUP (id={1}), not a project." -f $path, $grp.id) -ForegroundColor Yellow
        Write-Host 'Cascade needs a Project id for issue/work item writes.' -ForegroundColor Yellow
        $plist = Get-GroupProjects ([int]$grp.id)
        if ($plist.Count -eq 0) {
            Write-Host 'No projects under this group. Create one with menu [T] option 2, or pick another path.' -ForegroundColor Red
            exit 1
        }
        Write-Host 'Projects in this group/subgroups:' -ForegroundColor Cyan
        $i = 0
        foreach ($pr in ($plist | Select-Object -First 40)) {
            $i++
            Write-Host ("  [{0}] id={1}  {2}" -f $i, $pr.id, $pr.path_with_namespace)
        }
        $pick = Read-Host '  Enter number or project id'
        if ($pick -match '^\d+$') {
            $n = [int]$pick
            if ($n -ge 1 -and $n -le [Math]::Min(40, $plist.Count)) {
                $sel = @($plist)[$n-1]
                Save-ProjectId -Id ([int]$sel.id) -PathWithNamespace $sel.path_with_namespace
                exit 0
            }
            $byId = Invoke-RestMethod -Method Get -Uri "$api/projects/$n" -Headers $headers -TimeoutSec 60
            Save-ProjectId -Id ([int]$byId.id) -PathWithNamespace $byId.path_with_namespace
            exit 0
        }
        exit 1
    }

    Write-Host 'Search by leaf name...' -ForegroundColor DarkGray
    $leaf = ($path -split '/')[-1]
    $hits = Search-Projects $leaf
    if ($hits.Count -gt 0) {
        Write-Host 'Search hits:' -ForegroundColor Cyan
        $i = 0
        foreach ($pr in $hits) {
            $i++
            Write-Host ("  [{0}] id={1}  {2}" -f $i, $pr.id, $pr.path_with_namespace)
        }
        $pick = Read-Host '  Enter number or blank cancel'
        if ($pick -match '^\d+$') {
            $n = [int]$pick
            if ($n -ge 1 -and $n -le $hits.Count) {
                $sel = $hits[$n-1]
                Save-ProjectId -Id ([int]$sel.id) -PathWithNamespace $sel.path_with_namespace
                exit 0
            }
        }
    }

    Write-Host '404: path is not a visible project for this token.' -ForegroundColor Red
    Write-Host 'Fix: use [T] option 2 to create cascade-test-sandbox, or option 3 with full path_with_namespace.' -ForegroundColor Yellow
    Write-Host 'Or paste numeric project id from GitLab project Settings > General.' -ForegroundColor Yellow
    $idPick = Read-Host '  Paste numeric project id (or blank exit)'
    if ($idPick -match '^\d+$') {
        $byId = Invoke-RestMethod -Method Get -Uri "$api/projects/$idPick" -Headers $headers -TimeoutSec 60
        Save-ProjectId -Id ([int]$byId.id) -PathWithNamespace $byId.path_with_namespace
        exit 0
    }
    exit 1
}

# Create new project under GroupPath
Write-Host ("Create {0}/{1}" -f $GroupPath, $ProjectName) -ForegroundColor Cyan
$ns = Get-GroupByPath $GroupPath
if (-not $ns) {
    try {
        $enc = [uri]::EscapeDataString($GroupPath)
        $ns = Invoke-RestMethod -Method Get -Uri "$api/namespaces/$enc" -Headers $headers -TimeoutSec 60
    } catch {
        throw "Cannot resolve group/namespace: $GroupPath"
    }
}
$namespaceId = [int]$ns.id
Write-Host ("Namespace id={0}" -f $namespaceId) -ForegroundColor DarkGray

$fullPath = "$GroupPath/$ProjectName"
$existing = Get-ProjectByPath $fullPath
if ($existing) {
    Write-Host 'Already exists - using it.' -ForegroundColor Yellow
    Save-ProjectId -Id ([int]$existing.id) -PathWithNamespace $existing.path_with_namespace
    exit 0
}

$body = @{
    name                 = $ProjectName
    path                 = $ProjectName
    namespace_id         = $namespaceId
    visibility           = 'private'
    description          = 'Cascade safe-test sandbox for dups. Delete test work items freely.'
    issues_enabled       = $true
    initialize_with_readme = $true
} | ConvertTo-Json

$created = Invoke-RestMethod -Method Post -Uri "$api/projects" -Headers $headers `
    -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
Save-ProjectId -Id ([int]$created.id) -PathWithNamespace $created.path_with_namespace
Write-Host 'Created sandbox project.' -ForegroundColor Green
