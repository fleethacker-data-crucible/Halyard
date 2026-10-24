# Cascade GenAI client - Advana Databricks Grok + genai.mil (ARC genapi)
#requires -Version 5.1

$script:CascadeLlmDefaults = @{
    GrokUrl    = 'https://advana-jupiter.cloud.databricks.mil/serving-endpoints/databricks-grok-4-6/invocations'
    GrokModel  = 'system.ai.grok-4-6'
    GenAiUrl   = 'https://api.genai.mil/v1/chat/completions'
    # Must be a model your genapi token can call (from /v1/models list)
    GenAiModel = 'gemini-3.8-flash'
}

function Get-CascadeLlmConfig {
    [CmdletBinding()]
    param(
        [ValidateSet('Auto', 'Grok', 'GenAi')]
        [string]$Profile = 'Auto'
    )

    # Explicit provider profiles must never inherit the OTHER provider's URL/model.
    # Retain legacy LLM_URL/LLM_MODEL only for Auto profile.
    $url=$null; $model=$null
    if($Profile -eq 'GenAi'){$url=$env:GENAI_URL;$model=$env:GENAI_MODEL}
    elseif($Profile -eq 'Grok'){
        $url=$env:DATABRICKS_URL
        if(-not $url){$url=$env:ADVANA_URL}
        $model=$env:DATABRICKS_MODEL
        if(-not $model){$model=$env:ADVANA_MODEL}
    }else{$url=$env:LLM_URL;$model=$env:LLM_MODEL}
    $resolved = $Profile

    if ($Profile -eq 'Auto') {
        if ($url -match 'databricks\.mil|serving-endpoints') { $resolved = 'Grok' }
        elseif ($url -match 'genai\.mil') { $resolved = 'GenAi' }
        elseif (-not [string]::IsNullOrWhiteSpace($env:DATABRICKS_TOKEN) -and [string]::IsNullOrWhiteSpace($env:GENAI_TOKEN)) {
            $resolved = 'Grok'
        }
        elseif (-not [string]::IsNullOrWhiteSpace($env:GENAI_TOKEN) -and [string]::IsNullOrWhiteSpace($env:DATABRICKS_TOKEN)) {
            $resolved = 'GenAi'
        }
        elseif (-not [string]::IsNullOrWhiteSpace($env:DATABRICKS_TOKEN)) {
            $resolved = 'Grok'
        }
        elseif (-not [string]::IsNullOrWhiteSpace($env:GENAI_TOKEN)) {
            $resolved = 'GenAi'
        }
        else {
            # No profile-specific token yet; default GenAi if URL unknown
            $resolved = 'GenAi'
        }
    }

    if ([string]::IsNullOrWhiteSpace($url)) {
        if ($resolved -eq 'GenAi') { $url = $script:CascadeLlmDefaults.GenAiUrl }
        else { $url = $script:CascadeLlmDefaults.GrokUrl }
    }
    if ([string]::IsNullOrWhiteSpace($model)) {
        if ($resolved -eq 'GenAi') { $model = $script:CascadeLlmDefaults.GenAiModel }
        else { $model = $script:CascadeLlmDefaults.GrokModel }
    }

    return [pscustomobject]@{
        Profile = $resolved
        Url     = $url.TrimEnd('/')
        Model   = $model
    }
}

function Get-CascadeLlmToken {
    <#
    .SYNOPSIS
      Profile-isolated tokens:
        GenAi  -> GENAI_TOKEN      / CASCADE_ARC_ITEM_GenAi  (genapi)
        Grok   -> DATABRICKS_TOKEN / CASCADE_ARC_ITEM_Grok   (Advana Databricks PAT)
      Never falls back across profiles (no shared LLM_TOKEN for both).
    #>
    [CmdletBinding()]
    param(
        [string[]]$VaultNames,
        [switch]$AllowPrompt,
        [ValidateSet('Auto', 'Grok', 'GenAi')]
        [string]$ForProfile = 'Auto'
    )

    if ($ForProfile -eq 'Auto') {
        $cfg = Get-CascadeLlmConfig -Profile Auto
        $ForProfile = $cfg.Profile
    }

    if ($ForProfile -eq 'GenAi') {
        $envTarget = 'GENAI_TOKEN'
        $purpose   = 'GenAi / api.genai.mil'
        $remember  = 'GenAi'
        $hintArc   = 'genapi'
    } else {
        $envTarget = 'DATABRICKS_TOKEN'
        $purpose   = 'Grok / Databricks Advana (Jupiter)'
        $remember  = 'Grok'
        $hintArc   = 'advana-api'
    }

    # 1) Profile-specific env only
    $existing = [Environment]::GetEnvironmentVariable($envTarget)
    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        Write-Verbose ("Using env {0}" -f $envTarget)
        return $existing.Trim()
    }

    # The browser server must never invoke ARC token resolvers that may
    # trigger CAC/TOTP or a console prompt after it accepts HTTP requests.
    if($env:CASCADE_WEB_NONINTERACTIVE -eq '1'){return $null}

    # 2) Remembered ARC item name for THIS profile only
    $arcKey = 'CASCADE_ARC_ITEM_{0}' -f $remember
    $arcName = [Environment]::GetEnvironmentVariable($arcKey)
    if ([string]::IsNullOrWhiteSpace($arcName) -and $remember -eq 'GenAi') {
        # legacy key from early cascade.env
        $arcName = $env:CASCADE_ARC_ITEM_Llm
    }
    if ([string]::IsNullOrWhiteSpace($arcName) -and (Get-Command Resolve-CascadeArcMappedName -ErrorAction SilentlyContinue)) {
        try {
            $role = if ($remember -eq 'GenAi') { 'GenAi' } else { 'Grok' }
            $arcName = Resolve-CascadeArcMappedName -Role $role
        } catch { }
    }
    if (-not [string]::IsNullOrWhiteSpace($arcName) -and (Get-Command Get-CascadeArcSecretByName -ErrorAction SilentlyContinue)) {
        try {
            $null = Import-CascadeArcSession -ErrorAction SilentlyContinue
            $got = Get-CascadeArcSecretByName -ServiceName $arcName.Trim()
            # Returns plain string (not a row object)
            $plain = $null
            if ($got -is [string]) { $plain = $got.Trim() }
            elseif ($got -and $got.Secret) { $plain = [string]$got.Secret; $plain = $plain.Trim() }
            elseif ($got) { $plain = [string]$got; $plain = $plain.Trim() }
            if (-not [string]::IsNullOrWhiteSpace($plain)) {
                Set-Item -Path ("Env:{0}" -f $envTarget) -Value $plain
                Write-Host ("[*] Loaded {0} from ARC item '{1}' (len={2})" -f $envTarget, $arcName, $plain.Length) -ForegroundColor DarkGray
                return $plain
            }
        } catch {
            Write-Verbose "ARC remember load failed: $($_.Exception.Message)"
        }
    }

    # 3) Interactive ARC pick (stores into profile env + CASCADE_ARC_ITEM_*)
    if ($AllowPrompt -and (Get-Command Select-CascadeArcSecret -ErrorAction SilentlyContinue)) {
        Write-Host ("Token needed for {0} only (will not be used for the other LLM profile)." -f $purpose) -ForegroundColor DarkGray
        Write-Host ("  Prefer ARC item like '{0}'" -f $hintArc) -ForegroundColor DarkGray
        $fromArc = Select-CascadeArcSecret -Purpose $purpose -EnvTarget $envTarget `
            -RememberKey $remember -TryLoadArc -AllowSkip
        if ($fromArc) {
            # Ensure we did not also stamp a shared LLM_TOKEN
            if ($env:LLM_TOKEN -and $env:LLM_TOKEN -eq $fromArc) {
                Remove-Item Env:LLM_TOKEN -ErrorAction SilentlyContinue
            }
            return $fromArc
        }
    }

    # 4) Paste once into profile-specific env (also used after ARC [S] Skip)
    if ($AllowPrompt -and [Environment]::UserInteractive) {
        Write-Host ''
        Write-Host ("Paste token for {0} only (input hidden)." -f $purpose) -ForegroundColor Yellow
        Write-Host '  Use a CURRENT api.genai.mil key. Do not paste gitmil or advana tokens.' -ForegroundColor DarkGray
        $secure = Read-Host 'Token (input hidden)' -AsSecureString
        if ($null -eq $secure) { throw 'No token provided.' }
        $BSTR = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($BSTR)
        } finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($BSTR) | Out-Null
        }
        if ([string]::IsNullOrWhiteSpace($plain)) { throw 'No token provided.' }
        $plain = $plain.Trim()
        Set-Item -Path ("Env:{0}" -f $envTarget) -Value $plain
        # Do NOT set LLM_TOKEN (shared) - keeps profiles isolated
        Write-Host ("[*] {0} set for this process only." -f $envTarget) -ForegroundColor DarkGray
        $yn = Read-Host ("Save {0} to config\cascade.env? (y/N)" -f $envTarget)
        if ($yn -eq 'y' -or $yn -eq 'Y') {
            try {
                $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
                $envPath = Join-Path $repo 'config\cascade.env'
                $line = '{0}={1}' -f $envTarget, $plain
                $existingFile = ''
                if (Test-Path -LiteralPath $envPath) {
                    $existingFile = Get-Content -LiteralPath $envPath -Raw
                    if ($existingFile -match "(?m)^$envTarget=") {
                        $existingFile = [regex]::Replace($existingFile, "(?m)^$envTarget=.*$", $line)
                    } else {
                        $existingFile = $existingFile.TrimEnd() + "`r`n" + $line + "`r`n"
                    }
                } else {
                    $dir = Split-Path $envPath -Parent
                    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
                    $existingFile = "# cascade local secrets`r`n$line`r`n"
                }
                Set-Content -Path $envPath -Value $existingFile -Encoding UTF8
                Write-Host ("[*] Saved {0}" -f $envTarget) -ForegroundColor Green
            } catch {
                Write-Host ("[!] Save failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
            }
        }
        return $plain
    }

    throw ("No token for {0}. Set {1} or map ARC item via [S] (GenAi=genapi, Grok=advana-api)." -f $purpose, $envTarget)
}

function Get-CascadeLlmModels {
    <#
    .SYNOPSIS
      List model ids from the backend (genai.mil /v1/models). Same idea as ARC STARK.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('Auto', 'Grok', 'GenAi')]
        [string]$Profile = 'GenAi'
    )

    $cfg = Get-CascadeLlmConfig -Profile $Profile
    $token = Get-CascadeLlmToken -AllowPrompt -ForProfile $cfg.Profile

    try {
        if ($cfg.Profile -eq 'GenAi' -or $cfg.Url -match 'genai\.mil') {
            $modelsUri = ($cfg.Url -replace '/v1/chat/completions', '/v1/models')
            if ($modelsUri -notmatch '/v1/models') {
                $modelsUri = 'https://api.genai.mil/v1/models'
            }
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            } catch { }

            $list = Invoke-RestMethod -Uri $modelsUri -Method Get `
                -Headers @{ Authorization = ('Bearer ' + $token); Accept = 'application/json' } `
                -TimeoutSec 30

            $ids = New-Object System.Collections.Generic.List[string]

            # Normalize: data may be object[], single object, or odd shapes
            $rawItems = @()
            if ($null -eq $list) { }
            elseif ($list.data) { $rawItems = @($list.data) }
            elseif ($list.models) { $rawItems = @($list.models) }
            else { $rawItems = @($list) }

            foreach ($m in $rawItems) {
                if ($null -eq $m) { continue }

                if ($m -is [string]) {
                    foreach ($piece in ($m -split '[\s,;]+')) {
                        $t = $piece.Trim()
                        if ($t -and $t -notmatch '^(data|id|object)$') { [void]$ids.Add($t) }
                    }
                    continue
                }

                $idVal = $null
                try { $idVal = $m.id } catch { }
                if (-not $idVal) {
                    try { $idVal = $m.model } catch { }
                }
                if (-not $idVal) {
                    try { $idVal = $m.name } catch { }
                }

                if ($idVal -is [System.Array]) {
                    foreach ($piece in $idVal) {
                        $t = [string]$piece
                        if (-not [string]::IsNullOrWhiteSpace($t)) { [void]$ids.Add($t.Trim()) }
                    }
                } elseif ($idVal) {
                    $s = [string]$idVal
                    # Some gateways return one string with many ids
                    if ($s -match '\s' -and $s -match 'gemini') {
                        foreach ($piece in ($s -split '\s+')) {
                            if ($piece) { [void]$ids.Add($piece.Trim()) }
                        }
                    } else {
                        [void]$ids.Add($s.Trim())
                    }
                }
            }

            # De-dupe preserve order
            $seen = @{}
            $out = New-Object System.Collections.Generic.List[string]
            foreach ($x in $ids) {
                if (-not $seen.ContainsKey($x)) {
                    $seen[$x] = $true
                    [void]$out.Add($x)
                }
            }
            return @($out.ToArray())
        }

        Write-Verbose 'Grok profile: endpoint is fixed; returning configured model id only.'
        return @([string]$cfg.Model)
    } finally {
        $token = $null
    }
}

function Select-CascadeLlmModel {
    <#
    .SYNOPSIS
      Prefer env LLM_MODEL, else best chat model from live list, else default.
    .OUTPUTS
      System.String (always a single model id)
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('Auto', 'Grok', 'GenAi')]
        [string]$Profile = 'GenAi',
        [string]$Preferred
    )

    if (-not [string]::IsNullOrWhiteSpace($Preferred)) { return [string]$Preferred.Trim() }
    if (-not [string]::IsNullOrWhiteSpace($env:LLM_MODEL)) { return [string]$env:LLM_MODEL.Trim() }

    $cfg = Get-CascadeLlmConfig -Profile $Profile
    if ($cfg.Profile -ne 'GenAi') { return [string]$cfg.Model }

    try {
        $ids = @(Get-CascadeLlmModels -Profile GenAi | ForEach-Object { [string]$_ })
    } catch {
        Write-Verbose "Model list failed: $($_.Exception.Message)"
        return [string]$cfg.Model
    }

    if ($ids.Count -eq 0) { return [string]$cfg.Model }

    $prefer = @(
        'gemini-3.8-flash',
        'gemini-3.7-flash',
        'gemini-3.5-flash-withtools',
        'gemini-3.5-flash',
        'gemini-2.5-pro'
    )
    foreach ($pref in $prefer) {
        if ($ids -contains $pref) { return [string]$pref }
    }
    foreach ($id in $ids) {
        $s = [string]$id
        if ($s -match 'embedding') { continue }
        if ($s -match 'flash' -and $s -notmatch 'withtools') { return $s }
    }
    foreach ($id in $ids) {
        $s = [string]$id
        if ($s -match 'embedding') { continue }
        if ($s -match 'flash') { return $s }
    }
    foreach ($id in $ids) {
        $s = [string]$id
        if ($s -notmatch 'embedding') { return $s }
    }
    return [string]$ids[0]
}

function Get-CascadeChatTextFromResponse {
    param($Response)
    if ($null -eq $Response) { return $null }

    try {
        $choice0 = $Response.choices[0]
        if ($null -ne $choice0) {
            if ($null -ne $choice0.message) {
                $msg = $choice0.message
                $c = $null
                try { $c = $msg.content } catch { }

                if ($c -is [System.Array]) {
                    $parts = New-Object System.Collections.ArrayList
                    foreach ($part in $c) {
                        if ($null -eq $part) { continue }
                        if ($part -is [string]) { [void]$parts.Add($part); continue }
                        try {
                            if ($part.PSObject.Properties['text'] -and $part.text) {
                                [void]$parts.Add([string]$part.text)
                            } elseif ($part.PSObject.Properties['content'] -and $part.content) {
                                [void]$parts.Add([string]$part.content)
                            }
                        } catch { }
                    }
                    $joined = (($parts.ToArray()) -join '').Trim()
                    if ($joined) { return $joined }
                } elseif ($null -ne $c) {
                    $s = [string]$c
                    if (-not [string]::IsNullOrWhiteSpace($s) -and $s -ne 'null') { return $s }
                }

                # Some gov models put text on alternate properties
                foreach ($prop in @('reasoning_content', 'reasoning', 'text', 'output_text', 'refusal')) {
                    try {
                        if ($msg.PSObject.Properties[$prop] -and $msg.$prop) {
                            $s = [string]$msg.$prop
                            if (-not [string]::IsNullOrWhiteSpace($s) -and $s -ne 'null') { return $s }
                        }
                    } catch { }
                }
            }
            try {
                $t = [string]$choice0.text
                if (-not [string]::IsNullOrWhiteSpace($t) -and $t -ne 'null') { return $t }
            } catch { }
        }
    } catch { }

    foreach ($path in @(
            { param($r) $r.predictions[0] },
            { param($r) $r.message.content },
            { param($r) $r.content },
            { param($r) $r.output[0].content[0].text }
        )) {
        try {
            $t = [string](& $path $Response)
            if (-not [string]::IsNullOrWhiteSpace($t) -and $t -ne 'null') { return $t }
        } catch { }
    }
    return $null
}

function Invoke-CascadeChat {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Messages,
        [ValidateSet('Auto', 'Grok', 'GenAi')]
        [string]$Profile = 'Auto',
        [string]$Model,
        [int]$MaxTokens = 20000,
        [double]$Temperature = 0.2,
        [int]$TimeoutSec = 300,
        [switch]$Quiet,
        [switch]$NonInteractive
    )

    $cfg = Get-CascadeLlmConfig -Profile $Profile
    if ($Model) {
        $cfg = [pscustomobject]@{ Profile = $cfg.Profile; Url = $cfg.Url; Model = $Model }
    }

    $token = Get-CascadeLlmToken -AllowPrompt:(-not $NonInteractive) -ForProfile $cfg.Profile
    # Sanitize: strip quotes, Bearer prefix, whitespace/newlines (common ARC paste issues)
    if ($token) {
        $token = $token.Trim().Trim("'").Trim('"')
        if ($token -match '^(?i)Bearer\s+(.+)$') { $token = $Matches[1].Trim() }
        $token = ($token -replace '[\r\n]', '').Trim()
    }
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "GenAi/Grok token empty after ARC load. Update vault item or paste via [S] Skip."
    }
    if (-not $Quiet) {
        $mask = if ($token.Length -le 8) { '****' } else { $token.Substring(0,4) + '...' + $token.Substring($token.Length-4) }
        Write-Host ("    token_len={0} mask={1}" -f $token.Length, $mask) -ForegroundColor DarkGray
    }
    $uri = $cfg.Url

    $msgList = @()
    foreach ($m in $Messages) {
        if ($m -is [hashtable]) {
            $msgList += @{ role = [string]$m['role']; content = [string]$m['content'] }
        } else {
            $msgList += @{ role = [string]$m.role; content = [string]$m.content }
        }
    }

    # Body shapes differ slightly by backend
    if ($cfg.Profile -eq 'Grok' -or $uri -match 'serving-endpoints') {
        # Endpoint is model-specific; include model like playground curl.
        # Also send n=1 explicitly.
        $payload = [ordered]@{
            messages    = $msgList
            max_tokens  = $MaxTokens
            model       = $cfg.Model
            temperature = $Temperature
        }
    } else {
        $payload = [ordered]@{
            messages    = $msgList
            max_tokens  = $MaxTokens
            model       = $cfg.Model
            temperature = $Temperature
            stream      = $false
        }
    }

    $json = $payload | ConvertTo-Json -Depth 8
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)

    if (-not $Quiet) {
        
    # Transcript for review (sandbox-driven debugging)
    try {
        $txDir = Join-Path (Get-Location) 'output\llm_transcripts'
        if (-not (Test-Path $txDir)) { New-Item -ItemType Directory -Path $txDir -Force | Out-Null }
        $txStamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
        $txBase = Join-Path $txDir ("{0}_{1}" -f $txStamp, $cfg.Profile)
        $reqObj = [ordered]@{
            profile = $cfg.Profile
            model   = $cfg.Model
            uri     = $uri
            max_tokens = $MaxTokens
            temperature = $Temperature
            messages = $msgList
        }
        ($reqObj | ConvertTo-Json -Depth 12) | Set-Content ($txBase + '_request.json') -Encoding UTF8
        Write-Host ("    transcript request -> {0}_request.json" -f $txBase) -ForegroundColor DarkGray
    } catch {
        Write-Host ("    transcript save soft-fail: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
    }

Write-Host ('[*] Cascade LLM  profile={0}' -f $cfg.Profile) -ForegroundColor DarkCyan
        Write-Host ('    {0}' -f $uri) -ForegroundColor DarkGray
        Write-Host ('    model={0} messages={1} max_tokens={2}' -f $cfg.Model, $msgList.Count, $MaxTokens) -ForegroundColor DarkGray
    }

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }

    $headers = @{
        Authorization = ('Bearer ' + $token)
        Accept        = 'application/json'
    }

    try {
        $resp = Invoke-RestMethod -Uri $uri -Method Post `
            -Headers $headers `
            -Body $bytes `
            -ContentType 'application/json; charset=utf-8' `
            -TimeoutSec $TimeoutSec
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '401|unauthorized|Credential was not sent') {
            Write-Host 'HTTP 401 - token rejected by this endpoint.' -ForegroundColor Red
            if ($cfg.Profile -eq 'GenAi') {
                Write-Host '  Profile GenAi -> api.genai.mil needs a CURRENT genapi key (not gitmil, not advana).' -ForegroundColor Cyan
                Write-Host '  Fix: update ARC item genapi password, or [S] Skip and paste a fresh key.' -ForegroundColor Yellow
                Write-Host '  Clearing cached GENAI_TOKEN so next try reloads from ARC.' -ForegroundColor DarkGray
                try { Remove-Item Env:GENAI_TOKEN -ErrorAction SilentlyContinue } catch {}
            } else {
                Write-Host '  Profile Grok -> Databricks PAT from advana-api (not genapi).' -ForegroundColor Cyan
                try { Remove-Item Env:DATABRICKS_TOKEN -ErrorAction SilentlyContinue } catch {}
            }
        }
        if ($msg -match '403|Forbidden') {
            Write-Host 'HTTP 403 - token valid enough for some calls but model/endpoint denied.' -ForegroundColor Red
            Write-Host ('  Try LLM_MODEL with a model from /v1/models (current: {0})' -f $cfg.Model) -ForegroundColor Yellow
            Write-Host '  Example: set LLM_MODEL=gemini-3.8-flash in config\cascade.env' -ForegroundColor Yellow
        }
        throw
    } finally {
        $token = $null
    }

    $text = Get-CascadeChatTextFromResponse -Response $resp
    try {
        if ($txBase) {
            $text | Set-Content ($txBase + '_response.txt') -Encoding UTF8
            $respObj = [ordered]@{
                profile = $cfg.Profile
                model = $cfg.Model
                content_length = $(if ($text) { $text.Length } else { 0 })
                content_preview = $(if ($text -and $text.Length -gt 500) { $text.Substring(0, 500) + '...' } else { $text })
            }
            ($respObj | ConvertTo-Json -Depth 4) | Set-Content ($txBase + '_response_meta.json') -Encoding UTF8
            Write-Host ("    transcript response -> {0}_response.txt" -f $txBase) -ForegroundColor DarkGray
        }
    } catch { }
    return [pscustomobject]@{
        Content = $text
        Raw     = $resp
        Profile = $cfg.Profile
        Model   = $cfg.Model
        Url     = $uri
    }
}

function Test-CascadeLlmConnection {
    [CmdletBinding()]
    param(
        [ValidateSet('Auto', 'Grok', 'GenAi')]
        [string]$Profile = 'Auto'
    )

    $cfg = Get-CascadeLlmConfig -Profile $Profile
    Write-Host ('Testing profile={0} url={1}' -f $cfg.Profile, $cfg.Url) -ForegroundColor Cyan

    $picked = $null
    if ($cfg.Profile -eq 'GenAi') {
        try {
            $ids = @(Get-CascadeLlmModels -Profile GenAi)
            Write-Host ('Models endpoint OK ({0} ids)' -f $ids.Count) -ForegroundColor Green
            foreach ($id in $ids | Select-Object -First 15) {
                Write-Host ('  {0}' -f $id)
            }
            $picked = Select-CascadeLlmModel -Profile GenAi
            Write-Host ('Using model: {0}' -f $picked) -ForegroundColor Cyan
        } catch {
            Write-Host ('Models list failed: {0}' -f $_.Exception.Message) -ForegroundColor Yellow
            $picked = $cfg.Model
        }
    } else {
        $picked = $cfg.Model
        Write-Host ('Using model: {0}' -f $picked) -ForegroundColor Cyan
    }

    $picked = [string]$picked
    $r = Invoke-CascadeChat -Profile $Profile -Model $picked -MaxTokens 200 -Messages @(
        @{ role = 'user'; content = 'Say hello in one short sentence.' }
    )

    Write-Host ''
    if ([string]::IsNullOrWhiteSpace([string]$r.Content)) {
        Write-Host 'AUTH OK but empty assistant content.' -ForegroundColor Yellow
        try {
            Write-Host ('  finish_reason={0}' -f $r.Raw.choices[0].finish_reason) -ForegroundColor DarkGray
        } catch { }
        try {
            Write-Host ('  usage={0}' -f ($r.Raw.usage | ConvertTo-Json -Compress)) -ForegroundColor DarkGray
        } catch { }
        try {
            $msgObj = $r.Raw.choices[0].message
            Write-Host '  message properties:' -ForegroundColor DarkGray
            $msgObj.PSObject.Properties | ForEach-Object {
                Write-Host ('    {0} = {1}' -f $_.Name, (($_.Value | Out-String).Trim() -replace '\s+', ' ').Substring(0, [Math]::Min(80, (($_.Value | Out-String).Trim().Length))))
            }
        } catch {
            Write-Host ('  (could not dump message: {0})' -f $_.Exception.Message) -ForegroundColor DarkGray
        }
        Write-Host 'Endpoint accepted the token. Content field empty - may need model-specific response mapping.' -ForegroundColor Yellow
    } else {
        Write-Host 'Reply:' -ForegroundColor Green
        Write-Host $r.Content
        Write-Host ''
        Write-Host 'LLM connection succeeded.' -ForegroundColor Green
    }
    return $r
}

function Invoke-CascadeSuggestParents {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PlanCsv,
        [Parameter(Mandatory)][string]$FeatureCatalog,
        [ValidateSet('Auto', 'Grok', 'GenAi')]
        [string]$Profile = 'Auto',
        [string]$OutPath,
        [int]$MaxItems = 8
    )

    if (-not (Test-Path $PlanCsv)) { throw "Plan not found: $PlanCsv" }
    $rows = @(Import-Csv $PlanCsv | Where-Object {
            [string]::IsNullOrWhiteSpace($_.ProposedParent) -and $_.ProposedType -match 'STORY|TASK'
        } | Select-Object -First $MaxItems)

    if ($rows.Count -eq 0) {
        Write-Host 'No unparented STORY/TASK rows to suggest.'
        return @()
    }

    $issueLines = ($rows | ForEach-Object {
            'IID={0} | Type={1} | Title={2} | Milestone={3} | Labels={4}' -f `
                $_.Iid, $_.ProposedType, $_.ProposedTitle, $_.Milestone, $_.Labels
        }) -join "`n"

    $system = 'You are Cascade hierarchy assistant for SMPG Git work items. ' +
    'Assign each issue to exactly one Feature id from the ALLOWED FEATURE CATALOG. ' +
    'When none fit, use parent id NEW_FEATURE and propose a short feature title. ' +
    'Return ONLY a JSON array of objects with keys iid, parent, note. ' +
    'No markdown fences. No secrets.'

    $user = "ALLOWED FEATURE CATALOG:`n$FeatureCatalog`n`nISSUES:`n$issueLines"

    $r = Invoke-CascadeChat -Profile $Profile -MaxTokens 20000 -TimeoutSec 300 -Messages @(
        @{ role = 'system'; content = $system }
        @{ role = 'user'; content = $user }
    )

    $text = [string]$r.Content
    $text = $text -replace '^\s*```(?:json)?\s*', '' -replace '\s*```\s*$', ''

    if ([string]::IsNullOrWhiteSpace($text)) {
        Write-Warning 'Model returned empty content; cannot suggest parents.'
        return $r
    }

    $suggestions = $null
    try {
        $suggestions = $text | ConvertFrom-Json
    } catch {
        Write-Warning 'Could not parse JSON from model; raw content saved.'
        if (-not $OutPath) {
            $OutPath = Join-Path (Split-Path $PlanCsv -Parent) 'parent_suggestions_raw.txt'
        }
        $text | Set-Content -Path $OutPath -Encoding UTF8
        return $r
    }

    if (-not $OutPath) {
        $OutPath = Join-Path (Split-Path $PlanCsv -Parent) 'parent_suggestions.json'
    }
    $suggestions | ConvertTo-Json -Depth 6 | Set-Content -Path $OutPath -Encoding UTF8
    Write-Host "Wrote $OutPath"
    return $suggestions
}


function Extract-CascadeJsonPayload {
    <#
    .SYNOPSIS
      Pull JSON from messy Gemini/LLM text using markers and fallbacks.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { throw 'Empty LLM text' }

    $t = $Text.Trim()
    $markers = @(
        '***<<< JSON PAYLOAD >>>***',
        '<<< JSON PAYLOAD >>>',
        '***JSON PAYLOAD***',
        '```json',
        '```'
    )

    # 1) Explicit Cascade markers (preferred)
    foreach ($m in @('***<<< JSON PAYLOAD >>>***', '<<< JSON PAYLOAD >>>', '***JSON_PAYLOAD***', '---JSON---')) {
        $idx = $t.IndexOf($m, [StringComparison]::OrdinalIgnoreCase)
        if ($idx -ge 0) {
            $after = $t.Substring($idx + $m.Length)
            # end marker optional
            foreach ($end in @('***<<< END JSON >>>***', '<<< END JSON >>>', '***END_JSON***', '---END JSON---')) {
                $e = $after.IndexOf($end, [StringComparison]::OrdinalIgnoreCase)
                if ($e -ge 0) { $after = $after.Substring(0, $e); break }
            }
            $t = $after.Trim()
            break
        }
    }

    # 2) Markdown fence
    if ($t -match '(?is)```(?:json)?\s*(\{[\s\S]*\})\s*```') {
        $t = $Matches[1].Trim()
    }

    # 3) First { to last } (object) or [ to ] (array wrap)
    $start = $t.IndexOf('{')
    $end = $t.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) {
        $start = $t.IndexOf('[')
        $end = $t.LastIndexOf(']')
        if ($start -lt 0 -or $end -le $start) {
            throw 'No JSON object/array found in LLM text'
        }
        # wrap array as object for cluster tools if needed
        $t = $t.Substring($start, $end - $start + 1)
        return $t
    }
    $t = $t.Substring($start, $end - $start + 1)

    # 4) Sanitize common Gemini damage
    $t = $t -replace '[\u0000-\u0008\u000B\u000C\u000E-\u001F]', ''
    # smart quotes
    $t = $t.Replace([char]0x201C, '"').Replace([char]0x201D, '"').Replace([char]0x2018, "'").Replace([char]0x2019, "'")
    # trailing commas before } or ]
    $t = [regex]::Replace($t, ',\s*([\}\]])', '$1')

    return $t
}

function ConvertFrom-CascadeLlmJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text)

    $json = Extract-CascadeJsonPayload -Text $Text
    # Accept known Cascade shapes: cluster, restructure, OR chat-intake
    $isCluster = ($json -match '"clusters"') -or ($json -match '"proposals"') -or ($json -match '"analysis"')
    $isIntake  = ($json -match '"type"') -and (($json -match '"title"') -or ($json -match '"acceptance_criteria"') -or ($json -match '"user_story"') -or ($json -match '"body_markdown"') -or ($json -match '"needs_human_refinement"') -or ($json -match '"confidence"'))
    if ($json.Length -lt 10 -or (-not $isCluster -and -not $isIntake)) {
        $repoOut = Join-Path (Get-Location) 'output'
        if (-not (Test-Path $repoOut)) { New-Item -ItemType Directory -Path $repoOut -Force | Out-Null }
        $Text | Set-Content (Join-Path $repoOut 'last_llm_json_error.txt') -Encoding UTF8
        $json | Set-Content (Join-Path $repoOut 'last_llm_json_extracted.txt') -Encoding UTF8
        $head = $json.Substring(0, [Math]::Min(200, $json.Length))
        if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
            $null = Write-CascadeErrorLog -Operation 'ConvertFrom-CascadeLlmJson' -Message 'LLM JSON shape rejected or empty' -Detail $Text.Substring(0, [Math]::Min(4000, $Text.Length)) -Context @{
                extracted_head = $head
                extracted_len  = $json.Length
                raw_path       = (Join-Path $repoOut 'last_llm_json_error.txt')
            }
        }
        throw ("LLM did not return cascade JSON (got {0} chars starting: {1}). See output/cascade_last_error.txt" -f $json.Length, $json.Substring(0, [Math]::Min(80, $json.Length)))
    }
    try {
        return ($json | ConvertFrom-Json)
    } catch {
        $repo = $null
        try { $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent } catch {}
        if (-not $repo) { $repo = Get-Location }
        $out = Join-Path $repo 'output'
        if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out -Force | Out-Null }
        $Text | Set-Content (Join-Path $out 'last_llm_json_error.txt') -Encoding UTF8
        $json | Set-Content (Join-Path $out 'last_llm_json_extracted.txt') -Encoding UTF8
        if (Get-Command Write-CascadeErrorLog -ErrorAction SilentlyContinue) {
            $null = Write-CascadeErrorLog -Operation 'ConvertFrom-CascadeLlmJson' -Message 'Invalid JSON from LLM' -Exception $_.Exception.Message -Detail $Text.Substring(0, [Math]::Min(4000, $Text.Length)) -Context @{
                extracted_head = $json.Substring(0, [Math]::Min(200, $json.Length))
            }
        }
        throw ("Invalid JSON from LLM: {0}`nSee output/cascade_last_error.txt" -f $_.Exception.Message)
    }
}



function ConvertFrom-CascadeClusterMarkdown {
    <#
    .SYNOPSIS
      Fallback when Gemini returns markdown tables instead of JSON.
      Parses | IID | Title | `FEATURE-x` | rows and EPIC headers.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text)

    $clusters = @{}
    $currentEpic = 'EPIC-unknown'
    $currentFeature = 'FEATURE-unknown'
    $order = New-Object System.Collections.Generic.List[string]

    foreach ($line in ($Text -split "`n")) {
        $l = $line.Trim()
        # Epic headers: `EPIC-gold` or EPIC-gold
        if ($l -match '`?(EPIC-[a-z0-9\-]+)`?') {
            $currentEpic = $Matches[1]
        }
        # Feature in prose
        if ($l -match '`?(FEATURE-[A-Za-z0-9\-]+)`?') {
            $currentFeature = $Matches[1]
        }
        # Story row must look like: | 396 | Some Title | `FEATURE-x` |
        # Skip summary count rows (| 24 |) and header rows
        if ($l -match '^\|\s*(\d{2,5})\s*\|\s*([^|]+)\|') {
            $iid = [int]$Matches[1]
            $titleBit = $Matches[2].Trim()
            # real story iids are typically >= 100; skip small summary counts
            if ($iid -lt 100) { continue }
            if ($titleBit -match '^(?i)IID|Title|---|Epic') { continue }
            $feat = $currentFeature
            if ($l -match 'FEATURE-[A-Za-z0-9\-]+') { $feat = $Matches[0] }
            elseif ($titleBit -match 'FEATURE-') { continue } # not a story line
            $epic = $currentEpic
            if ($l -match 'EPIC-[a-z0-9\-]+') { $epic = $Matches[0] }
            $key = "$epic|$feat"
            if (-not $clusters.ContainsKey($key)) {
                $clusters[$key] = @{
                    epic_id = $epic
                    feature_id = $feat
                    story_iids = New-Object System.Collections.Generic.List[int]
                }
                $order.Add($key) | Out-Null
            }
            if (-not $clusters[$key].story_iids.Contains($iid)) {
                $clusters[$key].story_iids.Add($iid) | Out-Null
            }
        }
    }

    $list = @()
    $n = 0
    foreach ($key in $order) {
        $n++
        $c = $clusters[$key]
        $list += [pscustomobject]@{
            cluster_id     = "C$n"
            feature_id     = $c.feature_id
            feature_title  = $c.feature_id
            epic_id        = $c.epic_id
            epic_title     = $c.epic_id
            story_iids     = @($c.story_iids)
            rationale      = 'Parsed from Gemini markdown cluster report'
            effort         = 'Medium'
        }
    }

    if ($list.Count -eq 0) { throw 'Markdown fallback found zero story rows' }

    return [pscustomobject]@{
        analysis = 'Recovered clusters from Gemini markdown tables (JSON markers missing).'
        clusters = $list
        source   = 'markdown_fallback'
    }
}
