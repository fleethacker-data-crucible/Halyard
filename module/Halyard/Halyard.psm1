#requires -Version 5.1
# Halyard PowerShell module: a thin shell around the Halyard engine (Halyard.Core). All logic (providers, write
# gateway, analysis, synthetic sandbox, local host) lives in the engine, which ships as C# source and is compiled in
# memory at launch (Import-HalyardCore); PowerShell handles what only it can do on the workstation: reading
# halyard.env, the Lazarette unlock in the console, and launching the browser.
Set-StrictMode -Version 3.0

. (Join-Path $PSScriptRoot 'Lazarette.ps1')
. (Join-Path $PSScriptRoot 'Desktop.ps1')

$script:HalyardCoreLoaded = $false

function Get-HalyardSetting {
    <# Process environment value of HALYARD_<Suffix>; $null when it is not set. #>
    param([Parameter(Mandatory)][string]$Suffix)
    $v = [Environment]::GetEnvironmentVariable(('HALYARD_' + $Suffix), 'Process')
    if (-not [string]::IsNullOrWhiteSpace($v)) { return $v.Trim() }
    return $null
}

function Get-HalyardEnvFile {
    <# config\halyard.env. #>
    param([Parameter(Mandatory)][string]$Root)
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

# Framework assemblies the engine source may reference. Nothing else: no NuGet package, no third-party DLL.
# Keep in step with <Reference> in src\Halyard.Core\Halyard.Core.csproj (the C# 5 check build).
$script:HalyardCoreReferences = @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Security.dll')
$script:HalyardCoreLoad = $null

function Get-HalyardCoreSource {
    <#
    .SYNOPSIS
      The engine's C# source files (src\Halyard.Core\**\*.cs, build output folders excluded), in a stable order.
      HALYARD_CORE_SOURCE may point at another source folder (development).
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-HalyardRoot))
    $dir = Get-HalyardSetting 'CORE_SOURCE'
    if (-not $dir) { $dir = Join-Path $Root 'src\Halyard.Core' }
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        throw ('The Halyard engine source folder ' + $dir + ' was not found. Download the release bundle (Halyard-<version>.zip from https://github.com/fleethacker-data-crucible/Halyard/releases), extract all of it and run Launch-Halyard.bat from it.')
    }
    $dir = (Resolve-Path -LiteralPath $dir).Path
    $files = @(Get-ChildItem -LiteralPath $dir -Filter '*.cs' -Recurse -File |
        Where-Object { $_.FullName.Substring($dir.Length) -notmatch '(?i)[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName | ForEach-Object { $_.FullName })
    if ($files.Count -eq 0) { throw ('No engine source (*.cs) under ' + $dir + '. Extract the whole release bundle again.') }
    return [pscustomobject]@{ Directory = $dir; Files = $files }
}

function Import-HalyardCore {
    <#
    .SYNOPSIS
      Compiles the Halyard engine from source, in memory, and loads it into this session.
    .DESCRIPTION
      The engine ships as C# source (src\Halyard.Core). The workstation's application control blocks loading custom
      DLL files, so nothing is loaded from disk: Windows PowerShell 5.1 compiles the files with Add-Type and the
      inbox .NET Framework 4.8 compiler (C# 5), referencing framework assemblies only. This takes a few seconds per
      launch; the time is kept in Get-HalyardCoreLoadInfo. PowerShell 7 compiles the same files with its own
      compiler. Calling it again in the same session does nothing.
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-HalyardRoot))
    if ($script:HalyardCoreLoaded -or ('Halyard.Core.Facade' -as [type])) { $script:HalyardCoreLoaded = $true; return }
    if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
        throw ('PowerShell is running in ' + $ExecutionContext.SessionState.LanguageMode + ' mode, which does not allow compiling the Halyard engine (Add-Type). Halyard needs FullLanguage mode; ask IT. Do not try to work around the policy.')
    }
    $source = Get-HalyardCoreSource -Root $Root
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if ($PSVersionTable.PSEdition -ne 'Core') {
            # Windows PowerShell 5.1: the inbox CodeDom compiler (C# 5), in memory.
            $cp = New-Object System.CodeDom.Compiler.CompilerParameters
            $cp.GenerateInMemory = $true
            $cp.GenerateExecutable = $false
            $cp.IncludeDebugInformation = $false
            $cp.TreatWarningsAsErrors = $false
            $cp.WarningLevel = 0   # warnings never stop a launch; the C# 5 check build treats them as errors
            $cp.CompilerOptions = '/optimize+'
            [void]$cp.ReferencedAssemblies.AddRange([string[]]$script:HalyardCoreReferences)
            Add-Type -Path $source.Files -CompilerParameters $cp -ErrorAction Stop
        } else {
            # PowerShell 7 compiles against its own .NET reference set (which includes these framework assemblies).
            Add-Type -Path $source.Files -IgnoreWarnings -WarningAction SilentlyContinue -ErrorAction Stop
        }
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '(?i)policy|blocked|application control|0x800711C7|code integrity') {
            throw ('Windows blocked compiling the Halyard engine in memory (' + $msg + '). Halyard ships as source and loads no DLL file; ask IT whether in-memory Add-Type compilation is allowed for standard users. Do not try to work around the policy.')
        }
        throw ('The Halyard engine source under ' + $source.Directory + ' did not compile (' + $msg + '). Extract the release bundle again; if you changed the source, keep it to C# 5 and framework assemblies only.')
    }
    $watch.Stop()
    $script:HalyardCoreLoaded = $true
    $script:HalyardCoreLoad = [pscustomobject]@{
        Source       = $source.Directory
        Files        = $source.Files.Count
        Milliseconds = [int]$watch.Elapsed.TotalMilliseconds
        Compiler     = $(if ($PSVersionTable.PSEdition -ne 'Core') { 'Windows PowerShell ' + $PSVersionTable.PSVersion + ' (CodeDom, C# 5)' } else { 'PowerShell ' + $PSVersionTable.PSVersion })
    }
    Write-Verbose ('Compiled ' + $source.Files.Count + ' engine source files in ' + $script:HalyardCoreLoad.Milliseconds + ' ms')
}

function Get-HalyardCoreLoadInfo {
    <# How this session loaded the engine: source folder, file count, compile time (ms) and compiler; $null before Import-HalyardCore. #>
    [CmdletBinding()]
    param()
    if (-not $script:HalyardCoreLoad -and ('Halyard.Core.Facade' -as [type])) {
        return [pscustomobject]@{ Source = $null; Files = 0; Milliseconds = 0; Compiler = 'already loaded in this session' }
    }
    return $script:HalyardCoreLoad
}

function Get-HalyardGitHubCliToken {
    # Optional, personal machine only: uses an existing `gh auth login` session when the GitHub CLI is installed.
    # The workstation has no gh; there GITHUB_TOKEN (or Lazarette) supplies the token. Never prompts.
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
      Loads config\halyard.env into the process environment, resolves tokens, and returns HalyardSettings.
    .PARAMETER UseLazarette
      Auto (default): unlock Lazarette only for the GitLab provider or when HALYARD_USE_LAZARETTE=1. Always / Never override.
    #>
    [CmdletBinding()]
    param(
        [string]$Root = (Get-HalyardRoot),
        [string]$EnvFile,
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseLazarette = 'Auto'
    )
    Import-HalyardCore -Root $Root
    if (-not $EnvFile) { $EnvFile = Get-HalyardEnvFile -Root $Root }
    $applied = [Halyard.Core.Facade]::LoadEnvFile($EnvFile)
    if ($applied -gt 0) { Write-Verbose ("Applied $applied settings from $EnvFile") }

    $providerSetting = Get-HalyardSetting 'PROVIDER'
    $provider = if ($providerSetting) { $providerSetting.ToLowerInvariant() } else { 'github' }
    $useVault = (Get-HalyardSetting 'USE_LAZARETTE') -eq '1'
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
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseLazarette = 'Auto',
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
    $hostObj.LauncherPath = (Join-Path $Root 'scripts\Start-Halyard.ps1')
    $info = $hostObj.Start()
    Write-Host ''
    Write-Host ('Halyard is running at ' + $info.Url) -ForegroundColor Cyan
    Write-Host ('  provider ' + $settings.Provider.Kind + ', ' + $hostObj.Gateway.Policy.Describe()) -ForegroundColor DarkGray
    $load = Get-HalyardCoreLoadInfo
    if ($load -and $load.Files -gt 0) {
        $compiled = 'Engine compiled from ' + $load.Files + ' source files in ' + $load.Milliseconds + ' ms (' + $load.Compiler + ')'
        Write-Host ('  ' + $compiled) -ForegroundColor DarkGray
        $hostObj.Log.Info('ENGINE_COMPILED', $compiled)
    }
    if ($autoStop) { Write-Host '  It stops by itself when the browser tab closes (--stay keeps it running). "End session" in the browser also stops it.' -ForegroundColor DarkGray }
    else { Write-Host '  Use "End session" in the browser or scripts\Stop-Halyard.ps1 to stop it. Closing this window also stops it.' -ForegroundColor DarkGray }
    if ($NoBrowser) { Write-Host ('  Session link (open once): ' + $info.LaunchUrl) -ForegroundColor DarkGray } else { [void](Open-HalyardWindow -Url $info.LaunchUrl) }
    # The tray icon (Open, Quit) and the signal a second launch sets to bring this copy's window forward.
    $trayState = New-HalyardTrayState
    $trayState.Port = $info.Port
    $trayState.OutputDir = $settings.OutputDirectory
    $tray = $null
    if ($env:HALYARD_TRAY -ne '0') { $tray = Start-HalyardTray -State $trayState -IconPath (Join-Path $Root 'assets\brand\tray.ico') }
    $signal = $null
    try { $signal = New-HalyardOpenSignal -Root $Root } catch { $signal = $null }
    $shortcut = Initialize-HalyardShortcut -Root $Root
    if ($shortcut -and $shortcut.Ok -and $tray) { $trayState.NoticeTitle = 'Halyard'; $trayState.NoticeText = 'A Halyard shortcut is on your desktop.'; $trayState.NoticeId = 'shortcut' }
    $vaultFailed = $envInfo.Lazarette -and -not $envInfo.Lazarette.Unlocked
    if (-not $KeepConsole -and -not $vaultFailed) {
        Start-Sleep -Milliseconds 800
        [void][Halyard.Core.Facade]::SetConsoleVisible($false)
    } elseif ($vaultFailed) {
        Write-Host 'Console kept open because Lazarette did not unlock.' -ForegroundColor Yellow
    }
    try {
        # Poll so Ctrl+C in a visible console still works; act on the tray and on a second launch between polls.
        while (-not $hostObj.WaitForStop(500)) {
            $open = $false
            if ($trayState.OpenRequested) { $trayState.OpenRequested = $false; $open = $true }
            if ($signal) { try { if ($signal.WaitOne(0)) { $open = $true } } catch { } }
            if ($open -and -not (Show-HalyardBrowserWindow)) { [void](Open-HalyardWindow -Url $info.LaunchUrl) }
            if ($trayState.QuitRequested) { $trayState.QuitRequested = $false; $hostObj.Log.Info('SESSION_STOP_REQUESTED', 'Quit from the tray icon.'); $hostObj.Stop() }
        }
    } finally {
        Stop-HalyardTray $tray
        if ($signal) { $signal.Dispose() }
        $hostObj.Dispose()
        [void][Halyard.Core.Facade]::SetConsoleVisible($true)
    }
}

function Test-HalyardConnection {
    [CmdletBinding()]
    param([string]$Root = (Get-HalyardRoot), [ValidateSet('Auto', 'Always', 'Never')][string]$UseLazarette = 'Auto')
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
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseLazarette = 'Auto'
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
        [ValidateSet('Auto', 'Always', 'Never')][string]$UseLazarette = 'Auto'
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
    param([ValidateSet('source', 'sandbox')][string]$Repository = 'source', [int]$Max = 200, [ValidateSet('Auto', 'Always', 'Never')][string]$UseLazarette = 'Auto')
    $s = (Initialize-HalyardEnvironment -UseLazarette $UseLazarette).Settings
    foreach ($w in [Halyard.Core.Facade]::ListWorkItems($s, $Repository, $Max)) {
        [pscustomobject]@{
            Number = $w.Number; Type = $w.Type; Id = $w.CanonicalId; Parent = $w.ParentId; Title = $w.Title; State = $w.State
            Points = if ($w.Meta) { $w.Meta.Points } else { $null }; Labels = ($w.Labels -join ', '); Url = $w.Url
        }
    }
}

function Send-HalyardReportMessage {
    <#
    .SYNOPSIS
      Delivers a rendered report: through the Outlook desktop app (COM), through SMTP (Send-MailMessage), or not at all (File).
    .DESCRIPTION
      Used by scripts\Send-HalyardReport.ps1. Kept in the module so the delivery rules can be tested without a mail server
      or Outlook. Returns an object saying how the report was delivered. Throws, before anything is sent, when the
      recipients or the SMTP settings are missing.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Outlook', 'Smtp', 'File')][string]$Via,
        [Parameter(Mandatory)][string]$Html,
        [Parameter(Mandatory)][string]$Subject,
        [string[]]$To,
        [string]$SmtpServer,
        [string]$From,
        # Outlook only: open the message for a person to review and send, instead of sending it. -To may then be empty.
        [switch]$Draft
    )
    $recipients = @($To | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    switch ($Via) {
        'Outlook' {
            if ($recipients.Count -eq 0 -and -not $Draft) { throw 'Specify -To for Outlook delivery (or -Draft to open the message for review).' }
            $outlook = $null
            try { $outlook = New-Object -ComObject Outlook.Application } catch { throw 'Outlook is not available on this machine (COM automation failed). Use -Via Smtp or -Via File.' }
            $mail = $outlook.CreateItem(0)
            if ($recipients.Count -gt 0) { $mail.To = ($recipients -join ';') }
            $mail.Subject = $Subject
            $mail.HTMLBody = $Html
            if ($Draft) {
                $mail.Display($false)
                return [pscustomobject]@{ Via = 'Outlook'; Sent = $false; Draft = $true; To = $recipients; Server = $null }
            }
            $mail.Send()
            return [pscustomobject]@{ Via = 'Outlook'; Sent = $true; Draft = $false; To = $recipients; Server = $null }
        }
        'Smtp' {
            if ($recipients.Count -eq 0) { throw 'Specify -To for SMTP delivery.' }
            if (-not $SmtpServer -or -not $From) { throw 'Specify -SmtpServer and -From for SMTP delivery.' }
            Send-MailMessage -To $recipients -From $From -Subject $Subject -Body $Html -BodyAsHtml -SmtpServer $SmtpServer -Encoding ([System.Text.Encoding]::UTF8)
            return [pscustomobject]@{ Via = 'Smtp'; Sent = $true; To = $recipients; Server = $SmtpServer }
        }
        default {
            return [pscustomobject]@{ Via = 'File'; Sent = $false; To = @(); Server = $null }
        }
    }
}

function Get-HalyardCommand {
    <# Lists every Halyard command (halyard <noun> <verb>) with its loopback API method and route. -Json prints the stable, versioned document. #>
    [CmdletBinding()]
    param([switch]$Json)
    Import-HalyardCore
    $text = [Halyard.Core.Facade]::CommandsJson()
    if ($Json) { return $text }
    foreach ($c in ($text | ConvertFrom-Json).commands) {
        [pscustomobject]@{ Name = $c.name; Method = $c.method; Route = $c.route; Writes = $c.writes; Async = $c.async; Summary = $c.summary }
    }
}

Export-ModuleMember -Function Send-HalyardOpenSignal, Show-HalyardMessage, Open-HalyardWindow, New-HalyardShortcut, Get-HalyardCommand, Send-HalyardReportMessage, Get-HalyardRoot, Import-HalyardCore, Get-HalyardCoreSource, Get-HalyardCoreLoadInfo, Initialize-HalyardEnvironment, Connect-HalyardLazarette, Start-Halyard, Test-HalyardConnection, New-HalyardSyntheticCatalog, Invoke-HalyardSeed, Invoke-HalyardTeardown, Get-HalyardWorkItem

