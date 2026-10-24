# Cascade GitLab (web.git.mil) client

function Import-CascadeGitLabEnv {
    <#
    .SYNOPSIS
      Load GITLAB_* from config\cascade.env if not already set.
    #>
    [CmdletBinding()]
    param()
    if ($env:GITLAB_TOKEN -and $env:GITLAB_URL -and $env:GITLAB_PROJECT_ID) { return }

    $repo = $null
    try {
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    } catch { }
    $candidates = @()
    if ($repo) { $candidates += (Join-Path $repo 'config\cascade.env') }
    $candidates += (Join-Path (Get-Location) 'config\cascade.env')

    foreach ($envFile in $candidates) {
        if (-not (Test-Path -LiteralPath $envFile)) { continue }
        Get-Content -LiteralPath $envFile | ForEach-Object {
            $line = $_.Trim()
            if ($line -match '^\s*#' -or $line -eq '') { return }
            if ($line -match '^\s*([^=]+)=(.*)$') {
                $k = $Matches[1].Trim()
                $v = $Matches[2].Trim().Trim('"').Trim("'")
                if ($k -match '^(GITLAB_|ARC_PATH|CASCADE_ARC_ITEM_)') {
                    if (-not [string]::IsNullOrWhiteSpace($v)) {
                        # Explicit session/browser mappings take precedence over legacy cascade.env.
                        if (-not [Environment]::GetEnvironmentVariable($k)) {
                            Set-Item -Path ("Env:{0}" -f $k) -Value $v
                        }
                    }
                }
            }
        }
        Write-Verbose "Loaded GitLab settings from $envFile"
        break
    }
}

function Test-CascadeArcNameLooksLikeGitLab {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    $n = $Name.ToLowerInvariant()
    # Reject known non-GitLab services
    if ($n -match 'advana|databricks|jupiter|genapi|grok|gartner|aws|hackathon|^github$') { return $false }
    if ($n -match 'gitmil|gitlab|git\.mil|web\.git') { return $true }
    # unknown name - allow but caller may warn
    return $true
}

function Clear-CascadeGitLabTokenSession {
    <#
    .SYNOPSIS
      Drop bad session token / wrong ARC mapping for GitLab (e.g. genapi saved by mistake).
      Always removes bad CASCADE_ARC_ITEM_GitLab from cascade.env and sets gitmil when possible.
    #>
    [CmdletBinding()]
    param([switch]$ClearRemembered)
    Remove-Item Env:GITLAB_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:CASCADE_ARC_ITEM_GitLab -ErrorAction SilentlyContinue
    try {
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $envPath = Join-Path $repo 'config\cascade.env'
        if (Test-Path -LiteralPath $envPath) {
            $raw = Get-Content -LiteralPath $envPath -Raw
            $raw = [regex]::Replace($raw, '(?m)^CASCADE_ARC_ITEM_GitLab=.*\r?\n?', '')
            $raw = [regex]::Replace($raw, '(?m)^GITLAB_TOKEN=.*\r?\n?', '')
            if ($raw -notmatch '(?m)^CASCADE_ARC_ITEM_GitLab=') {
                $raw = $raw.TrimEnd() + "`r`nCASCADE_ARC_ITEM_GitLab=gitmil`r`n"
            }
            Set-Content -Path $envPath -Value $raw -Encoding UTF8
            Write-Host '[*] Fixed cascade.env: CASCADE_ARC_ITEM_GitLab=gitmil (was wrong/cleared)' -ForegroundColor Yellow
        }
    } catch {
        Write-Host ("[!] Could not rewrite cascade.env: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    }
}

function Get-CascadeGitLabToken {
    <#
    .SYNOPSIS
      GITLAB_TOKEN from env, or interactive ARC secret pick (operator chooses item).
      Must be web.git.mil PAT (ARC item gitmil) - not advana-api / genapi.
    #>
    [CmdletBinding()]
    param([switch]$AllowPrompt)

    Import-CascadeGitLabEnv

    # The synchronous browser host never calls a vault getter while serving a
    # request. All ARC service entries are resolved before startup.
    if($env:CASCADE_WEB_NONINTERACTIVE -eq '1'){
        if(-not [string]::IsNullOrWhiteSpace($env:GITLAB_TOKEN)){return $env:GITLAB_TOKEN.Trim()}
        return $null
    }

    # Default mapping for this product: DoW GitLab = gitmil (not commercial github)
    if ([string]::IsNullOrWhiteSpace($env:CASCADE_ARC_ITEM_GitLab)) {
        $env:CASCADE_ARC_ITEM_GitLab = 'gitmil'
    }
    if ($env:CASCADE_ARC_ITEM_GitLab -match '^(?i)github$') {
        Write-Host '[!] github is commercial/non-CUI only. For Cascade use gitmil (web.git.mil).' -ForegroundColor Yellow
        $env:CASCADE_ARC_ITEM_GitLab = 'gitmil'
    }

    # Auto-load from remembered ARC item name (profile-specific)
    if ([string]::IsNullOrWhiteSpace($env:GITLAB_TOKEN) -and $env:CASCADE_ARC_ITEM_GitLab) {
        $arcName = $env:CASCADE_ARC_ITEM_GitLab.Trim()
        if (-not (Test-CascadeArcNameLooksLikeGitLab -Name $arcName)) {
            Write-Host ("[!] CASCADE_ARC_ITEM_GitLab={0} does not look like a GitLab PAT item (use gitmil, not advana-api/genapi)." -f $arcName) -ForegroundColor Red
            Write-Host '    Fixing cascade.env to gitmil. Prefer Enter/recommended on next pick.' -ForegroundColor Yellow
            Clear-CascadeGitLabTokenSession
            $env:CASCADE_ARC_ITEM_GitLab = 'gitmil'
        } elseif (Get-Command Get-CascadeArcSecretByName -ErrorAction SilentlyContinue) {
            try {
                $null = Import-CascadeArcSession -ErrorAction SilentlyContinue
                $got = Get-CascadeArcSecretByName -ServiceName $arcName
                $plain = $null
                if ($got -is [string]) { $plain = $got.Trim() }
                elseif ($got -and $got.PSObject.Properties['Secret']) { $plain = ([string]$got.Secret).Trim() }
                elseif ($got) { $plain = ([string]$got).Trim() }
                if (-not [string]::IsNullOrWhiteSpace($plain)) {
                    $env:GITLAB_TOKEN = $plain
                    Write-Host ("[*] Loaded GitLab token from ARC item '{0}' (len={1})" -f $arcName, $plain.Length) -ForegroundColor DarkGray
                }
            } catch { }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITLAB_TOKEN)) {
        return $env:GITLAB_TOKEN.Trim()
    }

    if ($AllowPrompt -and (Get-Command Select-CascadeArcSecret -ErrorAction SilentlyContinue)) {
        Write-Host 'Select ARC item for GitLab PAT (usually: gitmil). Do NOT pick advana-api or genapi.' -ForegroundColor Cyan
        $fromArc = Select-CascadeArcSecret -Purpose 'GitLab / web.git.mil PAT (gitmil)' `
            -EnvTarget 'GITLAB_TOKEN' -RememberKey 'GitLab' -TryLoadArc -AllowSkip
        if ($fromArc) {
            # If user just remembered a bad name, warn
            if ($env:CASCADE_ARC_ITEM_GitLab -and -not (Test-CascadeArcNameLooksLikeGitLab -Name $env:CASCADE_ARC_ITEM_GitLab)) {
                Write-Host ("[!] You mapped '{0}' as GitLab - that will 401. Re-map to gitmil via [S]." -f $env:CASCADE_ARC_ITEM_GitLab) -ForegroundColor Red
            }
            return $fromArc
        }
    }

    if ($AllowPrompt -and [Environment]::UserInteractive) {
        Write-Host ''
        Write-Host 'No GITLAB_TOKEN. Paste a Personal Access Token (read_api).' -ForegroundColor Yellow
        $secure = Read-Host 'GitLab token (input hidden)' -AsSecureString
        if ($null -eq $secure) { throw 'No GitLab token provided.' }
        $BSTR = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($BSTR)
        } finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($BSTR) | Out-Null
        }
        if ([string]::IsNullOrWhiteSpace($plain)) { throw 'No GitLab token provided.' }
        $env:GITLAB_TOKEN = $plain.Trim()
        Write-Host '[*] GITLAB_TOKEN set for this process only.' -ForegroundColor DarkGray
        $yn = Read-Host 'Save GITLAB_TOKEN to config\cascade.env? (y/N)'
        if ($yn -eq 'y' -or $yn -eq 'Y') {
            try {
                $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
                $envPath = Join-Path $repo 'config\cascade.env'
                $line = 'GITLAB_TOKEN={0}' -f $env:GITLAB_TOKEN
                $existing = ''
                if (Test-Path -LiteralPath $envPath) {
                    $existing = Get-Content -LiteralPath $envPath -Raw
                    if ($existing -match '(?m)^GITLAB_TOKEN=') {
                        $existing = [regex]::Replace($existing, '(?m)^GITLAB_TOKEN=.*$', $line)
                    } else {
                        $existing = $existing.TrimEnd() + "`r`n" + $line + "`r`n"
                    }
                } else {
                    $dir = Split-Path $envPath -Parent
                    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
                    $existing = "# cascade local secrets`r`nGITLAB_URL=https://web.git.mil`r`n$line`r`n"
                }
                Set-Content -Path $envPath -Value $existing -Encoding UTF8
                Write-Host '[*] Saved GITLAB_TOKEN' -ForegroundColor Green
            } catch { }
        }
        return $env:GITLAB_TOKEN
    }

    throw 'GITLAB_TOKEN is not set. Pick an ARC secret or set config\cascade.env.'
}


function Convert-CascadeRestArray {
    <#
    .SYNOPSIS
      Normalize REST JSON arrays so callers get one object per element (not System.Object[]).
    #>
    param($InputObject)
    if ($null -eq $InputObject) { return , @() }

    $list = New-Object System.Collections.Generic.List[object]

    # Already a .NET array / list of items
    if ($InputObject -is [System.Array]) {
        foreach ($item in $InputObject) {
            if ($null -eq $item) { continue }
            # Nested array (bad unwrap) - flatten one level
            if ($item -is [System.Array] -and -not ($item -is [string])) {
                foreach ($inner in $item) { if ($null -ne $inner) { [void]$list.Add($inner) } }
            } else {
                [void]$list.Add($item)
            }
        }
        return , $list.ToArray()
    }

    if ($InputObject -is [System.Collections.IList] -and -not ($InputObject -is [string])) {
        for ($i = 0; $i -lt $InputObject.Count; $i++) {
            $item = $InputObject[$i]
            if ($null -ne $item) { [void]$list.Add($item) }
        }
        return , $list.ToArray()
    }

    # Single PSCustomObject (one project)
    [void]$list.Add($InputObject)
    return , $list.ToArray()
}


function Invoke-CascadeGitLabGetJson {
    <#
    .SYNOPSIS
      GET JSON from GitLab with -UseBasicParsing (no IE script warning) and flat array unwrap.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][hashtable]$Headers,
        [int]$TimeoutSec = 120
    )
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }
    $resp = Invoke-WebRequest -Method Get -Uri $Uri -Headers $Headers -UseBasicParsing -TimeoutSec $TimeoutSec
    if ([string]::IsNullOrWhiteSpace($resp.Content)) { return , @() }
    $json = $resp.Content | ConvertFrom-Json
    return , (Convert-CascadeRestArray -InputObject $json)
}

function Get-CascadeGitLabProjects {
    <#
    .SYNOPSIS
      List membership projects as a flat array of {id, path_with_namespace, name, web_url}.
    #>
    [CmdletBinding()]
    param(
        [hashtable]$Headers,
        [string]$ApiRoot,
        [int]$PerPage = 50
    )
    $uri = "$ApiRoot/projects?membership=true&per_page=$PerPage&simple=true&order_by=last_activity_at&sort=desc"
    $items = Invoke-CascadeGitLabGetJson -Uri $uri -Headers $Headers -TimeoutSec 60
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($pr in $items) {
        if ($null -eq $pr) { continue }
        $idVal = $null
        $pathVal = $null
        $nameVal = $null
        $webVal = $null
        try { $idVal = $pr.id } catch { }
        try { $pathVal = $pr.path_with_namespace } catch { }
        try { $nameVal = $pr.name } catch { }
        try { $webVal = $pr.web_url } catch { }
        # Skip collection-valued properties (means unwrap still wrong)
        if ($idVal -is [System.Array]) { continue }
        if ($pathVal -is [System.Array]) { continue }
        if ($null -eq $idVal -and [string]::IsNullOrWhiteSpace([string]$pathVal)) { continue }
        [void]$out.Add([pscustomobject]@{
            id                  = [string]$idVal
            path_with_namespace = [string]$pathVal
            name                = [string]$nameVal
            web_url             = [string]$webVal
        })
    }
    return , $out.ToArray()
}

function Get-CascadeGitLabHeaders {
    [CmdletBinding()]
    param([switch]$AllowPrompt)
    # Web API requests must never solicit a console/CAC prompt.
    $token = Get-CascadeGitLabToken -AllowPrompt:$AllowPrompt
    if ([string]::IsNullOrWhiteSpace($token)) {throw 'Git.mil credential unavailable. Unlock ARC and check the mapped GitLab vault item.'}
    return @{
        'PRIVATE-TOKEN' = $token
        'Content-Type'  = 'application/json'
        'Accept'        = 'application/json'
    }
}

function Get-CascadeGitLabApiRoot {
    Import-CascadeGitLabEnv
    $url = $env:GITLAB_URL
    if ([string]::IsNullOrWhiteSpace($url)) { $url = 'https://web.git.mil' }
    return ($url.TrimEnd('/') + '/api/v4')
}

function Get-CascadeGitLabProjectIdEncoded {
    Import-CascadeGitLabEnv
    $id = $env:GITLAB_PROJECT_ID
    if ([string]::IsNullOrWhiteSpace($id)) {
        throw 'GITLAB_PROJECT_ID is not set. Add GITLAB_PROJECT_ID=16629 to config\\cascade.env or run [G] and pick project.'
    }
    # Already numeric
    if ($id -match '^\d+$') { return $id }
    # Path form: group/project -> URL-encoded
    if ($id -match '%2F') { return $id }
    return [uri]::EscapeDataString($id)
}

function Get-CascadeGitLabBase {
    $root = Get-CascadeGitLabApiRoot
    $projectId = Get-CascadeGitLabProjectIdEncoded
    return "$root/projects/$projectId"
}

function Test-CascadeGitLabConnection {
    <#
    .SYNOPSIS
      Verify web.git.mil API: token, project, optional issue list sample.
    #>
    [CmdletBinding()]
    param(
        [int]$SampleIssues = 5,
        [switch]$SkipIssueList
    )

    $ErrorActionPreference = 'Stop'
    Import-CascadeGitLabEnv

    $result = [ordered]@{
        Ok            = $false
        Url           = $null
        ApiRoot       = $null
        ProjectId     = $null
        User          = $null
        ProjectName   = $null
        ProjectPath   = $null
        OpenIssues    = $null
        SampleCount   = 0
        Sample        = @()
        Error         = $null
    }

    try {
        try {
            [Net.ServicePointManager]::SecurityProtocol =
                [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        } catch { }

        $api = Get-CascadeGitLabApiRoot
        $result.ApiRoot = $api
        $result.Url = $env:GITLAB_URL
        if (-not $result.Url) { $result.Url = 'https://web.git.mil' }

        Write-Host ''
        Write-Host '=== Cascade GitLab connection test ===' -ForegroundColor Cyan
        Write-Host ("  URL         : {0}" -f $result.Url)
        Write-Host ("  API         : {0}" -f $api)

        $headers = Get-CascadeGitLabHeaders
        Write-Host '  Token       : present' -ForegroundColor DarkGray

        # 1) Authenticated user
        Write-Host '  GET /user ...' -ForegroundColor DarkGray
        $userArr = Invoke-CascadeGitLabGetJson -Uri "$api/user" -Headers $headers -TimeoutSec 60; $user = $userArr | Select-Object -First 1
        $result.User = $user.username
        Write-Host ("  User        : {0} (id={1})" -f $user.username, $user.id) -ForegroundColor Green

        # 2) Project
        Import-CascadeGitLabEnv
        if ([string]::IsNullOrWhiteSpace($env:GITLAB_PROJECT_ID)) {
            Write-Host '  PROJECT_ID  : not set - listing your projects' -ForegroundColor Yellow
            $projs = Get-CascadeGitLabProjects -Headers $headers -ApiRoot $api -PerPage 50

            if ($null -eq $projs -or $projs.Count -eq 0) {
                Write-Host '  No projects returned for this token.' -ForegroundColor Red
                $result.Error = 'No projects found'
                return [pscustomobject]$result
            }

            Write-Host ''
            Write-Host '  #    id        path' -ForegroundColor DarkGray
            Write-Host '  ---  --------  ----------------------------------------------' -ForegroundColor DarkGray
            for ($i = 0; $i -lt $projs.Count; $i++) {
                $pr = $projs[$i]
                Write-Host ("  [{0,2}]  {1,-8}  {2}" -f ($i + 1), $pr.id, $pr.path_with_namespace)
            }
            Write-Host ''
            Write-Host ("  Enter list number (1-{0}), or type a numeric id / group/project path." -f $projs.Count) -ForegroundColor Gray
            Write-Host '  [S] Skip for now' -ForegroundColor Gray
            $pick = Read-Host '  Project'
            if ([string]::IsNullOrWhiteSpace($pick) -or $pick -eq 'S' -or $pick -eq 's') {
                $result.Error = 'GITLAB_PROJECT_ID not set'
                return [pscustomobject]$result
            }
            $pick = $pick.Trim()
            $chosenId = $null
            if ($pick -match '^\d+$') {
                $n = [int]$pick
                if ($n -ge 1 -and $n -le $projs.Count) {
                    $chosenId = [string]$projs[$n - 1].id
                    $chosenPath = [string]$projs[$n - 1].path_with_namespace
                    Write-Host ("  Selected    : {0} ({1})" -f $chosenPath, $chosenId) -ForegroundColor Green
                } else {
                    $chosenId = $pick
                    Write-Host ("  Using id    : {0}" -f $chosenId) -ForegroundColor Green
                }
            } else {
                $chosenId = $pick
                Write-Host ("  Using path  : {0}" -f $chosenId) -ForegroundColor Green
            }
            $env:GITLAB_PROJECT_ID = $chosenId
            Write-Host ("  PROJECT_ID  : {0}" -f $env:GITLAB_PROJECT_ID) -ForegroundColor Green

            $yn = Read-Host '  Save GITLAB_PROJECT_ID to config\cascade.env? (y/N)'
            if ($yn -eq 'y' -or $yn -eq 'Y') {
                try {
                    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
                    $envPath = Join-Path $repo 'config\cascade.env'
                    $line = 'GITLAB_PROJECT_ID={0}' -f $env:GITLAB_PROJECT_ID
                    $existing = ''
                    if (Test-Path -LiteralPath $envPath) {
                        $existing = Get-Content -LiteralPath $envPath -Raw
                        if ($existing -match '(?m)^GITLAB_PROJECT_ID=') {
                            $existing = [regex]::Replace($existing, '(?m)^GITLAB_PROJECT_ID=.*$', $line)
                        } else {
                            $existing = $existing.TrimEnd() + "`r`n" + $line + "`r`n"
                        }
                    } else {
                        $dir = Split-Path $envPath -Parent
                        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
                        $existing = "GITLAB_URL=https://web.git.mil`r`n$line`r`n"
                    }
                    Set-Content -Path $envPath -Value $existing -Encoding UTF8
                    Write-Host '[*] Saved GITLAB_PROJECT_ID' -ForegroundColor Green
                } catch {
                    Write-Host ("[!] Save failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
                }
            }
        }

        $result.ProjectId = $env:GITLAB_PROJECT_ID
        $base = Get-CascadeGitLabBase
        Write-Host ("  Project API : {0}" -f $base) -ForegroundColor DarkGray
        Write-Host '  GET project ...' -ForegroundColor DarkGray
        $projArr = Invoke-CascadeGitLabGetJson -Uri $base -Headers $headers -TimeoutSec 60; $proj = $projArr | Select-Object -First 1
        $result.ProjectName = $proj.name
        $result.ProjectPath = $proj.path_with_namespace
        Write-Host ("  Project     : {0}" -f $proj.path_with_namespace) -ForegroundColor Green
        Write-Host ("  Web URL     : {0}" -f $proj.web_url)

        if (-not $SkipIssueList) {
            Write-Host ("  GET issues (sample {0}) ..." -f $SampleIssues) -ForegroundColor DarkGray
            Import-CascadeGitLabEnv
            $sampleLabels = if ($env:GITLAB_LABELS) { $env:GITLAB_LABELS.Trim() } else { 'area::data-crucible' }
            $uri = "$base/issues?state=all&per_page=$SampleIssues&order_by=updated_at&sort=desc"
            if ($sampleLabels) { $uri += '&labels=' + [uri]::EscapeDataString($sampleLabels) }
            Write-Host ("  Filter      : labels={0} state=all" -f $sampleLabels) -ForegroundColor DarkGray
            $issues = @(Invoke-CascadeGitLabGetJson -Uri $uri -Headers $headers -TimeoutSec 60)
            $sampleRows = New-Object System.Collections.Generic.List[object]
            foreach ($iss in $issues) {
                if ($null -eq $iss) { continue }
                $iid = $null; $title = $null; $st = $null; $labs = ''
                try { $iid = $iss.iid } catch { }
                try { $title = $iss.title } catch { }
                try { $st = $iss.state } catch { }
                try {
                    if ($iss.labels -is [System.Array]) { $labs = ($iss.labels | ForEach-Object { "$_" }) -join ',' }
                    else { $labs = [string]$iss.labels }
                } catch { }
                if ($iid -is [System.Array]) { continue }
                if ($title -is [System.Array]) { continue }
                [void]$sampleRows.Add([pscustomobject]@{
                    iid    = [string]$iid
                    title  = [string]$title
                    state  = [string]$st
                    labels = $labs
                })
            }
            $result.SampleCount = $sampleRows.Count
            $result.Sample = @($sampleRows.ToArray())
            Write-Host ("  Open sample : {0} issue(s)" -f $sampleRows.Count) -ForegroundColor Green
            foreach ($i in $result.Sample) {
                Write-Host ("    #{0}  {1}" -f $i.iid, $i.title)
            }
        }

        $result.Ok = $true
        Write-Host ''
        Write-Host 'GitLab connection succeeded.' -ForegroundColor Green
        Write-Host 'Next: menu [5] Live Git sync  (or Get-CascadeIssuesFromGitLab).' -ForegroundColor DarkGray
    } catch {
        $result.Error = $_.Exception.Message
        Write-Host ''
        Write-Host ('GitLab connection FAILED: {0}' -f $result.Error) -ForegroundColor Red
        if ($_.ErrorDetails.Message) {
            Write-Host $_.ErrorDetails.Message -ForegroundColor DarkRed
        }
        Write-Host 'Check: GITLAB_URL, GITLAB_TOKEN (PAT with api/read_api), GITLAB_PROJECT_ID, network to web.git.mil' -ForegroundColor Yellow
        if ($result.Error -match '401') {
            Write-Host 'HTTP 401: wrong token for GitLab (e.g. advana-api saved as GitLab). Clearing session token.' -ForegroundColor Yellow
            Clear-CascadeGitLabTokenSession
            Write-Host 'Edit config\\cascade.env: set CASCADE_ARC_ITEM_GitLab=gitmil then retry [G].' -ForegroundColor Cyan
        }
    }

    return [pscustomobject]$result
}

function Get-CascadeIssuesFromFixture {
    [CmdletBinding()]
    param(
        [string]$Path = (Join-Path $PSScriptRoot '..\..\fixtures\work_items_fixture.json')
    )
    if (-not (Test-Path $Path)) {
        $alt = Join-Path (Get-Location) 'fixtures\work_items_fixture.json'
        if (Test-Path $alt) { $Path = $alt }
        else { throw "Fixture not found: $Path" }
    }
    $raw = Get-Content -Raw -Path $Path | ConvertFrom-Json
    return @($raw.issues)
}


function Invoke-CascadeGitLabApi {
    <#
    .SYNOPSIS
      REST call with 401 recovery hint (wrong ARC item for GitLab).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [hashtable]$Headers,
        [ValidateSet('Get','Post','Put')][string]$Method = 'Get',
        [string]$Body,
        [int]$TimeoutSec = 120
    )
    if (-not $Headers) { $Headers = Get-CascadeGitLabHeaders }
    try {
        if ($Method -eq 'Get') {
            return Invoke-RestMethod -Method Get -Uri $Uri -Headers $Headers -TimeoutSec $TimeoutSec
        }
        $bytes = $null
        if ($Body) { $bytes = [Text.Encoding]::UTF8.GetBytes($Body) }
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSec
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '401') {
            if (Get-Command Clear-CascadeGitLabTokenSession -ErrorAction SilentlyContinue) {
                Clear-CascadeGitLabTokenSession
            } else {
                Remove-Item Env:GITLAB_TOKEN -ErrorAction SilentlyContinue
            }
            throw "GitLab 401 Unauthorized. GITLAB_TOKEN was cleared. Map ARC item gitmil via [S] (not advana-api/genapi), then retry [G]."
        }
        throw
    }
}

function Get-CascadeIssuesFromGitLab {
    <#
    .SYNOPSIS
      List issues from GitLab API (requires network + token).
    .PARAMETER Labels
      Comma-separated label names (AND). Default: env GITLAB_LABELS or "area::data-crucible".
      Pass empty string "" for no label filter.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('opened', 'closed', 'all')]
        [string]$State = 'opened',
        [int]$PerPage = 100,
        [string]$Labels = $null
    )
    $base = Get-CascadeGitLabBase
    $headers = Get-CascadeGitLabHeaders

    if ($null -eq $Labels) {
        Import-CascadeGitLabEnv
        if (-not [string]::IsNullOrWhiteSpace($env:GITLAB_LABELS)) {
            $Labels = $env:GITLAB_LABELS.Trim()
        } else {
            $Labels = 'area::data-crucible'
        }
    }

    $page = 1
    $all = New-Object System.Collections.Generic.List[object]
    do {
        $uri = "$base/issues?state=$State&per_page=$PerPage&page=$page"
        if (-not [string]::IsNullOrWhiteSpace($Labels)) {
            $uri += '&labels=' + [uri]::EscapeDataString($Labels)
        }
        Write-Verbose $uri
        $batch = @(Invoke-CascadeGitLabGetJson -Uri $uri -Headers $headers -TimeoutSec 120)
        if ($batch.Count -eq 0) { break }
        foreach ($b in $batch) {
            if ($null -eq $b) { continue }
            if ($b -is [System.Array]) {
                foreach ($inner in $b) { if ($null -ne $inner) { [void]$all.Add($inner) } }
            } else {
                [void]$all.Add($b)
            }
        }
        $page++
    } while ($batch.Count -eq $PerPage)

    $labelMsg = if ([string]::IsNullOrWhiteSpace($Labels)) { 'any label' } else { "labels=$Labels" }
    Write-Host ("Fetched {0} issue(s) from GitLab (state={1}, {2})." -f $all.Count, $State, $labelMsg) -ForegroundColor Cyan
    # Prevent PS pipeline unwrap so callers see full Count
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($x in $all) { [void]$result.Add($x) }
    Write-Output -NoEnumerate $result.ToArray()
}


function Set-CascadeGitLabIssue {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][int]$Iid,
        [string]$Title,
        [string]$Description,
        [string[]]$Labels
    )
    $base = Get-CascadeGitLabBase
    $headers = Get-CascadeGitLabHeaders
    $body = @{}
    if ($PSBoundParameters.ContainsKey('Title')) { $body.title = $Title }
    if ($PSBoundParameters.ContainsKey('Description')) { $body.description = $Description }
    if ($PSBoundParameters.ContainsKey('Labels')) { $body.labels = ($Labels -join ',') }
    $json = $body | ConvertTo-Json -Depth 5
    if ($PSCmdlet.ShouldProcess("!$Iid", 'PUT update issue')) {
        return Invoke-RestMethod -Method Put -Uri "$base/issues/$Iid" -Headers $headers -Body $json -TimeoutSec 60
    }
}

function New-CascadeGitLabIssue {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$Description,
        [string[]]$Labels = @('cascade')
    )
    $base = Get-CascadeGitLabBase
    $headers = Get-CascadeGitLabHeaders
    $payload = @{
        title       = $Title
        description = $Description
        labels      = ($Labels -join ',')
    } | ConvertTo-Json
    if ($PSCmdlet.ShouldProcess($Title, 'POST create issue')) {
        return Invoke-RestMethod -Method Post -Uri "$base/issues" -Headers $headers -Body $payload -TimeoutSec 60
    }
}