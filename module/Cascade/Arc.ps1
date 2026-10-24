#requires -Version 5.1
# ARC vault bridge. ARC (pm.ps1) owns CAC + TOTP; Cascade only loads it once, asks it to unlock in the
# visible console, and reads named items into process environment variables. Secret values are never
# logged, written to disk or passed to the browser. ASCII only for Windows PowerShell 5.1.

$script:CascadeArcModule = $null
$script:CascadeArcRoot = $null

function Find-CascadeArcRoot {
    [CmdletBinding()]
    param([string]$CascadeRoot)
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($p in @($env:ARC_PATH, $env:ARC_ROOT, $env:ARC_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($p)) { [void]$candidates.Add($p.Trim().Trim('"').Trim("'")) }
    }
    if ($CascadeRoot) {
        $parent = Split-Path $CascadeRoot -Parent
        foreach ($name in @('arc', 'Arc', 'ARC')) { [void]$candidates.Add((Join-Path $parent $name)) }
    }
    if ($env:USERPROFILE) {
        [void]$candidates.Add((Join-Path $env:USERPROFILE 'Projects\arc'))
        [void]$candidates.Add((Join-Path $env:USERPROFILE 'arc'))
    }
    $seen = @{}
    foreach ($c in $candidates) {
        try {
            $full = [IO.Path]::GetFullPath($c)
            $k = $full.ToLowerInvariant()
            if ($seen.ContainsKey($k)) { continue }
            $seen[$k] = $true
            if (Test-Path -LiteralPath (Join-Path $full 'pm.ps1') -PathType Leaf) { return $full }
        } catch { }
    }
    return $null
}

function Test-CascadeArcHelpersAvailable {
    $unlock = [bool](Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue)
    $stark = [bool](Get-Command Get-PmStarkApiKey -ErrorAction SilentlyContinue)
    $vault = [bool](Get-Command Get-LiveVault -ErrorAction SilentlyContinue)
    $find = [bool](Get-Command Find-VaultRows -ErrorAction SilentlyContinue)
    $decrypt = [bool](Get-Command Unprotect-SecretText -ErrorAction SilentlyContinue)
    return ($unlock -and ($stark -or ($decrypt -and ($find -or $vault))))
}

function Import-CascadeArcSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ArcRoot)
    # Loading pm.ps1 a second time resets ARC's in-memory vault session, so load exactly once.
    if (Test-CascadeArcHelpersAvailable) { return $true }
    if (Get-Module -Name CascadeArc -ErrorAction SilentlyContinue) { return $false }
    $pm = Join-Path $ArcRoot 'pm.ps1'
    try {
        $module = New-Module -Name CascadeArc -ScriptBlock {
            param([string]$PmPath)
            $ErrorActionPreference = 'Continue'
            . $PmPath
            Export-ModuleMember -Function * -Alias * -ErrorAction SilentlyContinue
        } -ArgumentList $pm
        Import-Module -ModuleInfo $module -Global -ErrorAction Stop -WarningAction SilentlyContinue
        $script:CascadeArcModule = $module
        $script:CascadeArcRoot = $ArcRoot
        return (Test-CascadeArcHelpersAvailable)
    } catch {
        Write-Warning ('ARC import failed: ' + $_.Exception.GetType().Name + '. Open ARC directly and review its logs.')
        return $false
    }
}

function Test-CascadeArcVaultUnlocked {
    if (-not (Test-CascadeArcHelpersAvailable)) { return $false }
    $module = $script:CascadeArcModule
    if (-not $module) {
        $cmd = Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue
        if ($cmd) { $module = $cmd.Module }
    }
    if (-not $module) { return $false }
    try {
        return [bool](& $module {
            if ($script:PmState -is [hashtable] -and $script:PmState.ContainsKey('VaultUnlocked')) { return [bool]$script:PmState.VaultUnlocked }
            return [bool]$script:VaultUnlocked
        })
    } catch { return $false }
}

function ConvertTo-CascadeArcRows {
    param($InputObject)
    # ARC may wrap ArrayLists with unary commas; flatten until only row objects remain.
    $queue = New-Object System.Collections.Queue
    $rows = New-Object System.Collections.Generic.List[object]
    if ($null -ne $InputObject) { $queue.Enqueue($InputObject) }
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        if ($null -eq $item) { continue }
        if ($item -is [System.Collections.IList] -and -not ($item -is [string])) {
            foreach ($child in $item) { if ($null -ne $child) { $queue.Enqueue($child) } }
        } elseif ($item.PSObject.Properties.Name -contains 'Service') {
            [void]$rows.Add($item)
        }
    }
    return @($rows.ToArray())
}

function ConvertFrom-CascadeArcRow {
    param($Row)
    if (-not $Row) { return $null }
    $raw = $null
    foreach ($prop in @('Secret', 'Password', 'ApiKey')) {
        try { if (-not [string]::IsNullOrWhiteSpace([string]$Row.$prop)) { $raw = [string]$Row.$prop; break } } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $plain = $null
    if (Get-Command Unprotect-SecretText -ErrorAction SilentlyContinue) {
        try { $plain = Unprotect-SecretText -Protected $raw } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($plain)) {
        try {
            $ss = ConvertTo-SecureString $raw -ErrorAction Stop
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
            try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
            finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) | Out-Null }
        } catch { $plain = $null } # never forward ciphertext as a token
    }
    if ([string]::IsNullOrWhiteSpace($plain)) { return $null }
    return $plain.Trim()
}

function Get-CascadeArcSecret {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    if (Get-Command Get-PmStarkApiKey -ErrorAction SilentlyContinue) {
        try {
            $k = Get-PmStarkApiKey -VaultName $Name
            if (-not [string]::IsNullOrWhiteSpace($k)) { return $k.Trim() }
        } catch { }
    }
    if (Get-Command Find-VaultRows -ErrorAction SilentlyContinue) {
        try {
            $hits = @(ConvertTo-CascadeArcRows -InputObject (Find-VaultRows -Name $Name))
            if ($hits.Count -gt 0) {
                $row = $hits | Sort-Object { ([string]$_.Secret).Length } -Descending | Select-Object -First 1
                return (ConvertFrom-CascadeArcRow -Row $row)
            }
        } catch { }
    }
    if (Get-Command Get-LiveVault -ErrorAction SilentlyContinue) {
        try {
            $row = @(ConvertTo-CascadeArcRows -InputObject (Get-LiveVault)) | Where-Object { [string]$_.Service -eq $Name } | Select-Object -First 1
            if ($row) { return (ConvertFrom-CascadeArcRow -Row $row) }
        } catch { }
    }
    return $null
}

function Connect-CascadeArc {
    <#
    .SYNOPSIS
      Loads ARC once, completes CAC + TOTP in this console, and copies named vault items into process
      environment variables (GITLAB_TOKEN, LLM_TOKEN, DATABRICKS_TOKEN, GITHUB_TOKEN). The vault item names
      come from CASCADE_ARC_ITEM_GitLab / _Llm / _Databricks / _GitHub in config\cascade.env; unset = skipped.
    .OUTPUTS
      A summary object with Unlocked, ArcRoot, Loaded (names of variables set) and Error.
    #>
    [CmdletBinding()]
    param(
        [string]$ArcRoot,
        [string]$CascadeRoot = (Get-CascadeRoot)
    )
    $result = [pscustomobject]@{ Unlocked = $false; ArcRoot = $null; Loaded = @(); Missing = @(); Error = $null }
    if (-not $ArcRoot) { $ArcRoot = Find-CascadeArcRoot -CascadeRoot $CascadeRoot }
    if (-not $ArcRoot) {
        $result.Error = 'ARC_NOT_FOUND'
        Write-Warning 'ARC was not found. Set ARC_PATH in config\cascade.env to the folder containing pm.ps1.'
        return $result
    }
    $result.ArcRoot = $ArcRoot
    if (-not (Import-CascadeArcSession -ArcRoot $ArcRoot)) {
        $result.Error = 'ARC_IMPORT_FAILED'
        return $result
    }
    try {
        if (-not (Test-CascadeArcVaultUnlocked)) {
            Write-Host ''
            Write-Host 'Cascade: complete ARC CAC + TOTP authentication in this window.' -ForegroundColor Cyan
            if (-not [bool](Ensure-VaultUnlocked)) {
                $result.Error = 'ARC_AUTH_CANCELLED'
                Write-Warning 'ARC authentication was cancelled. Cascade will open without vault credentials.'
                return $result
            }
        }
    } catch {
        $result.Error = 'ARC_AUTH_EXCEPTION'
        Write-Warning ('ARC unlock failed: ' + $_.Exception.GetType().Name)
        return $result
    }
    $result.Unlocked = [bool](Test-CascadeArcVaultUnlocked)
    if (-not $result.Unlocked) { $result.Error = 'ARC_STATE_NOT_UNLOCKED'; return $result }

    $map = @(
        @{ Env = 'GITLAB_TOKEN';     Item = $env:CASCADE_ARC_ITEM_GitLab;     Default = '' },
        @{ Env = 'LLM_TOKEN';        Item = $env:CASCADE_ARC_ITEM_Llm;        Default = '' },
        @{ Env = 'DATABRICKS_TOKEN'; Item = $env:CASCADE_ARC_ITEM_Databricks; Default = '' },
        @{ Env = 'GITHUB_TOKEN';     Item = $env:CASCADE_ARC_ITEM_GitHub;     Default = '' }
    )
    if (($map | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Item) }).Count -eq 0) {
        Write-Host '    No CASCADE_ARC_ITEM_* names are configured; nothing was read from the vault.' -ForegroundColor DarkYellow
    }
    $loaded = New-Object System.Collections.Generic.List[string]
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $map) {
        $name = if ([string]::IsNullOrWhiteSpace($entry.Item)) { $entry.Default } else { $entry.Item.Trim() }
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($entry.Env, 'Process'))) { [void]$loaded.Add($entry.Env); continue }
        $value = $null
        try { $value = Get-CascadeArcSecret -Name $name } catch { }
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            [Environment]::SetEnvironmentVariable($entry.Env, $value, 'Process')
            [void]$loaded.Add($entry.Env)
        } else {
            [void]$missing.Add($name)
        }
        $value = $null
    }
    $result.Loaded = @($loaded.ToArray())
    $result.Missing = @($missing.ToArray())
    Write-Host ('[+] ARC unlocked; loaded ' + ($result.Loaded -join ', ')) -ForegroundColor Green
    if ($result.Missing.Count -gt 0) { Write-Host ('    Vault items not found: ' + ($result.Missing -join ', ')) -ForegroundColor DarkYellow }
    return $result
}
