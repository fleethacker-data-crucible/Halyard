#requires -Version 5.1
# ARC integration contract (service NAMES only - never log secret values)
# Canonical ARC Service names:
#   gitmil              -> GitLab PAT (web.git.mil)
#   genapi              -> GenAi / api.genai.mil
#   advana-api          -> Grok / Advana Databricks (matcher aliases only: advana, databricks)
#   github              -> GitHub token (Keystone)
#   TEST - RDS - Admin
#   PROD - RDS - Admin
#   aws_access_key_id / aws_secret_access_key / aws_session_token
# Prefer env: CASCADE_ARC_ITEM_*, ARC_RDS_*, ARC_AWS_*_ITEM, config ArcItems over hard-coded strings.
# Discover ARC via ARC_PATH or sibling Projects\arc; load pm.ps1 once; never re-dot-source after unlock.

# ARC bridge: load ARC as a global dynamic module (keeps $script: vault state + all helpers).
# Operator picks which vault item maps to each API. ASCII-only for PS 5.1.

$script:CascadeArcModule=$null
$script:CascadeArcRoot=$null
$script:CascadeArcLastErrorCode=''
$script:CascadeArcMissingModules=@()

function Find-CascadeArcRoot {
    [CmdletBinding()]
    param()
    # Order follows Keystone: explicitly configured ARC first, then sibling,
    # then user Projects. Ignore stale/nonexistent overrides instead of
    # preventing discovery of another approved local installation.
    $candidates=New-Object System.Collections.Generic.List[string]
    foreach($path in @($env:ARC_PATH,$env:ARC_ROOT,$env:ARC_HOME)){
        if(-not [string]::IsNullOrWhiteSpace($path)){
            [void]$candidates.Add($path.Trim().Trim('"').Trim("'"))
        }
    }
    $repo=$null
    try{$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent}catch{}
    if($repo){
        $settings=Join-Path $repo 'config/cascade.env'
        if(Test-Path -LiteralPath $settings){
            foreach($line in @(Get-Content -LiteralPath $settings)){
                if($line -match '^\s*ARC_PATH\s*=\s*(.+)$'){
                    [void]$candidates.Add($Matches[1].Trim().Trim('"').Trim("'"))
                }
            }
        }
        foreach($folder in @('arc','Arc')){
            [void]$candidates.Add((Join-Path (Split-Path $repo -Parent) $folder))
        }
    }
    if($env:USERPROFILE){
        [void]$candidates.Add((Join-Path $env:USERPROFILE 'Projects/arc'))
        [void]$candidates.Add((Join-Path $env:USERPROFILE 'arc'))
    }
    $seen=@{}
    foreach($candidate in $candidates){
        if(-not $candidate){continue}
        try{
            $full=[IO.Path]::GetFullPath($candidate)
            if($seen.ContainsKey($full.ToLowerInvariant())){continue}
            $seen[$full.ToLowerInvariant()]=$true
            if(Test-Path -LiteralPath (Join-Path $full 'pm.ps1') -PathType Leaf){
                return $full
            }
        }catch{}
    }
    return $null
}
function Get-CascadeArcRequiredFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ArcRoot)
    # ARC's pm.ps1 declares a $libOrder. This avoids hardcoding a particular
    # ARC release and catches missing files BEFORE importing partial state.
    $pm=Join-Path $ArcRoot 'pm.ps1'
    if(-not (Test-Path -LiteralPath $pm -PathType Leaf)){return @()}
    try{
        $source=[IO.File]::ReadAllText($pm)
        $found=[regex]::Match($source,'(?s)\$libOrder\s*=\s*@\((.*?)\)')
        if(-not $found.Success){return @()}
        $result=New-Object System.Collections.Generic.List[string]
        foreach($m in [regex]::Matches($found.Groups[1].Value,"'([A-Za-z0-9._-]+\.ps1)'")){
            [void]$result.Add(('lib/'+$m.Groups[1].Value))
        }
        return @($result.ToArray())
    }catch{return @()}
}
function Get-CascadeArcMissingFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ArcRoot)
    $missing=New-Object System.Collections.Generic.List[string]
    foreach($item in @(Get-CascadeArcRequiredFiles -ArcRoot $ArcRoot)){
        if(-not (Test-Path -LiteralPath (Join-Path $ArcRoot $item) -PathType Leaf)){
            [void]$missing.Add($item)
        }
    }
    return @($missing.ToArray())
}
function Test-CascadeArcHelpersAvailable {
    # ARC can expose *one* helper even when its module failed partway through.
    # Keystone requires the actual unlock + vault-reading APIs. Do the same.
    $unlock=[bool](Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue)
    $stark=[bool](Get-Command Get-PmStarkApiKey -ErrorAction SilentlyContinue)
    $vault=[bool](Get-Command Get-LiveVault -ErrorAction SilentlyContinue)
    $find=[bool](Get-Command Find-VaultRows -ErrorAction SilentlyContinue)
    $decrypt=[bool](Get-Command Unprotect-SecretText -ErrorAction SilentlyContinue)
    return ($unlock -and ($stark -or ($decrypt -and ($find -or $vault))))
}
function Import-CascadeArcSession {
    [CmdletBinding()]
    param([string]$ArcRoot,[switch]$Quiet)
    # A second import would reset ARC's in-memory CAC/TOTP session.
    if(Test-CascadeArcHelpersAvailable){
        $script:CascadeArcLastErrorCode=''
        if(-not $script:CascadeArcModule){
            $cmd=Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue
            if($cmd -and $cmd.Module){$script:CascadeArcModule=$cmd.Module}
        }
        return $true
    }
    if(Get-Module -Name CascadeArc -ErrorAction SilentlyContinue){
        $script:CascadeArcLastErrorCode='ARC_HELPERS_UNAVAILABLE'
        if(-not $Quiet){Write-Warning 'ARC was loaded incompletely. Restart Cascade and check ARC local logs.'}
        return $false
    }
    if(-not $ArcRoot){$ArcRoot=Find-CascadeArcRoot}
    if(-not $ArcRoot){
        $script:CascadeArcLastErrorCode='ARC_NOT_FOUND'
        if(-not $Quiet){Write-Warning 'ARC not found. Configure the ARC folder containing pm.ps1.'}
        return $false
    }
    $script:CascadeArcRoot=$ArcRoot
    $missing=@(Get-CascadeArcMissingFiles -ArcRoot $ArcRoot)
    $script:CascadeArcMissingModules=$missing
    if($missing.Count -gt 0){
        $script:CascadeArcLastErrorCode='ARC_MISSING_MODULES'
        if(-not $Quiet){Write-Warning 'ARC installation missing required libraries. Update the full ARC checkout.'}
        return $false
    }
    try{
        $pm=Join-Path $ArcRoot 'pm.ps1'
        # Use exactly one dynamic PowerShell module for vault session lifetime.
        $module=New-Module -Name CascadeArc -ScriptBlock {
            param([string]$PmPath)
            $ErrorActionPreference='Continue'
            . $PmPath
            Export-ModuleMember -Function * -Alias * -ErrorAction SilentlyContinue
        } -ArgumentList $pm
        Import-Module -ModuleInfo $module -Global -ErrorAction Stop -WarningAction SilentlyContinue
        $script:CascadeArcModule=$module
        if(-not (Test-CascadeArcHelpersAvailable)){
            $script:CascadeArcLastErrorCode='ARC_HELPERS_UNAVAILABLE'
            if(-not $Quiet){Write-Warning 'ARC loaded without working CAC/vault helpers. Check ARC local logs.'}
            return $false
        }
        $script:CascadeArcLastErrorCode=''
        if(-not $Quiet){Write-Host '[+] ARC loaded into Cascade PowerShell session.' -ForegroundColor Green}
        return $true
    }catch{
        $script:CascadeArcLastErrorCode=if($_.Exception.Message -match 'ARC load incomplete'){
            'ARC_LIBRARY_INITIALIZATION_FAILED'
        }else{'ARC_IMPORT_EXCEPTION'}
        # NO dot-source fallback: re-running ARC's loader can reset vault state
        # and partially exported helpers can appear healthy while unusable.
        if(-not $Quiet){Write-Warning ('ARC import failed ('+$script:CascadeArcLastErrorCode+'). Review ARC local diagnostics.')}
        return $false
    }
}
function Publish-CascadeArcFunctionsToGlobal {
    return (Import-CascadeArcSession -Quiet)
}
function Get-CascadeArcLoadDiagnostics {
    # Explicit module-to-host contract: $script: inside Cascade.psm1 is NOT
    # the host web script's $script: scope. Only return nonsecret fields.
    return [pscustomobject]@{
        ErrorCode=[string]$script:CascadeArcLastErrorCode
        Loaded=[bool](Test-CascadeArcHelpersAvailable)
        MissingLibraryCount=@($script:CascadeArcMissingModules).Count
        HasSelectedRoot=(-not [string]::IsNullOrWhiteSpace([string]$script:CascadeArcRoot))
    }
}
function Test-CascadeArcVaultUnlocked {
    # Noninteractive; inspect the same module that exports Ensure-VaultUnlocked.
    # This works with ARC's PmState hashtable and older VaultUnlocked flag.
    if(-not (Test-CascadeArcHelpersAvailable)){return $false}
    $module=$script:CascadeArcModule
    if(-not $module){
        $cmd=Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue
        if($cmd){$module=$cmd.Module}
    }
    if(-not $module){return $false}
    try{
        $ready=& $module {
            if($script:PmState -is [hashtable] -and $script:PmState.ContainsKey('VaultUnlocked')){
                return [bool]$script:PmState.VaultUnlocked
            }
            return [bool]$script:VaultUnlocked
        }
        return [bool]$ready
    }catch{return $false}
}

function Convert-CascadeArcRows {
    # Follow Keystone: ARC may wrap an ArrayList with unary commas, so flatten
    # every collection until only vault ROW objects remain.
    [CmdletBinding()]
    param($InputObject)
    $queue=New-Object System.Collections.Queue
    $rows=New-Object System.Collections.Generic.List[object]
    if($null -ne $InputObject){$queue.Enqueue($InputObject)}
    while($queue.Count -gt 0){
        $item=$queue.Dequeue()
        if($null -eq $item){continue}
        if($item -is [System.Collections.IList] -and -not ($item -is [string])){
            foreach($child in $item){if($null -ne $child){$queue.Enqueue($child)}}
        }elseif($item.PSObject.Properties.Name -contains 'Service'){
            [void]$rows.Add($item)
        }
    }
    return @($rows.ToArray())
}

function Get-CascadeArcSecretCatalog {
    [CmdletBinding()]
    param([switch]$TryLoadArc)

    if ($TryLoadArc) { $null = Import-CascadeArcSession }

    if (-not (Test-CascadeArcHelpersAvailable)) {
        Write-Host '[!] ARC helpers not available - cannot list secrets.' -ForegroundColor Yellow
        return @()
    }

    $rows = New-Object System.Collections.Generic.List[object]
    $rawItems = @()

    # 1) Live vault rows (correct ArrayList unwrap)
    if (Get-Command Get-LiveVault -ErrorAction SilentlyContinue) {
        try {
            $rawItems = Convert-CascadeArcRows -InputObject (Get-LiveVault)
        } catch {
            Write-Host ("[!] Get-LiveVault: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }

    # 2) Catalog helper
    if ($rawItems.Count -eq 0 -and (Get-Command Get-PmVaultNameCatalog -ErrorAction SilentlyContinue)) {
        try {
            $rawItems = Convert-CascadeArcRows -InputObject (Get-PmVaultNameCatalog)
        } catch {
            Write-Verbose "Get-PmVaultNameCatalog: $($_.Exception.Message)"
        }
    }

    # 3) Unlock then retry live
    if ($rawItems.Count -eq 0 -and (Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue)) {
        try {
            if (Ensure-VaultUnlocked) {
                if (Get-Command Get-LiveVault -ErrorAction SilentlyContinue) {
                    $rawItems = Convert-CascadeArcRows -InputObject (Get-LiveVault)
                }
            } else {
                Write-Host '[!] ARC vault locked - use menu [A] to unlock in this window.' -ForegroundColor Yellow
            }
        } catch {
            Write-Host ("[!] Unlock: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }

    foreach ($r in $rawItems) {
        if ($null -eq $r) { continue }
        # Nested list - expand again
        if ($r -is [System.Collections.IList] -and -not ($r -is [string]) -and -not ($r.PSObject.Properties.Name -contains 'Service')) {
            foreach ($inner in (Convert-CascadeArcRows -InputObject $r)) {
                $rawItems = @($rawItems)  # no-op; handle below by recursion skip
            }
            continue
        }

        $name = $null
        try {
            $svc = $r.Service
            if ($svc -is [System.Array] -and -not ($svc -is [string])) {
                # Should not happen after unwrap; skip blob
                continue
            }
            $name = [string]$svc
        } catch { continue }

        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        # Reject obvious join-blobs (many known tokens in one "name")
        if ($name -match '\sgenapi\s|\sgitmil\s|\sadvana-api\s' -and ($name.Split(' ').Count -gt 4)) {
            Write-Verbose "Skipping joined blob name"
            continue
        }

        $user = ''
        $typ = ''
        $has = $true
        try {
            $u = $r.User
            if ($u -is [System.Array] -and -not ($u -is [string])) { $user = [string]$u[0] }
            else { $user = [string]$u }
        } catch { }
        try {
            $ty = $r.Type
            if ($ty -is [System.Array] -and -not ($ty -is [string])) { $typ = [string]$ty[0] }
            else { $typ = [string]$ty }
        } catch { }
        try {
            $has = -not [string]::IsNullOrWhiteSpace([string]$r.Secret)
        } catch {
            try { if ($null -ne $r.HasSecret) { $has = [bool]$r.HasSecret } } catch { }
        }

        [void]$rows.Add([pscustomobject]@{
            Index     = 0
            Service   = $name.Trim()
            User      = $user
            Type      = $typ
            HasSecret = $has
        })
    }

    # Dedupe by service name
    $seen = @{}
    $unique = New-Object System.Collections.Generic.List[object]
    $i = 1
    foreach ($r in $rows) {
        $key = $r.Service.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $r.Index = $i
        [void]$unique.Add($r)
        $i++
    }

    if ($unique.Count -eq 0) {
        Write-Host '[!] No ARC secrets listed (vault locked or empty).' -ForegroundColor Yellow
    } else {
        Write-Host ("[*] {0} ARC item(s) listed" -f $unique.Count) -ForegroundColor DarkGray
    }
    return @($unique.ToArray())
}

function Convert-CascadeArcSecretRow {
    param($Row)
    if (-not $Row) { return $null }
    $raw = [string]$Row.Secret
    if ([string]::IsNullOrWhiteSpace($raw)) {
        try { $raw = [string]$Row.Password } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        try { $raw = [string]$Row.ApiKey } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $plain = $null
    if (Get-Command Unprotect-SecretText -ErrorAction SilentlyContinue) {
        try { $plain = Unprotect-SecretText -Protected $raw } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($plain)) {
        try {
            $ss = ConvertTo-SecureString $raw -ErrorAction Stop
            $BSTR = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
            try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($BSTR) }
            finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($BSTR) | Out-Null }
        } catch { $plain = $null } # Never forward ciphertext/DPAPI blobs as API tokens.
    }
    if ([string]::IsNullOrWhiteSpace($plain)) { return $null }
    return $plain.Trim()
}

function Get-CascadeArcSecretByName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ServiceName)

    if (Get-Command Get-PmStarkApiKey -ErrorAction SilentlyContinue) {
        try {
            $k = Get-PmStarkApiKey -VaultName $ServiceName
            if (-not [string]::IsNullOrWhiteSpace($k)) { return $k.Trim() }
        } catch { Write-Verbose "Get-PmStarkApiKey: $($_.Exception.Message)" }
    }
    if (Get-Command Find-VaultRows -ErrorAction SilentlyContinue) {
        try {
            # Materialize as array: a single ARC row may not expose .Count in PS 5.1.
            $hits = @(Convert-CascadeArcRows -InputObject (Find-VaultRows -Name $ServiceName))
            if ($hits.Count -gt 0) {
                $row = $hits | Sort-Object { ([string]$_.Secret).Length } -Descending | Select-Object -First 1
                return (Convert-CascadeArcSecretRow -Row $row)
            }
        } catch { Write-Verbose "Find-VaultRows: $($_.Exception.Message)" }
    }
    if (Get-Command Get-LiveVault -ErrorAction SilentlyContinue) {
        try {
            $all = @(Convert-CascadeArcRows -InputObject (Get-LiveVault))
            $row = $all | Where-Object { [string]$_.Service -eq $ServiceName } | Select-Object -First 1
            if ($row) { return (Convert-CascadeArcSecretRow -Row $row) }
        } catch { Write-Verbose "Get-LiveVault: $($_.Exception.Message)" }
    }
    return $null
}

function Save-CascadeArcPick {
    param([string]$RememberKey, [string]$ServiceName)
    try {
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $envPath = Join-Path $repo 'config\cascade.env'
        $mapKey = if ($RememberKey) { "CASCADE_ARC_ITEM_$RememberKey" } else { 'CASCADE_ARC_ITEM' }
        $line = '{0}={1}' -f $mapKey, $ServiceName
        $existing = ''
        if (Test-Path -LiteralPath $envPath) {
            $existing = Get-Content -LiteralPath $envPath -Raw
            if ($existing -match "(?m)^$mapKey=") {
                $existing = [regex]::Replace($existing, "(?m)^$mapKey=.*$", $line)
            } else {
                $existing = $existing.TrimEnd() + "`r`n" + $line + "`r`n"
            }
        } else {
            $dir = Split-Path $envPath -Parent
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
            $existing = "# cascade local - ARC item names (not secrets)`r`n$line`r`n"
        }
        Set-Content -Path $envPath -Value $existing -Encoding UTF8
        Write-Host ("[*] Saved {0}={1}" -f $mapKey, $ServiceName) -ForegroundColor Green
    } catch {
        Write-Host ("[!] Could not save pick: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    }
}

function Get-CascadeArcCanonicalNames {
    <#
    .SYNOPSIS
      Canonical ARC vault Service names (contract). Aliases are for matchers only.
    #>
    [CmdletBinding()]
    param()
    return [pscustomobject]@{
        GitLab   = 'gitmil'
        GenAi    = 'genapi'
        Grok     = 'advana-api'
        GitHub   = 'github'
        RdsTest  = 'TEST - RDS - Admin'
        RdsProd  = 'PROD - RDS - Admin'
        AwsAccessKey    = 'aws_access_key_id'
        AwsSecretKey    = 'aws_secret_access_key'
        AwsSessionToken = 'aws_session_token'
        # Matcher-only aliases (never preferred as primary mapping)
        GrokAliases   = @('advana', 'databricks')
        GitLabAliases = @()
        GenAiAliases  = @()
    }
}

function Resolve-CascadeArcMappedName {
    <#
    .SYNOPSIS
      Prefer env overrides for ARC item names. Never returns secret values.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GitLab','GenAi','Grok','GitHub','RdsTest','RdsProd','AwsAccessKey','AwsSecretKey','AwsSessionToken')]
        [string]$Role
    )
    $canon = Get-CascadeArcCanonicalNames
    # Env override keys (preferred)
    $envKeys = @{
        GitLab          = @('CASCADE_ARC_ITEM_GitLab', 'ARC_GITLAB_ITEM')
        GenAi           = @('CASCADE_ARC_ITEM_GenAi', 'CASCADE_ARC_ITEM_Llm', 'ARC_GENAI_ITEM')
        Grok            = @('CASCADE_ARC_ITEM_Grok', 'ARC_GROK_ITEM', 'ARC_ADVANA_ITEM')
        GitHub          = @('CASCADE_ARC_ITEM_GitHub', 'ARC_GITHUB_ITEM')
        RdsTest         = @('CASCADE_ARC_ITEM_RdsTest', 'ARC_RDS_TEST_ITEM', 'ARC_RDS_TEST')
        RdsProd         = @('CASCADE_ARC_ITEM_RdsProd', 'ARC_RDS_PROD_ITEM', 'ARC_RDS_PROD')
        AwsAccessKey    = @('CASCADE_ARC_ITEM_AwsAccessKey', 'ARC_AWS_ACCESS_KEY_ITEM')
        AwsSecretKey    = @('CASCADE_ARC_ITEM_AwsSecretKey', 'ARC_AWS_SECRET_KEY_ITEM')
        AwsSessionToken = @('CASCADE_ARC_ITEM_AwsSessionToken', 'ARC_AWS_SESSION_TOKEN_ITEM')
    }
    foreach ($k in $envKeys[$Role]) {
        $v = [Environment]::GetEnvironmentVariable($k)
        if (-not $v) {
            try { $v = (Get-Item -Path "Env:$k" -ErrorAction SilentlyContinue).Value } catch { $v = $null }
        }
        if (-not [string]::IsNullOrWhiteSpace($v)) {
            return $v.Trim()
        }
    }
    switch ($Role) {
        'GitLab'          { return [string]$canon.GitLab }
        'GenAi'           { return [string]$canon.GenAi }
        'Grok'            { return [string]$canon.Grok }
        'GitHub'          { return [string]$canon.GitHub }
        'RdsTest'         { return [string]$canon.RdsTest }
        'RdsProd'         { return [string]$canon.RdsProd }
        'AwsAccessKey'    { return [string]$canon.AwsAccessKey }
        'AwsSecretKey'    { return [string]$canon.AwsSecretKey }
        'AwsSessionToken' { return [string]$canon.AwsSessionToken }
        default           { return $null }
    }
}

function Test-CascadeArcServiceFitsPurpose {
    param(
        [Parameter(Position = 0)]
        [Alias('ServiceName')]
        [string]$Service = '',
        [Parameter(Position = 1)]
        [string]$Purpose = '',
        [string]$RememberKey = ''
    )
    # DoW git.mil work uses ARC item "gitmil".
    # Commercial non-CUI GitHub uses ARC item "github" - never for this GitLab purpose.
    if ([string]::IsNullOrWhiteSpace($Service)) { return $false }
    $s = $Service.ToLowerInvariant().Trim()
    $p = ("{0} {1}" -f $Purpose, $RememberKey).ToLowerInvariant()

    $isGitLab = [bool]($p -match 'gitlab|git\.mil|gitmil')
    $isGenAi  = [bool](($p -match 'genai|genapi') -and -not $isGitLab)
    $isGrok   = [bool](($p -match 'grok|databricks|advana') -and -not $isGitLab -and -not $isGenAi)
    $isGitHub = [bool](($p -match 'github|keystone') -and -not $isGitLab)
    $isRds    = [bool]($p -match 'rds|postgres admin')
    $isAws    = [bool]($p -match 'aws|amazon')

    if ($isGitLab) {
        if ($s -eq 'gitmil') { return $true }
        if ($s -like '*gitmil*') { return $true }
        if ($s -eq 'github') { return $false }
        if ($s -eq 'genapi') { return $false }
        if ($s -eq 'advana-api') { return $false }
        if ($s -like 'aws_*') { return $false }
        if ($s -like '*rds*') { return $false }
        if ($s -like '*gitlab*') { return $true }
        return $true
    }
    if ($isGenAi) {
        if ($s -eq 'genapi') { return $true }
        if ($s -eq 'gitmil' -or $s -eq 'github' -or $s -eq 'advana-api') { return $false }
        if ($s -like '*genapi*' -or $s -like '*genai*') { return $true }
        return $true
    }
    if ($isGrok) {
        if ($s -eq 'advana-api') { return $true }
        if ($s -eq 'gitmil' -or $s -eq 'genapi' -or $s -eq 'github') { return $false }
        if ($s -like '*advana*' -or $s -like '*databricks*' -or $s -like '*grok*') { return $true }
        return $true
    }
    if ($isGitHub) {
        if ($s -eq 'github') { return $true }
        if ($s -eq 'gitmil' -or $s -eq 'genapi') { return $false }
        return $true
    }
    if ($isRds) {
        if ($s -like '*rds*') { return $true }
        return $false
    }
    if ($isAws) {
        if ($s -like 'aws_*') { return $true }
        return $false
    }
    return $true
}

function Get-CascadeArcRecommendedService {
    param($Catalog, [string]$Purpose, [string]$RememberKey)
    if ($null -eq $Catalog) { return $null }
    $p = ("{0} {1}" -f $Purpose, $RememberKey).ToLowerInvariant()
    $canon = Get-CascadeArcCanonicalNames

    # Primary exact names (canonical only), then matcher aliases
    $exact = @()
    $aliases = @()
    if ($p -match 'gitlab|git\.mil|gitmil') {
        $exact = @([string]$canon.GitLab)
        $aliases = @($canon.GitLabAliases)
    } elseif ($p -match 'genai|genapi') {
        $exact = @([string]$canon.GenAi)
        $aliases = @($canon.GenAiAliases)
    } elseif ($p -match 'grok|databricks|advana') {
        $exact = @([string]$canon.Grok)
        $aliases = @($canon.GrokAliases)
    } elseif ($p -match 'github|keystone') {
        $exact = @([string]$canon.GitHub)
    } elseif ($p -match 'rds.*test|test.*rds') {
        $exact = @([string]$canon.RdsTest)
    } elseif ($p -match 'rds.*prod|prod.*rds') {
        $exact = @([string]$canon.RdsProd)
    } elseif ($p -match 'aws_access|access_key') {
        $exact = @([string]$canon.AwsAccessKey)
    } elseif ($p -match 'aws_secret|secret_access') {
        $exact = @([string]$canon.AwsSecretKey)
    } elseif ($p -match 'session_token') {
        $exact = @([string]$canon.AwsSessionToken)
    }

    $rows = @()
    foreach ($r in $Catalog) { if ($null -ne $r) { $rows += $r } }

    foreach ($n in $exact) {
        $nl = $n.ToLowerInvariant()
        foreach ($r in $rows) {
            $svc = ''
            try { $svc = [string]$r.Service } catch { continue }
            if ($svc.ToLowerInvariant() -eq $nl) { return $r }
        }
    }
    foreach ($n in $aliases) {
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        $nl = $n.ToLowerInvariant()
        foreach ($r in $rows) {
            $svc = ''
            try { $svc = [string]$r.Service } catch { continue }
            $sl = $svc.ToLowerInvariant()
            if ($sl -eq $nl -or $sl -match [regex]::Escape($nl)) { return $r }
        }
    }
    return $null
}

function Select-CascadeArcSecret {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Purpose,
        [string]$EnvTarget,
        [string]$RememberKey,
        [switch]$TryLoadArc,
        [switch]$AllowSkip
    )

    if ($RememberKey) {
        $memVar = "CASCADE_ARC_PICK_$RememberKey"
        $mapped = [Environment]::GetEnvironmentVariable($memVar)
        if ($mapped) {
            if (-not (Test-CascadeArcServiceFitsPurpose -ServiceName $mapped -Purpose $Purpose -RememberKey $RememberKey)) {
                Write-Host ("[!] Session pick '{0}' is wrong for {1} - choose again." -f $mapped, $Purpose) -ForegroundColor Yellow
                [Environment]::SetEnvironmentVariable($memVar, $null)
            } else {
                Write-Host ("[*] Using ARC item '{0}' for {1} (session choice)" -f $mapped, $Purpose) -ForegroundColor DarkGray
                $secret = Get-CascadeArcSecretByName -ServiceName $mapped
                if ($secret) {
                    if ($EnvTarget) { Set-Item -Path "Env:$EnvTarget" -Value $secret }
                    return $secret
                }
                Write-Host ("[!] Stored pick '{0}' failed to read - choose again." -f $mapped) -ForegroundColor Yellow
            }
        }
    }

    if ($TryLoadArc) { $null = Import-CascadeArcSession }

    if (-not (Test-CascadeArcHelpersAvailable)) {
        Write-Host '[!] ARC not available in this session.' -ForegroundColor Yellow
        Write-Host '    Use menu [A] to load/unlock ARC, then [S] to list and map secrets.' -ForegroundColor DarkGray
        return $null
    }

    $catalog = @(Get-CascadeArcSecretCatalog -TryLoadArc:$false)
    if ($catalog.Count -eq 0) {
        Write-Host '[!] No ARC secrets listed. Unlock vault ([A]) first.' -ForegroundColor Yellow
        return $null
    }

    $recommended = Get-CascadeArcRecommendedService -Catalog $catalog -Purpose $Purpose -RememberKey $RememberKey

    Write-Host ("=== Select ARC secret for: {0} ===" -f $Purpose) -ForegroundColor Cyan
    if ($recommended) {
        Write-Host ("  >>> RECOMMENDED: type {0}  or  {1}  <<<" -f $recommended.Index, $recommended.Service) -ForegroundColor Green
        Write-Host '  (Press Enter to accept recommended)' -ForegroundColor Green
    }
    Write-Host '  #   Service name              User                 Type' -ForegroundColor DarkGray
    Write-Host '  --  ------------------------- -------------------- ----------' -ForegroundColor DarkGray
    foreach ($row in $catalog) {
        $mark = ''
        if ($recommended -and $row.Service -eq $recommended.Service) { $mark = '  <== use this' }
        Write-Host ("  {0,2}  {1,-25} {2,-20} {3}{4}" -f $row.Index, $row.Service, $row.User, $row.Type, $mark)
    }
    Write-Host '  Enter number, or exact Service name.' -ForegroundColor Gray
    if ($AllowSkip) { Write-Host '  [S] Skip (paste token / use env instead)' -ForegroundColor DarkGray }

    $maxTries = 5
    for ($try = 0; $try -lt $maxTries; $try++) {
        $choice = Read-Host '  Choice'
        if ([string]::IsNullOrWhiteSpace($choice) -and $recommended) {
            $choice = [string]$recommended.Index
            Write-Host ("[*] Accepted recommended: {0}" -f $recommended.Service) -ForegroundColor Green
        }
        if ($AllowSkip -and ($choice -eq 'S' -or $choice -eq 's')) { return $null }

        $service = $null
        $n = 0
        if ([int]::TryParse($choice, [ref]$n)) {
            $hit = $catalog | Where-Object { $_.Index -eq $n } | Select-Object -First 1
            if ($hit) { $service = $hit.Service }
        }
        if (-not $service) {
            $hit = $catalog | Where-Object {
                $_.Service -eq $choice -or $_.Service.ToLowerInvariant() -eq $choice.ToLowerInvariant()
            } | Select-Object -First 1
            if ($hit) { $service = $hit.Service } else { $service = $choice }
        }

        $fits = $true
        try {
            $fits = Test-CascadeArcServiceFitsPurpose -Service $service -Purpose $Purpose -RememberKey $RememberKey
        } catch {
            # On error, allow exact canonical names rather than brick the picker
            $fits = ($service -match '^(?i)gitmil|genapi|advana-api|github$')
        }
        # Hard allow: gitmil is always valid for GitLab/git.mil purposes
        if ($service -match '^(?i)gitmil$' -and $Purpose -match '(?i)gitlab|git\.mil|gitmil') {
            $fits = $true
        }
        if (-not $fits) {
            Write-Host ("[!] '{0}' is the WRONG vault item for {1}." -f $service, $Purpose) -ForegroundColor Red
            if ($recommended) {
                Write-Host ("    Use #{0} {1}  (gitmil for GitLab, genapi for GenAi, advana-api for Grok)." -f $recommended.Index, $recommended.Service) -ForegroundColor Yellow
            }
            Write-Host '    Note: github is for commercial/non-CUI only; this work uses gitmil (web.git.mil).' -ForegroundColor DarkGray
            continue
        }

        Write-Host ("[*] Reading ARC item '{0}' ..." -f $service) -ForegroundColor DarkGray
        $secret = Get-CascadeArcSecretByName -ServiceName $service
        if ([string]::IsNullOrWhiteSpace($secret)) {
            Write-Host ("[!] Could not read secret for '{0}'." -f $service) -ForegroundColor Red
            continue
        }

        Write-Host ("[*] Loaded '{0}' for {1}" -f $service, $Purpose) -ForegroundColor Green
        if ($RememberKey) {
            [Environment]::SetEnvironmentVariable("CASCADE_ARC_PICK_$RememberKey", $service)
        }
        if ($EnvTarget) { Set-Item -Path "Env:$EnvTarget" -Value $secret }

        $yn = Read-Host '  Remember this ARC item name in config\cascade.env? (y/N)'
        if ($yn -eq 'y' -or $yn -eq 'Y') {
            Save-CascadeArcPick -RememberKey $RememberKey -ServiceName $service
        }
        return $secret
    }
    Write-Host '[!] Too many invalid picks.' -ForegroundColor Red
    return $null
}

function Get-CascadeTokenFromArc {
    [CmdletBinding()]
    param(
        [string[]]$VaultNames,
        [switch]$TryLoadArc,
        [string]$Purpose = 'API token',
        [string]$EnvTarget,
        [string]$RememberKey
    )

    if ($RememberKey) {
        $mapKey = "CASCADE_ARC_ITEM_$RememberKey"
        $mapped = [Environment]::GetEnvironmentVariable($mapKey)
        if (-not $mapped) {
            try { $mapped = (Get-Item -Path "Env:$mapKey" -ErrorAction SilentlyContinue).Value } catch { }
        }
        # Role-based env resolution (CASCADE_ARC_ITEM_* / ARC_*_ITEM) then canonical fallback name
        if (-not $mapped) {
            $role = $null
            switch -Regex ($RememberKey) {
                '^(GitLab)$' { $role = 'GitLab' }
                '^(GenAi|Llm)$' { $role = 'GenAi' }
                '^(Grok)$' { $role = 'Grok' }
                '^(GitHub)$' { $role = 'GitHub' }
                '^(RdsTest)$' { $role = 'RdsTest' }
                '^(RdsProd)$' { $role = 'RdsProd' }
            }
            if ($role) {
                try { $mapped = Resolve-CascadeArcMappedName -Role $role } catch { $mapped = $null }
            }
        }
        if ($mapped) {
            if ($TryLoadArc) { $null = Import-CascadeArcSession -Quiet }
            Write-Host ("[*] config maps {0} -> ARC item '{1}'" -f $RememberKey, $mapped) -ForegroundColor DarkGray
            $secret = Get-CascadeArcSecretByName -ServiceName $mapped
            if ($secret) {
                if ($EnvTarget) { Set-Item -Path "Env:$EnvTarget" -Value $secret }
                return $secret
            }
            Write-Host ("[!] Mapped item '{0}' not readable - pick from list." -f $mapped) -ForegroundColor Yellow
        }
    }

    if (-not [Environment]::UserInteractive) { return $null }

    return Select-CascadeArcSecret -Purpose $Purpose -EnvTarget $EnvTarget `
        -RememberKey $RememberKey -TryLoadArc:$TryLoadArc -AllowSkip
}
