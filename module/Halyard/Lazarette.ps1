#requires -Version 5.1
# Lazarette vault bridge. Lazarette owns smart card / passphrase + TOTP; Halyard only loads
# it once, asks it to unlock in the visible console, and reads named items into process environment variables.
# Secret values are never logged, written to disk or passed to the browser. ASCII only for Windows PowerShell 5.1.
#
# Contract (Lazarette docs/INTEGRATION.md): find the install (the folder with lib\Lazarette.ps1), load it once, unlock
# with Unlock-LazaretteVaultInteractive and read items with Get-LazaretteSecret -Service.
#
# Result codes (LAZARETTE_NOT_FOUND, LAZARETTE_IMPORT_FAILED, LAZARETTE_AUTH_CANCELLED, LAZARETTE_AUTH_EXCEPTION,
# LAZARETTE_STATE_NOT_UNLOCKED) are passed to the UI as launcher state.

$script:HalyardLazaretteModule = $null
$script:HalyardLazaretteRoot = $null
$script:HalyardLazaretteUnlocked = $false

function Get-HalyardLazaretteEntry {
    <# The loader file in a Lazarette install folder: lib\Lazarette.ps1. #>
    param([string]$Root)
    if (-not $Root) { return $null }
    $p = Join-Path $Root 'lib\Lazarette.ps1'
    if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
    return $null
}

function Find-HalyardLazaretteRoot {
    <#
    .SYNOPSIS
      Finds the Lazarette install. Order: LAZARETTE_PATH, then a Lazarette folder next to Halyard, then
      %USERPROFILE%\Projects\Lazarette. A folder qualifies when it has lib\Lazarette.ps1; the first qualifying folder
      wins, so an explicit variable always beats a guessed location.
    #>
    [CmdletBinding()]
    param([string]$HalyardRoot)
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:LAZARETTE_PATH)) { [void]$candidates.Add($env:LAZARETTE_PATH.Trim().Trim('"').Trim("'")) }
    if ($HalyardRoot) { [void]$candidates.Add((Join-Path (Split-Path $HalyardRoot -Parent) 'Lazarette')) }
    if ($env:USERPROFILE) { [void]$candidates.Add((Join-Path $env:USERPROFILE 'Projects\Lazarette')) }
    $seen = @{}
    foreach ($c in $candidates) {
        try {
            $full = [IO.Path]::GetFullPath($c)
            $k = $full.ToLowerInvariant()
            if ($seen.ContainsKey($k)) { continue }
            $seen[$k] = $true
            if (Get-HalyardLazaretteEntry -Root $full) { return $full }
        } catch { }
    }
    return $null
}

function Get-HalyardLazaretteCommand {
    <# The first defined command of the given names. #>
    param([string[]]$Names)
    foreach ($n in $Names) {
        $cmd = Get-Command $n -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd }
    }
    return $null
}

function Test-HalyardLazaretteLibAvailable {
    $unlock = Get-HalyardLazaretteCommand @('Unlock-LazaretteVaultInteractive')
    $secret = Get-HalyardLazaretteCommand @('Get-LazaretteSecret')
    return [bool]($unlock -and $secret)
}

function Import-HalyardLazaretteSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LazaretteRoot)
    # Load once per process; an unlocked vault stays unlocked while the functions are loaded.
    if (Test-HalyardLazaretteLibAvailable) { return $true }
    if (Get-Module -Name HalyardLazarette -ErrorAction SilentlyContinue) { return $false }
    $entry = Get-HalyardLazaretteEntry -Root $LazaretteRoot
    if (-not $entry) { return $false }
    try {
        $module = New-Module -Name HalyardLazarette -ScriptBlock {
            param([string]$EntryPath)
            $ErrorActionPreference = 'Continue'
            . $EntryPath
            Export-ModuleMember -Function * -Alias * -ErrorAction SilentlyContinue
        } -ArgumentList $entry
        Import-Module -ModuleInfo $module -Global -ErrorAction Stop -WarningAction SilentlyContinue
        $script:HalyardLazaretteModule = $module
        $script:HalyardLazaretteRoot = $LazaretteRoot
        $script:HalyardLazaretteUnlocked = $false
        return (Test-HalyardLazaretteLibAvailable)
    } catch {
        Write-Warning ('Lazarette import failed: ' + $_.Exception.GetType().Name + '. Open Lazarette directly and review its logs.')
        return $false
    }
}

function Test-HalyardLazaretteVaultUnlocked {
    $test = Get-HalyardLazaretteCommand @('Test-LazaretteVaultUnlocked')
    if (-not $test) {
        # Lock-less provider (Lazarette Lite): no vault state to test, so a
        # successful unlock call in this process means the store is usable.
        # Full Lazarette always defines this command, so its path is unchanged.
        return [bool]$script:HalyardLazaretteUnlocked
    }
    try { return [bool](& $test) } catch { return $false }
}

function Invoke-HalyardLazaretteUnlock {
    $unlock = Get-HalyardLazaretteCommand @('Unlock-LazaretteVaultInteractive')
    if (-not $unlock) { return $false }
    $ok = [bool](& $unlock)
    if ($ok) { $script:HalyardLazaretteUnlocked = $true }
    return $ok
}

function Get-HalyardLazaretteSecret {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $get = Get-HalyardLazaretteCommand @('Get-LazaretteSecret')
    if (-not $get) { return $null }
    # Exactly one item or it throws (locked, missing, ambiguous); a throw is reported as a missing item.
    try {
        $v = & $get -Service $Name
        if (-not [string]::IsNullOrWhiteSpace($v)) { return ([string]$v).Trim() }
    } catch { }
    return $null
}

function Get-HalyardLazaretteItemName {
    <# Vault item name for one purpose: HALYARD_LAZARETTE_ITEM_<Purpose>. #>
    param([Parameter(Mandatory)][string]$Purpose)
    $v = [Environment]::GetEnvironmentVariable(('HALYARD_LAZARETTE_ITEM_' + $Purpose), 'Process')
    if (-not [string]::IsNullOrWhiteSpace($v)) { return $v.Trim() }
    return ''
}

function Connect-HalyardLazarette {
    <#
    .SYNOPSIS
      Loads Lazarette once, completes the unlock (smart card PIN or passphrase, then TOTP) in this console, and copies
      named vault items into process environment variables (GITLAB_TOKEN, GITHUB_TOKEN). The item names come from
      HALYARD_LAZARETTE_ITEM_GitLab / _GitHub in config\halyard.env; unset = skipped. Model calls do not use the vault:
      they go to Fairlead with the key Fairlead installs in Credential Manager (Fairlead/Halyard).
    .OUTPUTS
      A summary object with Unlocked, LazaretteRoot, Loaded, Missing and Error (the LAZARETTE_* result codes above).
    #>
    [CmdletBinding()]
    param(
        [string]$LazaretteRoot,
        [string]$HalyardRoot = (Get-HalyardRoot)
    )
    $result = [pscustomobject]@{ Unlocked = $false; LazaretteRoot = $null; Loaded = @(); Missing = @(); Error = $null }
    if (-not $LazaretteRoot) { $LazaretteRoot = Find-HalyardLazaretteRoot -HalyardRoot $HalyardRoot }
    if (-not $LazaretteRoot) {
        $result.Error = 'LAZARETTE_NOT_FOUND'
        Write-Warning 'Lazarette was not found. Set LAZARETTE_PATH in config\halyard.env to the Lazarette folder (the one containing lib\).'
        return $result
    }
    $result.LazaretteRoot = $LazaretteRoot
    if (-not (Import-HalyardLazaretteSession -LazaretteRoot $LazaretteRoot)) {
        $result.Error = 'LAZARETTE_IMPORT_FAILED'
        return $result
    }
    try {
        if (-not (Test-HalyardLazaretteVaultUnlocked)) {
            if (Get-HalyardLazaretteCommand @('Test-LazaretteVaultUnlocked')) {
                Write-Host ''
                Write-Host 'Halyard: unlock Lazarette in this window (smart card PIN or passphrase, then the authenticator code).' -ForegroundColor Cyan
            }
            if (-not (Invoke-HalyardLazaretteUnlock)) {
                $result.Error = 'LAZARETTE_AUTH_CANCELLED'
                Write-Warning 'Lazarette was not unlocked. Halyard will open without vault credentials.'
                return $result
            }
        }
    } catch {
        $result.Error = 'LAZARETTE_AUTH_EXCEPTION'
        Write-Warning ('Lazarette unlock failed: ' + $_.Exception.GetType().Name)
        return $result
    }
    $result.Unlocked = [bool](Test-HalyardLazaretteVaultUnlocked)
    if (-not $result.Unlocked) { $result.Error = 'LAZARETTE_STATE_NOT_UNLOCKED'; return $result }

    $map = @(
        @{ Env = 'GITLAB_TOKEN'; Item = (Get-HalyardLazaretteItemName 'GitLab') },
        @{ Env = 'GITHUB_TOKEN'; Item = (Get-HalyardLazaretteItemName 'GitHub') }
    )
    if (@($map | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Item) }).Count -eq 0) {
        Write-Host '    No HALYARD_LAZARETTE_ITEM_* names are configured; nothing was read from the vault.' -ForegroundColor DarkYellow
    }
    $loaded = New-Object System.Collections.Generic.List[string]
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $map) {
        $name = [string]$entry.Item
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($entry.Env, 'Process'))) { [void]$loaded.Add($entry.Env); continue }
        $value = $null
        try { $value = Get-HalyardLazaretteSecret -Name $name } catch { }
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
    Write-Host ('[+] Lazarette unlocked; loaded ' + ($result.Loaded -join ', ')) -ForegroundColor Green
    if ($result.Missing.Count -gt 0) { Write-Host ('    Vault items not found: ' + ($result.Missing -join ', ')) -ForegroundColor DarkYellow }
    return $result
}
