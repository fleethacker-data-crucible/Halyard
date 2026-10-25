#requires -Version 5.1
# Lazarette vault bridge. Lazarette owns CAC / passphrase + TOTP; Halyard only loads
# it once, asks it to unlock in the visible console, and reads named items into process environment variables.
# Secret values are never logged, written to disk or passed to the browser. ASCII only for Windows PowerShell 5.1.
#
# Contract (Lazarette docs/INTEGRATION.md): find the install by lib\Lazarette.ps1 or lib\Arc.ps1, load it once,
# unlock with Unlock-LazaretteVaultInteractive (else Unlock-ArcVaultInteractive) and read items with
# Get-LazaretteSecret -Service (else Get-ArcSecret -Service). The Arc-named file and functions are what Lazarette
# exports today; the Lazarette-named ones are preferred as soon as it ships them.
#
# Result codes (ARC_NOT_FOUND, ARC_IMPORT_FAILED, ARC_AUTH_CANCELLED, ARC_AUTH_EXCEPTION, ARC_STATE_NOT_UNLOCKED)
# follow Lazarette's own ARC_* codes, which are its wire format; the launcher passes them to the UI as launcher state.
# ARC_PATH / ARC_ROOT / ARC_HOME, lib\Arc.ps1 and the *-Arc* functions are likewise what Lazarette documents and
# exports today (docs/INTEGRATION.md); the Lazarette-named equivalents are tried first.

$script:HalyardLazaretteModule = $null
$script:HalyardLazaretteRoot = $null

function Get-HalyardLazaretteEntry {
    <# The loader file in a Lazarette install folder: lib\Lazarette.ps1, else lib\Arc.ps1. #>
    param([string]$Root)
    if (-not $Root) { return $null }
    foreach ($rel in @('lib\Lazarette.ps1', 'lib\Arc.ps1')) {
        $p = Join-Path $Root $rel
        if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
    }
    return $null
}

function Find-HalyardLazaretteRoot {
    <#
    .SYNOPSIS
      Finds the Lazarette install. Order: LAZARETTE_PATH / LAZARETTE_ROOT / LAZARETTE_HOME, then the former
      ARC_PATH / ARC_ROOT / ARC_HOME, then a sibling folder (Lazarette, then arc), then
      %USERPROFILE%\Projects\Lazarette, %USERPROFILE%\Projects\arc, %LOCALAPPDATA%\Lazarette, %LOCALAPPDATA%\ARC.
      A folder qualifies when it has lib\Lazarette.ps1 or lib\Arc.ps1; the first qualifying folder wins, so an
      explicit variable always beats a guessed location.
    #>
    [CmdletBinding()]
    param([string]$HalyardRoot)
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($p in @($env:LAZARETTE_PATH, $env:LAZARETTE_ROOT, $env:LAZARETTE_HOME, $env:ARC_PATH, $env:ARC_ROOT, $env:ARC_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($p)) { [void]$candidates.Add($p.Trim().Trim('"').Trim("'")) }
    }
    if ($HalyardRoot) {
        $parent = Split-Path $HalyardRoot -Parent
        foreach ($name in @('Lazarette', 'lazarette', 'arc', 'Arc', 'ARC')) { [void]$candidates.Add((Join-Path $parent $name)) }
    }
    if ($env:USERPROFILE) {
        foreach ($rel in @('Projects\Lazarette', 'Projects\arc', 'Lazarette', 'arc')) { [void]$candidates.Add((Join-Path $env:USERPROFILE $rel)) }
    }
    if ($env:LOCALAPPDATA) {
        foreach ($name in @('Lazarette', 'ARC')) { [void]$candidates.Add((Join-Path $env:LOCALAPPDATA $name)) }
    }
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
    <# The first defined command of the given names (Lazarette name first, then the ARC name). #>
    param([string[]]$Names)
    foreach ($n in $Names) {
        $cmd = Get-Command $n -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd }
    }
    return $null
}

function Test-HalyardLazaretteLibAvailable {
    $unlock = Get-HalyardLazaretteCommand @('Unlock-LazaretteVaultInteractive', 'Unlock-ArcVaultInteractive')
    $secret = Get-HalyardLazaretteCommand @('Get-LazaretteSecret', 'Get-ArcSecret')
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
        return (Test-HalyardLazaretteLibAvailable)
    } catch {
        Write-Warning ('Lazarette import failed: ' + $_.Exception.GetType().Name + '. Open Lazarette directly and review its logs.')
        return $false
    }
}

function Test-HalyardLazaretteVaultUnlocked {
    $test = Get-HalyardLazaretteCommand @('Test-LazaretteVaultUnlocked', 'Test-ArcVaultUnlocked')
    if (-not $test) { return $false }
    try { return [bool](& $test) } catch { return $false }
}

function Invoke-HalyardLazaretteUnlock {
    $unlock = Get-HalyardLazaretteCommand @('Unlock-LazaretteVaultInteractive', 'Unlock-ArcVaultInteractive')
    if (-not $unlock) { return $false }
    return [bool](& $unlock)
}

function Get-HalyardLazaretteSecret {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $get = Get-HalyardLazaretteCommand @('Get-LazaretteSecret', 'Get-ArcSecret')
    if (-not $get) { return $null }
    # Exactly one item or it throws (locked, missing, ambiguous); a throw is reported as a missing item.
    try {
        $v = & $get -Service $Name
        if (-not [string]::IsNullOrWhiteSpace($v)) { return ([string]$v).Trim() }
    } catch { }
    return $null
}

function Get-HalyardLazaretteItemName {
    <# Vault item name for one purpose: HALYARD_LAZARETTE_ITEM_<Purpose>, else the former CASCADE_ARC_ITEM_<Purpose> (one release). #>
    param([Parameter(Mandatory)][string]$Purpose)
    foreach ($n in @(('HALYARD_LAZARETTE_ITEM_' + $Purpose), ('CASCADE_ARC_ITEM_' + $Purpose))) {
        $v = [Environment]::GetEnvironmentVariable($n, 'Process')
        if (-not [string]::IsNullOrWhiteSpace($v)) { return $v.Trim() }
    }
    return ''
}

function Connect-HalyardLazarette {
    <#
    .SYNOPSIS
      Loads Lazarette once, completes the unlock (CAC PIN or passphrase, then TOTP) in this console, and copies
      named vault items into process environment variables (GITLAB_TOKEN, LLM_TOKEN, DATABRICKS_TOKEN,
      GITHUB_TOKEN). The item names come from HALYARD_LAZARETTE_ITEM_GitLab / _Llm / _Databricks / _GitHub in
      config\halyard.env (the former CASCADE_ARC_ITEM_* names are still read); unset = skipped.
    .OUTPUTS
      A summary object with Unlocked, LazaretteRoot, Loaded, Missing and Error (Lazarette's ARC_* result codes).
    #>
    [CmdletBinding()]
    param(
        [string]$LazaretteRoot,
        [string]$HalyardRoot = (Get-HalyardRoot)
    )
    $result = [pscustomobject]@{ Unlocked = $false; LazaretteRoot = $null; Loaded = @(); Missing = @(); Error = $null }
    if (-not $LazaretteRoot) { $LazaretteRoot = Find-HalyardLazaretteRoot -HalyardRoot $HalyardRoot }
    if (-not $LazaretteRoot) {
        $result.Error = 'ARC_NOT_FOUND'
        Write-Warning 'Lazarette was not found. Set LAZARETTE_PATH in config\halyard.env to the Lazarette folder (the one containing lib\).'
        return $result
    }
    $result.LazaretteRoot = $LazaretteRoot
    if (-not (Import-HalyardLazaretteSession -LazaretteRoot $LazaretteRoot)) {
        $result.Error = 'ARC_IMPORT_FAILED'
        return $result
    }
    try {
        if (-not (Test-HalyardLazaretteVaultUnlocked)) {
            Write-Host ''
            Write-Host 'Halyard: unlock Lazarette in this window (CAC PIN or passphrase, then the authenticator code).' -ForegroundColor Cyan
            if (-not (Invoke-HalyardLazaretteUnlock)) {
                $result.Error = 'ARC_AUTH_CANCELLED'
                Write-Warning 'Lazarette was not unlocked. Halyard will open without vault credentials.'
                return $result
            }
        }
    } catch {
        $result.Error = 'ARC_AUTH_EXCEPTION'
        Write-Warning ('Lazarette unlock failed: ' + $_.Exception.GetType().Name)
        return $result
    }
    $result.Unlocked = [bool](Test-HalyardLazaretteVaultUnlocked)
    if (-not $result.Unlocked) { $result.Error = 'ARC_STATE_NOT_UNLOCKED'; return $result }

    $map = @(
        @{ Env = 'GITLAB_TOKEN';     Item = (Get-HalyardLazaretteItemName 'GitLab') },
        @{ Env = 'LLM_TOKEN';        Item = (Get-HalyardLazaretteItemName 'Llm') },
        @{ Env = 'DATABRICKS_TOKEN'; Item = (Get-HalyardLazaretteItemName 'Databricks') },
        @{ Env = 'GITHUB_TOKEN';     Item = (Get-HalyardLazaretteItemName 'GitHub') }
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
