#requires -Version 5.1
# Halyard PowerShell module: a thin shell around Halyard.Core.dll. All logic (providers, write gateway,
# analysis, synthetic sandbox, local host) lives in the DLL; PowerShell handles what only it can do on the
# workstation: reading halyard.env, the Lazarette unlock in the console, and launching the browser.
Set-StrictMode -Version 3.0

. (Join-Path $PSScriptRoot 'Lazarette.ps1')

$script:HalyardCoreLoaded = $false

function Get-HalyardSetting {
    <# Process environment value of HALYARD_<Suffix>, else the former CASCADE_<Suffix> (fallback kept for one release); $null when neither is set. #>
    param([Parameter(Mandatory)][string]$Suffix)
    foreach ($n in @(('HALYARD_' + $Suffix), ('CASCADE_' + $Suffix))) {
        $v = [Environment]::GetEnvironmentVariable($n, 'Process')
        if (-not [string]::IsNullOrWhiteSpace($v)) { return $v.Trim() }
    }
    return $null
}

function Get-HalyardEnvFile {
    <#
      config\halyard.env. On the first launch after the rename, a config\cascade.env is copied forward to it once
      (variable names updated, the original left untouched) by Halyard.Core before this returns.
    #>
    param([Parameter(Mandatory)][string]$Root)
    if ([Halyard.Core.Facade]::MigrateLegacyEnvFile($Root)) {
        Write-Host 'Copied config\cascade.env to config\halyard.env (first launch after the rename; the old file is unchanged).' -ForegroundColor DarkGray
    }
    return (Join-Path $Root 'config\halyard.env')
}

function Get-HalyardRoot {
    <# Repository or bundle root: two levels above module\Halyard. #>
    [CmdletBinding()]
    param()
    $r = Get-HalyardSetting 'ROOT'
    if ($r -and (Test-Path -LiteralPath $r)) { return (Resolve-Path -LiteralPath $r).Path }
    return (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
}

function Get-HalyardCoreCandidates {
    param([string]$Root)
    $desktop = $PSVersionTable.PSEdition -ne 'Core'
    $tfm = if ($desktop) { 'net48' } else { 'netstandard2.0' }
    $list = New-Object System.Collections.Generic.List[string]
    $custom = Get-HalyardSetting 'CORE_DLL'
    if ($custom) { [void]$list.Add($custom) }
    [void]$list.Add((Join-Path $Root ('lib\' + $tfm + '\Halyard.Core.dll')))
    [void]$list.Add((Join-Path $Root 'lib\Halyard.Core.dll'))
    foreach ($cfg in @('Release', 'Debug')) {
        [void]$list.Add((Join-Path $Root ('src\Halyard.Core\bin\' + $cfg + '\' + $tfm + '\Halyard.Core.dll')))
    }
    return @($list.ToArray())
}

function Import-HalyardCore {
    <#
    .SYNOPSIS
      Loads Halyard.Core.dll (net48 for Windows PowerShell 5.1, netstandard2.0 for PowerShell 7).
    .DESCRIPTION
      Looks in lib\ (release bundle) and then in the build output under src\. Newtonsoft.Json.dll must sit
      next to it. Throws with the searched paths when nothing is found.
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-HalyardRoot))
    if ($script:HalyardCoreLoaded -or ('Halyard.Core.Facade' -as [type])) { $script:HalyardCoreLoaded = $true; return }
    $candidates = Get-HalyardCoreCandidates -Root $Root
    $dll = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $dll) {
        throw ("Halyard.Core.dll was not found. Download the release bundle (Halyard-<version>.zip from https://github.com/sun-of-anton/Halyard/releases) and run Launch-Halyard.bat from it, or build with build\Build-Halyard.ps1. Searched:`n  " + ($candidates -join "`n  "))
    }
    $dir = Split-Path $dll -Parent
    $json = Join-Path $dir 'Newtonsoft.Json.dll'
    if (-not ('Newtonsoft.Json.JsonConvert' -as [type])) {
        if (-not (Test-Path -LiteralPath $json)) {
            throw ('Newtonsoft.Json.dll must sit next to ' + $dll + '. Use the release bundle from https://github.com/sun-of-anton/Halyard/releases or rebuild with build\Build-Halyard.ps1.')
        }
        Add-Type -LiteralPath $json -ErrorAction Stop
    }
    try {
        Add-Type -LiteralPath $dll -ErrorAction Stop
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '(?i)policy|blocked|application control|0x800711C7|code integrity') {
            throw ('Windows blocked Halyard.Core.dll (' + $msg + '). The workstation application-control policy does not allow this unsigned DLL. Ask IT to sign or allow it; do not try to work around the policy.')
        }
        throw
    }
    $script:HalyardCoreLoaded = $true
    Write-Verbose ('Loaded ' + $dll)
}

function Get-HalyardGitHubCliToken {
    # Uses an existing `gh auth login` session on a personal machine. Never prompts.
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { return $null }
    try {
        $t = & $gh.Source auth token 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($t)) { return ([string]$t).Trim() }
    } catch { }
    return $null
}

function Initialize-HalyardEnvironment {
    <#
    .SYNOPSIS
      Loads config\halyard.env into the process environment (copying a former config\cascade.env forward once),
      resolves tokens, and returns HalyardSettings.
    .PARAMETER UseLazarette
      Auto (default): unlock Lazarette only for the GitLab provider or when HALYARD_USE_LAZARETTE=1. Always / Never override.
    #>
    [CmdletBinding()]
    param(
        [string]$Root = (Get-HalyardRoot),
        [string]$EnvFile,
        [ValidateSet('Auto', 'Always', 'Never')][Alias('UseArc')][string]$UseLazarette = 'Auto'
    )
    Import-HalyardCore -Root $Root
    if (-not $EnvFile) { $EnvFile = Get-HalyardEnvFile -Root $Root }
    $applied = [Halyard.Core.Facade]::LoadEnvFile($EnvFile)
    if ($applied -gt 0) { Write-Verbose ("Applied $applied settings from $EnvFile") }

    $providerSetting = Get-HalyardSetting 'PROVIDER'
    $provider = if ($providerSetting) { $providerSetting.ToLowerInvariant() } else { 'github' }
    $useVault = (Get-HalyardSetting 'USE_LAZARETTE') -eq '1' -or $env:CASCADE_USE_ARC -eq '1'   # former name, one release
    $vault = $null
    $wantVault = ($UseLazarette -eq 'Always') -or ($UseLazarette -eq 'Auto' -and ($provider -eq 'gitlab' -or $useVault))
    if ($wantVault) { $vault = Connect-HalyardLazarette -HalyardRoot $Root }
    if ($provider -eq 'github' -and [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN) -and [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        $t = Get-HalyardGitHubCliToken
        if ($t) { [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', $t, 'Process'); Write-Verbose 'Using the GitHub CLI token.' }
    }
    $settings = [Halyard.Core.Facade]::SettingsFromEnvironment($Root)
    return [pscustomobject]@{ Settings = $settings; Lazarette = $vault; Provider = $provider; EnvFile = $EnvFile; EnvApplied = $applied }
}

function Find-HalyardWebRoot {
    param([string]$Root)
    foreach ($p in @((Get-HalyardSetting 'WEBROOT'), (Join-Path $Root 'web\dist'), (Join-Path $Root 'web'))) {
        if ($p -and (Test-Path -LiteralPath (Join-Path $p 'index.html'))) {
            $idx = Get-Content -LiteralPath (Join-Path $p 'index.html') -Raw
            if ($idx -notmatch '%APP_JS%') { return (Resolve-Path -LiteralPath $p).Path }
        }
    }
    return $null
}

function Start-Halyard {
    <#
    .SYNOPSIS
      Starts the local Halyard host on 127.0.0.1, opens the browser with a one-time session link, hides the
      console (unless -KeepConsole) and blocks until the browser's End-session control is used.
    #>
    [CmdletBinding()]
    param(
        [string]$Root = (Get-HalyardRoot),
        [int]$Port = 0,
        [switch]$NoBrowser,
        [switch]$KeepConsole,
        [ValidateSet('Auto', 'Always', 'Never')][Alias('UseArc')][string]$UseLazarette = 'Auto',
        [string]$WebRoot,
        [bool]$AutoStop = $true
    )
    $envInfo = Initialize-HalyardEnvironment -Root $Root -UseLazarette $UseLazarette
    $settings = $envInfo.Settings
    if (-not $WebRoot) { $WebRoot = Find-HalyardWebRoot -Root $Root }
    if (-not $WebRoot) { Write-Warning 'No built UI found (web\dist). The engine will run; build the UI with build\Build-Halyard.ps1.' }
    $launcher = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $launcher['powershell'] = $PSVersionTable.PSVersion.ToString()
    $launcher['edition'] = $PSVersionTable.PSEdition
    if ($envInfo.Lazarette) {
        $launcher['lazaretteUnlocked'] = [bool]$envInfo.Lazarette.Unlocked
        if ($envInfo.Lazarette.Error) { $launcher['lazaretteError'] = $envInfo.Lazarette.Error }
    }
    $preferred = if ($Port -gt 0) { $Port } else { $settings.PreferredPort }
    $autoStop = $AutoStop -and $settings.AutoStop
    $hostObj = [Halyard.Core.Facade]::CreateHost($settings, $WebRoot, $preferred, $launcher, $autoStop)
    $info = $hostObj.Start()
    Write-Host ''
    Write-Host ('Halyard is running at ' + $info.Url) -ForegroundColor Cyan
    Write-Host ('  provider ' + $settings.Provider.Kind + ', ' + $hostObj.Gateway.Policy.Describe()) -ForegroundColor DarkGray
    if ($autoStop) { Write-Host '  It stops by itself when the browser tab closes (--stay keeps it running). "End session" in the browser also stops it.' -ForegroundColor DarkGray }
    else { Write-Host '  Use "End session" in the browser or scripts\Stop-Halyard.ps1 to stop it. Closing this window also stops it.' -ForegroundColor DarkGray }
    if ($NoBrowser) { Write-Host ('  Session link (open once): ' + $info.LaunchUrl) -ForegroundColor DarkGray } else { Start-Process $info.LaunchUrl }
    $vaultFailed = $envInfo.Lazarette -and -not $envInfo.Lazarette.Unlocked
    if (-not $KeepConsole -and -not $vaultFailed) {
        Start-Sleep -Milliseconds 800
        [void][Halyard.Core.Facade]::SetConsoleVisible($false)
    } elseif ($vaultFailed) {
        Write-Host 'Console kept open because Lazarette did not unlock.' -ForegroundColor Yellow
    }
    try {
        # Poll so Ctrl+C in a visible console still works.
        while (-not $hostObj.WaitForStop(500)) { }
    } finally {
        $hostObj.Dispose()
        [void][Halyard.Core.Facade]::SetConsoleVisible($true)
    }
}

function Test-HalyardConnection {
    [CmdletBinding()]
    param([string]$Root = (Get-HalyardRoot), [ValidateSet('Auto', 'Always', 'Never')][Alias('UseArc')][string]$UseLazarette = 'Auto')
    $s = (Initialize-HalyardEnvironment -Root $Root -UseLazarette $UseLazarette).Settings
    $id = [Halyard.Core.Facade]::TestConnection($s)
    [pscustomobject]@{ Provider = $s.Provider.Kind; Login = $id.Login; Api = $id.ApiBase; Source = $s.Provider.SourceRepository; Sandbox = $s.Provider.SandboxRepository }
}

function New-HalyardSyntheticCatalog {
    <# Writes the deterministic synthetic hierarchy to JSON (default: fixtures\synthetic-work-items.json). #>
    [CmdletBinding()]
    param(
        [string]$Path = (Join-Path (Get-HalyardRoot) 'fixtures\synthetic-work-items.json'),
        [ValidateRange(5, 2000)][int]$Total = 100,
        [int]$Seed = 20261024,
        [switch]$IncludeDefects,
        [ValidateRange(0.0, 0.5)][double]$DefectRate = 0.2
    )
    Import-HalyardCore
    [Halyard.Core.Facade]::WriteCatalog($Path, $Total, $Seed, [bool]$IncludeDefects, [double]$DefectRate)
}

function Invoke-HalyardSeed {
    <#
    .SYNOPSIS
      Creates a synthetic Theme -> Epic -> Feature -> Story -> Task hierarchy in the sandbox repository.
    .EXAMPLE
      Invoke-HalyardSeed -Total 100 -RunId demo-1 -IncludeDefects
      Creates 100 items of which about 20 carry a deliberate gap for the human review workflow.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param(
        [ValidateRange(5, 2000)][int]$Total = 100,
        [int]$Seed = 20261024,
        [string]$RunId,
        [switch]$IncludeDefects,
        [ValidateRange(0.0, 0.5)][double]$DefectRate = 0.2,
        [switch]$NoNativeLinks,
        [string]$CatalogPath,
        [ValidateSet('Auto', 'Always', 'Never')][Alias('UseArc')][string]$UseLazarette = 'Auto'
    )
    $s = (Initialize-HalyardEnvironment -UseLazarette $UseLazarette).Settings
    $target = $s.Provider.Kind.ToString() + ':' + $s.Provider.SandboxRepository
    if (-not $PSCmdlet.ShouldProcess($target, "Create $Total synthetic work items")) { return }
    $progress = [Action[string]] { param($m) Write-Host ('  ' + $m) -ForegroundColor DarkGray }
    if ($CatalogPath) {
        $manifest = [Halyard.Core.Facade]::SeedFromFile($s, $CatalogPath, $RunId, -not $NoNativeLinks, $progress)
    } else {
        $manifest = [Halyard.Core.Facade]::Seed($s, $Total, $Seed, [bool]$IncludeDefects, [double]$DefectRate, $RunId, -not $NoNativeLinks, $progress)
    }
    Write-Host ('Run ' + $manifest.RunId + ': created ' + $manifest.Items.Count + ' of ' + $manifest.Requested + ' in ' + $manifest.Target + ' (' + $manifest.NeedsReview + ' seeded with gaps for review)') -ForegroundColor Green
    if ($manifest.Errors.Count -gt 0) { $manifest.Errors | ForEach-Object { Write-Warning $_ } }
    return $manifest
}

function Invoke-HalyardTeardown {
    <#
    .SYNOPSIS
      Permanently deletes synthetic items from the sandbox: one run, every synthetic item, or (with the typed
      confirmation phrase) everything in the sandbox.
    .EXAMPLE
      Invoke-HalyardTeardown -RunId demo-1
    .EXAMPLE
      Invoke-HalyardTeardown -AllSynthetic -DryRun
    .EXAMPLE
      Invoke-HalyardTeardown -Everything -ConfirmText 'DELETE fleethacker-data-crucible/Halyard-Ballast'
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = 'Run')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Run')][string]$RunId,
        [Parameter(Mandatory, ParameterSetName = 'All')][switch]$AllSynthetic,
        [Parameter(Mandatory, ParameterSetName = 'Everything')][switch]$Everything,
        [Parameter(ParameterSetName = 'Everything')][string]$ConfirmText,
        [switch]$DryRun,
        [ValidateSet('Auto', 'Always', 'Never')][Alias('UseArc')][string]$UseLazarette = 'Auto'
    )
    $s = (Initialize-HalyardEnvironment -UseLazarette $UseLazarette).Settings
    $scope = switch ($PSCmdlet.ParameterSetName) { 'All' { 'allSynthetic' } 'Everything' { 'everything' } default { 'run' } }
    $target = $s.Provider.Kind.ToString() + ':' + $s.Provider.SandboxRepository
    $what = switch ($scope) { 'run' { "Delete synthetic items of run $RunId" } 'allSynthetic' { 'Delete every synthetic item' } default { 'Delete EVERY item' } }
    if (-not $DryRun -and -not $PSCmdlet.ShouldProcess($target, $what)) { return }
    $progress = [Action[string]] { param($m) Write-Host ('  ' + $m) -ForegroundColor DarkGray }
    $r = [Halyard.Core.Facade]::Teardown($s, $scope, $RunId, $ConfirmText, [bool]$DryRun, $progress)
    if ($r.DryRun) { Write-Host ("Dry run: $($r.Matched) items would be deleted from $($r.Target).") -ForegroundColor Cyan }
    else { Write-Host ("Deleted $($r.Deleted) of $($r.Matched) from $($r.Target); skipped $($r.Skipped).") -ForegroundColor Green }
    if ($r.Errors.Count -gt 0) { $r.Errors | ForEach-Object { Write-Warning $_ } }
    return $r
}

function Get-HalyardWorkItem {
    <# Lists work items from the source (default) or sandbox repository as provider-agnostic objects. #>
    [CmdletBinding()]
    param([ValidateSet('source', 'sandbox')][string]$Repository = 'source', [int]$Max = 200, [ValidateSet('Auto', 'Always', 'Never')][Alias('UseArc')][string]$UseLazarette = 'Auto')
    $s = (Initialize-HalyardEnvironment -UseLazarette $UseLazarette).Settings
    foreach ($w in [Halyard.Core.Facade]::ListWorkItems($s, $Repository, $Max)) {
        [pscustomobject]@{
            Number = $w.Number; Type = $w.Type; Id = $w.CanonicalId; Parent = $w.ParentId; Title = $w.Title; State = $w.State
            Points = if ($w.Meta) { $w.Meta.Points } else { $null }; Labels = ($w.Labels -join ', '); Url = $w.Url
        }
    }
}

Export-ModuleMember -Function Get-HalyardRoot, Import-HalyardCore, Initialize-HalyardEnvironment, Connect-HalyardLazarette, Start-Halyard, Test-HalyardConnection, New-HalyardSyntheticCatalog, Invoke-HalyardSeed, Invoke-HalyardTeardown, Get-HalyardWorkItem

