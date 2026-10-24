#requires -Version 5.1
# Cascade PowerShell module: a thin shell around Cascade.Core.dll. All logic (providers, write gateway,
# analysis, synthetic sandbox, local host) lives in the DLL; PowerShell handles what only it can do on the
# workstation: reading cascade.env, ARC CAC/TOTP unlock in the console, and launching the browser.
Set-StrictMode -Version 3.0

. (Join-Path $PSScriptRoot 'Arc.ps1')

$script:CascadeCoreLoaded = $false

function Get-CascadeRoot {
    <# Repository or bundle root: two levels above module\Cascade. #>
    [CmdletBinding()]
    param()
    if ($env:CASCADE_ROOT -and (Test-Path -LiteralPath $env:CASCADE_ROOT)) { return (Resolve-Path -LiteralPath $env:CASCADE_ROOT).Path }
    return (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
}

function Get-CascadeCoreCandidates {
    param([string]$Root)
    $desktop = $PSVersionTable.PSEdition -ne 'Core'
    $tfm = if ($desktop) { 'net48' } else { 'netstandard2.0' }
    $list = New-Object System.Collections.Generic.List[string]
    if ($env:CASCADE_CORE_DLL) { [void]$list.Add($env:CASCADE_CORE_DLL) }
    [void]$list.Add((Join-Path $Root ('lib\' + $tfm + '\Cascade.Core.dll')))
    [void]$list.Add((Join-Path $Root 'lib\Cascade.Core.dll'))
    foreach ($cfg in @('Release', 'Debug')) {
        [void]$list.Add((Join-Path $Root ('src\Cascade.Core\bin\' + $cfg + '\' + $tfm + '\Cascade.Core.dll')))
    }
    return @($list.ToArray())
}

function Import-CascadeCore {
    <#
    .SYNOPSIS
      Loads Cascade.Core.dll (net48 for Windows PowerShell 5.1, netstandard2.0 for PowerShell 7).
    .DESCRIPTION
      Looks in lib\ (release bundle) and then in the build output under src\. Newtonsoft.Json.dll must sit
      next to it. Throws with the searched paths when nothing is found.
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-CascadeRoot))
    if ($script:CascadeCoreLoaded -or ('Cascade.Core.Facade' -as [type])) { $script:CascadeCoreLoaded = $true; return }
    $candidates = Get-CascadeCoreCandidates -Root $Root
    $dll = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $dll) {
        throw ("Cascade.Core.dll was not found. Build it with build\Build-Cascade.ps1, or use a release bundle. Searched:`n  " + ($candidates -join "`n  "))
    }
    $dir = Split-Path $dll -Parent
    $json = Join-Path $dir 'Newtonsoft.Json.dll'
    if ((Test-Path -LiteralPath $json) -and -not ('Newtonsoft.Json.JsonConvert' -as [type])) {
        Add-Type -LiteralPath $json -ErrorAction Stop
    }
    try {
        Add-Type -LiteralPath $dll -ErrorAction Stop
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '(?i)policy|blocked|application control|0x800711C7|code integrity') {
            throw ('Windows blocked Cascade.Core.dll (' + $msg + '). The workstation application-control policy does not allow this unsigned DLL. Ask IT to sign or allow it; do not try to work around the policy.')
        }
        throw
    }
    $script:CascadeCoreLoaded = $true
    Write-Verbose ('Loaded ' + $dll)
}

function Get-CascadeGitHubCliToken {
    # Uses an existing `gh auth login` session on a personal machine. Never prompts.
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { return $null }
    try {
        $t = & $gh.Source auth token 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($t)) { return ([string]$t).Trim() }
    } catch { }
    return $null
}

function Initialize-CascadeEnvironment {
    <#
    .SYNOPSIS
      Loads config\cascade.env into the process environment, resolves tokens, and returns CascadeSettings.
    .PARAMETER UseArc
      Auto (default): unlock ARC only for the GitLab provider or when CASCADE_USE_ARC=1. Always / Never override.
    #>
    [CmdletBinding()]
    param(
        [string]$Root = (Get-CascadeRoot),
        [string]$EnvFile,
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseArc = 'Auto'
    )
    Import-CascadeCore -Root $Root
    if (-not $EnvFile) { $EnvFile = Join-Path $Root 'config\cascade.env' }
    $applied = [Cascade.Core.Facade]::LoadEnvFile($EnvFile)
    if ($applied -gt 0) { Write-Verbose ("Applied $applied settings from $EnvFile") }

    $provider = if ($env:CASCADE_PROVIDER) { $env:CASCADE_PROVIDER.Trim().ToLowerInvariant() } elseif ($env:GITLAB_PROJECT_ID) { 'gitlab' } else { 'github' }
    $arc = $null
    $wantArc = ($UseArc -eq 'Always') -or ($UseArc -eq 'Auto' -and ($provider -eq 'gitlab' -or $env:CASCADE_USE_ARC -eq '1'))
    if ($wantArc) { $arc = Connect-CascadeArc -CascadeRoot $Root }
    if ($provider -eq 'github' -and [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN) -and [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        $t = Get-CascadeGitHubCliToken
        if ($t) { [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', $t, 'Process'); Write-Verbose 'Using the GitHub CLI token.' }
    }
    $settings = [Cascade.Core.Facade]::SettingsFromEnvironment($Root)
    return [pscustomobject]@{ Settings = $settings; Arc = $arc; Provider = $provider; EnvFile = $EnvFile; EnvApplied = $applied }
}

function Find-CascadeWebRoot {
    param([string]$Root)
    foreach ($p in @($env:CASCADE_WEBROOT, (Join-Path $Root 'web\dist'), (Join-Path $Root 'web'))) {
        if ($p -and (Test-Path -LiteralPath (Join-Path $p 'index.html'))) {
            $idx = Get-Content -LiteralPath (Join-Path $p 'index.html') -Raw
            if ($idx -notmatch '%APP_JS%') { return (Resolve-Path -LiteralPath $p).Path }
        }
    }
    return $null
}

function Start-Cascade {
    <#
    .SYNOPSIS
      Starts the local Cascade host on 127.0.0.1, opens the browser with a one-time session link, hides the
      console (unless -KeepConsole) and blocks until the browser's End-session control is used.
    #>
    [CmdletBinding()]
    param(
        [string]$Root = (Get-CascadeRoot),
        [int]$Port = 0,
        [switch]$NoBrowser,
        [switch]$KeepConsole,
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseArc = 'Auto',
        [string]$WebRoot
    )
    $envInfo = Initialize-CascadeEnvironment -Root $Root -UseArc $UseArc
    $settings = $envInfo.Settings
    if (-not $WebRoot) { $WebRoot = Find-CascadeWebRoot -Root $Root }
    if (-not $WebRoot) { Write-Warning 'No built UI found (web\dist). The engine will run; build the UI with build\Build-Cascade.ps1.' }
    $launcher = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $launcher['powershell'] = $PSVersionTable.PSVersion.ToString()
    $launcher['edition'] = $PSVersionTable.PSEdition
    if ($envInfo.Arc) {
        $launcher['arcUnlocked'] = [bool]$envInfo.Arc.Unlocked
        if ($envInfo.Arc.Error) { $launcher['arcError'] = $envInfo.Arc.Error }
    }
    $preferred = if ($Port -gt 0) { $Port } else { $settings.PreferredPort }
    $hostObj = [Cascade.Core.Facade]::CreateHost($settings, $WebRoot, $preferred, $launcher)
    $info = $hostObj.Start()
    Write-Host ''
    Write-Host ('Cascade is running at ' + $info.Url) -ForegroundColor Cyan
    Write-Host ('  provider ' + $settings.Provider.Kind + ', ' + $hostObj.Gateway.Policy.Describe()) -ForegroundColor DarkGray
    Write-Host '  Use "End session" in the browser to stop it. Closing this window also stops it.' -ForegroundColor DarkGray
    if (-not $NoBrowser) { Start-Process $info.LaunchUrl }
    $arcFailed = $envInfo.Arc -and -not $envInfo.Arc.Unlocked
    if (-not $KeepConsole -and -not $arcFailed) {
        Start-Sleep -Milliseconds 800
        [void][Cascade.Core.Facade]::SetConsoleVisible($false)
    } elseif ($arcFailed) {
        Write-Host 'Console kept open because ARC did not unlock.' -ForegroundColor Yellow
    }
    try {
        # Poll so Ctrl+C in a visible console still works.
        while (-not $hostObj.WaitForStop(500)) { }
    } finally {
        $hostObj.Dispose()
        [void][Cascade.Core.Facade]::SetConsoleVisible($true)
    }
}

function Test-CascadeConnection {
    [CmdletBinding()]
    param([string]$Root = (Get-CascadeRoot), [ValidateSet('Auto', 'Always', 'Never')][string]$UseArc = 'Auto')
    $s = (Initialize-CascadeEnvironment -Root $Root -UseArc $UseArc).Settings
    $id = [Cascade.Core.Facade]::TestConnection($s)
    [pscustomobject]@{ Provider = $s.Provider.Kind; Login = $id.Login; Api = $id.ApiBase; Source = $s.Provider.SourceRepository; Sandbox = $s.Provider.SandboxRepository }
}

function New-CascadeSyntheticCatalog {
    <# Writes the deterministic synthetic hierarchy to JSON (default: fixtures\synthetic-work-items.json). #>
    [CmdletBinding()]
    param(
        [string]$Path = (Join-Path (Get-CascadeRoot) 'fixtures\synthetic-work-items.json'),
        [ValidateRange(5, 2000)][int]$Total = 100,
        [int]$Seed = 20261024,
        [switch]$IncludeDefects
    )
    Import-CascadeCore
    [Cascade.Core.Facade]::WriteCatalog($Path, $Total, $Seed, [bool]$IncludeDefects)
}

function Invoke-CascadeSeed {
    <#
    .SYNOPSIS
      Creates a synthetic Theme -> Epic -> Feature -> Story -> Task hierarchy in the sandbox repository.
    .EXAMPLE
      Invoke-CascadeSeed -Total 100 -RunId demo-1
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param(
        [ValidateRange(5, 2000)][int]$Total = 100,
        [int]$Seed = 20261024,
        [string]$RunId,
        [switch]$IncludeDefects,
        [switch]$NoNativeLinks,
        [string]$CatalogPath,
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseArc = 'Auto'
    )
    $s = (Initialize-CascadeEnvironment -UseArc $UseArc).Settings
    $target = $s.Provider.Kind.ToString() + ':' + $s.Provider.SandboxRepository
    if (-not $PSCmdlet.ShouldProcess($target, "Create $Total synthetic work items")) { return }
    $progress = [Action[string]] { param($m) Write-Host ('  ' + $m) -ForegroundColor DarkGray }
    if ($CatalogPath) {
        $manifest = [Cascade.Core.Facade]::SeedFromFile($s, $CatalogPath, $RunId, -not $NoNativeLinks, $progress)
    } else {
        $manifest = [Cascade.Core.Facade]::Seed($s, $Total, $Seed, [bool]$IncludeDefects, $RunId, -not $NoNativeLinks, $progress)
    }
    Write-Host ('Run ' + $manifest.RunId + ': created ' + $manifest.Items.Count + ' of ' + $manifest.Requested + ' in ' + $manifest.Target) -ForegroundColor Green
    if ($manifest.Errors.Count -gt 0) { $manifest.Errors | ForEach-Object { Write-Warning $_ } }
    return $manifest
}

function Invoke-CascadeTeardown {
    <#
    .SYNOPSIS
      Permanently deletes synthetic items from the sandbox: one run, every synthetic item, or (with the typed
      confirmation phrase) everything in the sandbox.
    .EXAMPLE
      Invoke-CascadeTeardown -RunId demo-1
    .EXAMPLE
      Invoke-CascadeTeardown -AllSynthetic -DryRun
    .EXAMPLE
      Invoke-CascadeTeardown -Everything -ConfirmText 'DELETE sun-of-anton/cascade-sandbox'
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = 'Run')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Run')][string]$RunId,
        [Parameter(Mandatory, ParameterSetName = 'All')][switch]$AllSynthetic,
        [Parameter(Mandatory, ParameterSetName = 'Everything')][switch]$Everything,
        [Parameter(ParameterSetName = 'Everything')][string]$ConfirmText,
        [switch]$DryRun,
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseArc = 'Auto'
    )
    $s = (Initialize-CascadeEnvironment -UseArc $UseArc).Settings
    $scope = switch ($PSCmdlet.ParameterSetName) { 'All' { 'allSynthetic' } 'Everything' { 'everything' } default { 'run' } }
    $target = $s.Provider.Kind.ToString() + ':' + $s.Provider.SandboxRepository
    $what = switch ($scope) { 'run' { "Delete synthetic items of run $RunId" } 'allSynthetic' { 'Delete every synthetic item' } default { 'Delete EVERY item' } }
    if (-not $DryRun -and -not $PSCmdlet.ShouldProcess($target, $what)) { return }
    $progress = [Action[string]] { param($m) Write-Host ('  ' + $m) -ForegroundColor DarkGray }
    $r = [Cascade.Core.Facade]::Teardown($s, $scope, $RunId, $ConfirmText, [bool]$DryRun, $progress)
    if ($r.DryRun) { Write-Host ("Dry run: $($r.Matched) items would be deleted from $($r.Target).") -ForegroundColor Cyan }
    else { Write-Host ("Deleted $($r.Deleted) of $($r.Matched) from $($r.Target); skipped $($r.Skipped).") -ForegroundColor Green }
    if ($r.Errors.Count -gt 0) { $r.Errors | ForEach-Object { Write-Warning $_ } }
    return $r
}

function Get-CascadeWorkItem {
    <# Lists work items from the source (default) or sandbox repository as provider-agnostic objects. #>
    [CmdletBinding()]
    param([ValidateSet('source', 'sandbox')][string]$Repository = 'source', [int]$Max = 200, [ValidateSet('Auto', 'Always', 'Never')][string]$UseArc = 'Auto')
    $s = (Initialize-CascadeEnvironment -UseArc $UseArc).Settings
    foreach ($w in [Cascade.Core.Facade]::ListWorkItems($s, $Repository, $Max)) {
        [pscustomobject]@{
            Number = $w.Number; Type = $w.Type; Id = $w.CanonicalId; Parent = $w.ParentId; Title = $w.Title; State = $w.State
            Points = if ($w.Meta) { $w.Meta.Points } else { $null }; Labels = ($w.Labels -join ', '); Url = $w.Url
        }
    }
}

Export-ModuleMember -Function Get-CascadeRoot, Import-CascadeCore, Initialize-CascadeEnvironment, Connect-CascadeArc, Start-Cascade, Test-CascadeConnection, New-CascadeSyntheticCatalog, Invoke-CascadeSeed, Invoke-CascadeTeardown, Get-CascadeWorkItem
