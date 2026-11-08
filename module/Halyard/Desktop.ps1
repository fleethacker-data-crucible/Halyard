#requires -Version 5.1
# =============================================================================
# Halyard on the desktop, the way Lazarette and Semaphore do it: the interface in its own app window (Edge or Chrome
# --app), a tray icon to open or quit it, a second launch that brings the running copy forward instead of starting
# another, a desktop shortcut, and message boxes instead of console prompts (the launcher runs hidden).
# Windows PowerShell 5.1, user context, no admin. Dot-sourced by Halyard.psm1.
# =============================================================================

function Get-HalyardAppBrowser {
    # Edge, then Chrome, in their standard install folders. $null when neither is there.
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA) | Where-Object { $_ }
    foreach ($rel in @('Microsoft\Edge\Application\msedge.exe', 'Google\Chrome\Application\chrome.exe')) {
        foreach ($r in $roots) {
            $p = Join-Path $r $rel
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    return $null
}

function Test-HalyardAppWindowWanted {
    # HALYARD_APP_WINDOW=0 opens a normal browser tab instead of an app window.
    return -not ($env:HALYARD_APP_WINDOW -eq '0' -or $env:HALYARD_APP_WINDOW -eq 'false')
}

function Open-HalyardWindow {
    # Opens the interface in an app window (Edge or Chrome --app), else in the default browser. Returns 'app' or 'tab'.
    param([Parameter(Mandatory)][string]$Url)
    if (Test-HalyardAppWindowWanted) {
        $exe = Get-HalyardAppBrowser
        if ($exe) {
            try { Start-Process -FilePath $exe -ArgumentList @('--app=' + $Url) | Out-Null; return 'app' }
            catch { Write-Verbose ('Could not open an app window: ' + $_.Exception.Message) }
        }
    }
    Start-Process $Url | Out-Null
    return 'tab'
}

function Test-HalyardWindowTitle {
    # The titles the page sets (web/src/app.tsx): "Halyard", or "<Page> · Halyard", alone in an app window or followed by " - <browser>".
    param([string]$Title)
    $dot = [string][char]0xB7
    return ($Title -cmatch ('^([^' + $dot + ']+ ' + $dot + ' )?Halyard( - .*)?$'))
}

function Show-HalyardBrowserWindow {
    # Brings a window already showing Halyard to the front. $true when one was found.
    param([string[]]$ProcessName = @('msedge', 'chrome'))
    $found = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { Test-HalyardWindowTitle $_.MainWindowTitle })
    if ($found.Count -eq 0) { return $false }
    try {
        if (-not ('Halyard.Desktop.NativeWindow' -as [type])) {
            Add-Type -Namespace Halyard.Desktop -Name NativeWindow -MemberDefinition '[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
        }
        $h = $found[0].MainWindowHandle
        if ([Halyard.Desktop.NativeWindow]::IsIconic($h)) { [void][Halyard.Desktop.NativeWindow]::ShowWindow($h, 9) }   # SW_RESTORE
        [void][Halyard.Desktop.NativeWindow]::SetForegroundWindow($h)
        return $true
    } catch { return $false }
}

# ---- second launch ---------------------------------------------------------

function Get-HalyardOpenSignalName {
    # One name per install folder and user session, so two Halyard copies never answer for each other.
    param([Parameter(Mandatory)][string]$Root)
    $key = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/').ToLowerInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $h = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($key)) } finally { $sha.Dispose() }
    return ('Local\Halyard-Open-' + (($h[0..7] | ForEach-Object { $_.ToString('x2') }) -join ''))
}

function New-HalyardOpenSignal {
    # Engine side: the event the next launch sets. Auto-reset.
    param([Parameter(Mandatory)][string]$Root)
    return (New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, (Get-HalyardOpenSignalName -Root $Root)))
}

function Send-HalyardOpenSignal {
    # Launcher side: $true when a running engine took the signal; $false when none listens (none running, or an older build).
    param([Parameter(Mandatory)][string]$Root)
    try {
        $h = [System.Threading.EventWaitHandle]::OpenExisting((Get-HalyardOpenSignalName -Root $Root))
        try { return [bool]$h.Set() } finally { $h.Dispose() }
    } catch { return $false }
}

# ---- messages --------------------------------------------------------------

function Show-HalyardMessage {
    # A message box (the launcher has no visible console). Returns 'Yes', 'No' or 'OK'.
    param([Parameter(Mandatory)][string]$Text, [ValidateSet('Info', 'Warning', 'Error', 'Question')][string]$Kind = 'Info')
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $buttons = if ($Kind -eq 'Question') { [System.Windows.Forms.MessageBoxButtons]::YesNo } else { [System.Windows.Forms.MessageBoxButtons]::OK }
        $icon = switch ($Kind) { 'Warning' { [System.Windows.Forms.MessageBoxIcon]::Warning } 'Error' { [System.Windows.Forms.MessageBoxIcon]::Error } 'Question' { [System.Windows.Forms.MessageBoxIcon]::Question } default { [System.Windows.Forms.MessageBoxIcon]::Information } }
        return ([string][System.Windows.Forms.MessageBox]::Show($Text, 'Halyard', $buttons, $icon))
    } catch {
        Write-Host $Text
        return $(if ($Kind -eq 'Question') { 'No' } else { 'OK' })
    }
}

# ---- desktop shortcut ------------------------------------------------------

function Get-HalyardShortcutPath {
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    if ([string]::IsNullOrEmpty($desktop)) { return '' }
    return (Join-Path $desktop 'Halyard.lnk')
}

function Get-HalyardStateDir {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { [IO.Path]::GetTempPath() }
    return (Join-Path $base 'Halyard')
}

function New-HalyardShortcut {
    # A .lnk on the Desktop that runs Launch-Halyard.bat with the Halyard icon. Returns @{ Ok; Path; Reason }; never throws.
    param([Parameter(Mandatory)][string]$Root)
    $path = Get-HalyardShortcutPath
    if ($path -eq '') { return @{ Ok = $false; Path = ''; Reason = 'No Desktop folder for this account.' } }
    $target = Join-Path $Root 'Launch-Halyard.bat'
    if (-not (Test-Path -LiteralPath $target)) { return @{ Ok = $false; Path = $path; Reason = 'Launch-Halyard.bat was not found.' } }
    $shell = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($path)
        $lnk.TargetPath = $target
        $lnk.WorkingDirectory = $Root
        $lnk.Description = 'Halyard: your backlog, up to leadership and down to tasks'
        $icon = Join-Path $Root 'assets\brand\favicon.ico'
        if (Test-Path -LiteralPath $icon) { $lnk.IconLocation = $icon + ',0' }
        $lnk.WindowStyle = 7   # minimised; the app window is the interface
        $lnk.Save()
        return @{ Ok = $true; Path = $path; Reason = '' }
    } catch {
        return @{ Ok = $false; Path = $path; Reason = ('Could not create the shortcut: ' + $_.Exception.GetType().Name) }
    } finally {
        if ($shell) { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) } catch { } }
    }
}

function Initialize-HalyardShortcut {
    # Makes the desktop shortcut once, on the first launch from this folder. If it is deleted later it stays deleted.
    param([Parameter(Mandatory)][string]$Root)
    if ($env:HALYARD_SHORTCUT -eq '0') { return $null }
    $dir = Get-HalyardStateDir
    $marker = Join-Path $dir 'shortcut-offered.txt'
    if (Test-Path -LiteralPath $marker) { return $null }
    $r = New-HalyardShortcut -Root $Root
    try { New-Item -ItemType Directory -Force -Path $dir | Out-Null; Set-Content -LiteralPath $marker -Value ((Get-Date).ToString('s') + ' ' + $Root) -Encoding UTF8 } catch { }
    return $r
}

# ---- tray icon -------------------------------------------------------------

$script:HalyardTrayScript = @'
param($State, $IconPath)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $icon = $null
    if ($IconPath -and (Test-Path -LiteralPath $IconPath)) { try { $icon = New-Object System.Drawing.Icon($IconPath, [System.Windows.Forms.SystemInformation]::SmallIconSize) } catch { $icon = $null } }
    if ($null -eq $icon) { $icon = [System.Drawing.SystemIcons]::Application }

    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $open = $menu.Items.Add('&Open Halyard')
    $open.Font = New-Object System.Drawing.Font($open.Font, [System.Drawing.FontStyle]::Bold)
    $status = $menu.Items.Add('Starting...')
    $status.Enabled = $false
    $folder = $menu.Items.Add('Open the &output folder')
    [void]$menu.Items.Add('-')
    $quit = $menu.Items.Add('&Quit Halyard')

    $ni = New-Object System.Windows.Forms.NotifyIcon
    $ni.Icon = $icon
    $ni.Text = 'Halyard - starting'
    $ni.ContextMenuStrip = $menu
    $ni.Visible = $true

    $open.add_Click({ $State.OpenRequested = $true }.GetNewClosure())
    $ni.add_DoubleClick({ $State.OpenRequested = $true }.GetNewClosure())
    $ni.add_BalloonTipClicked({ $State.OpenRequested = $true }.GetNewClosure())
    $folder.add_Click({ if ($State.OutputDir) { try { Start-Process -FilePath 'explorer.exe' -ArgumentList @([string]$State.OutputDir) } catch { } } }.GetNewClosure())
    $quit.add_Click({
        $r = [System.Windows.Forms.MessageBox]::Show('Quit Halyard? The engine stops and the window stops working. Snapshots and proposals stay on disk.', 'Halyard', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question, [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
        if ($r -eq [System.Windows.Forms.DialogResult]::Yes) { $State.QuitRequested = $true }
    }.GetNewClosure())

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 500
    $timer.add_Tick({
        if ($State.Stopped) { $timer.Stop(); [System.Windows.Forms.Application]::ExitThread(); return }
        $port = [int]$State.Port
        $text = if ($port -le 0) { 'Halyard - starting' } else { 'Halyard - running' }
        if ($ni.Text -ne $text) { $ni.Text = $text }
        $line = if ($port -le 0) { 'Starting...' } else { 'Running on 127.0.0.1:' + $port }
        if ($status.Text -ne $line) { $status.Text = $line }
        $nid = [string]$State.NoticeId
        if ($nid -and $nid -ne [string]$State.NoticeShown) {
            $State.NoticeShown = $nid
            try { $ni.ShowBalloonTip(8000, [string]$State.NoticeTitle, [string]$State.NoticeText, [System.Windows.Forms.ToolTipIcon]::Info) } catch { }
        }
    }.GetNewClosure())
    $timer.Start()
    $State.Ready = $true
    [System.Windows.Forms.Application]::Run()
    $timer.Dispose()
    $ni.Visible = $false
    $ni.Dispose()
    $menu.Dispose()
} catch {
    $State.Error = $_.Exception.Message
    $State.Ready = $false
    $State.Stopped = $true
}
'@

function New-HalyardTrayState {
    return [hashtable]::Synchronized(@{ Ready = $false; Stopped = $false; Error = ''; Port = 0; OutputDir = ''; OpenRequested = $false; QuitRequested = $false; NoticeId = ''; NoticeShown = ''; NoticeTitle = ''; NoticeText = '' })
}

function Start-HalyardTray {
    # The icon on its own STA thread. Returns a handle for Stop-HalyardTray, or $null when no icon could be shown
    # (no desktop session, WinForms blocked); Halyard then still stops with its window, so nothing lingers unseen.
    param([Parameter(Mandatory)]$State, [string]$IconPath = '')
    try {
        $rs = [runspacefactory]::CreateRunspace()
        $rs.ApartmentState = [System.Threading.ApartmentState]::STA
        $rs.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
        $rs.Open()
        $ps = [powershell]::Create()
        $ps.Runspace = $rs
        [void]$ps.AddScript($script:HalyardTrayScript).AddArgument($State).AddArgument($IconPath)
        $async = $ps.BeginInvoke()
        $deadline = (Get-Date).AddSeconds(10)
        while (-not $State.Ready -and -not $State.Stopped -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (-not $State.Ready) { try { $ps.Stop() } catch { }; $ps.Dispose(); $rs.Dispose(); return $null }
        return [pscustomobject]@{ PowerShell = $ps; Runspace = $rs; Async = $async; State = $State }
    } catch { return $null }
}

function Stop-HalyardTray {
    param($Tray)
    if (-not $Tray) { return }
    $Tray.State.Stopped = $true
    try { [void]$Tray.Async.AsyncWaitHandle.WaitOne(3000) } catch { }
    try { $Tray.PowerShell.Dispose() } catch { }
    try { $Tray.Runspace.Dispose() } catch { }
}
