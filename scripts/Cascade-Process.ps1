#requires -Version 5.1
# Dot-sourced helpers for finding and stopping running Cascade engines. Used by Stop-Cascade.ps1,
# Build-Cascade.ps1 and Start-Cascade.ps1 (the single-instance guard).

function Get-CascadeProcesses {
    <#
    .SYNOPSIS
      Running Cascade engines: PID, port and URL, from the runtime files under logs\ plus any
      PowerShell whose command line runs Start-Cascade.ps1. Stale runtime files are removed.
    #>
    [CmdletBinding()]
    param([string]$Root, [switch]$IncludeHolders)
    $found = @{}
    $logs = Join-Path $Root 'logs'
    if (Test-Path $logs) {
        foreach ($f in Get-ChildItem -Path $logs -Filter 'cascade-server-*.json' -ErrorAction SilentlyContinue) {
            try {
                $info = Get-Content -Path $f.FullName -Raw | ConvertFrom-Json
                $proc = Get-Process -Id ([int]$info.pid) -ErrorAction SilentlyContinue
                if ($proc -and $proc.ProcessName -match '^(powershell|pwsh)$') {
                    $found[[int]$info.pid] = [pscustomobject]@{ Pid = [int]$info.pid; Port = $info.port; Url = $info.url; Started = $info.started }
                } else {
                    Remove-Item -Path $f.FullName -Force -ErrorAction SilentlyContinue
                }
            } catch { Remove-Item -Path $f.FullName -Force -ErrorAction SilentlyContinue }
        }
    }
    try {
        $mine = $PID
        Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessId -ne $mine -and $_.CommandLine -and $_.CommandLine -match 'Start-Cascade\.ps1' } |
            ForEach-Object {
                if (-not $found.ContainsKey([int]$_.ProcessId)) {
                    $found[[int]$_.ProcessId] = [pscustomobject]@{ Pid = [int]$_.ProcessId; Port = $null; Url = $null; Started = $_.CreationDate; Kind = 'engine' }
                }
            }
    } catch { }
    foreach ($v in $found.Values) { if (-not $v.PSObject.Properties['Kind']) { $v | Add-Member -NotePropertyName Kind -NotePropertyValue 'engine' } }
    # Any other process that loaded Cascade.Core.dll (a seed, teardown, report or test run left open) locks the DLL too.
    # Process.Modules does not list .NET-mapped assemblies, so ask the Windows Restart Manager, which also sees
    # elevated holders from a normal window.
    if ($IncludeHolders) {
        foreach ($dll in Get-CascadeDllPaths -Root $Root) {
            foreach ($h in Get-FileHolders -Path $dll) {
                if ($h.Pid -eq $PID -or $found.ContainsKey($h.Pid)) { continue }
                $found[$h.Pid] = [pscustomobject]@{ Pid = $h.Pid; Port = $null; Url = $null; Started = $h.Started; Kind = 'holder'; Name = $h.Name }
            }
        }
    }
    return @($found.Values | Sort-Object Pid)
}

function Get-CascadeDllPaths {
    [CmdletBinding()]
    param([string]$Root)
    $paths = @()
    foreach ($rel in @('src\Cascade.Core\bin\Release\net48\Cascade.Core.dll', 'src\Cascade.Core\bin\Debug\net48\Cascade.Core.dll', 'dist\Cascade\lib\Cascade.Core.dll', 'lib\Cascade.Core.dll')) {
        $p = Join-Path $Root $rel
        if (Test-Path $p) { $paths += (Resolve-Path $p).Path }
    }
    return $paths
}

function Get-FileHolders {
    <#
    .SYNOPSIS
      Processes that have the file open or mapped, via the Windows Restart Manager (rstrtmgr.dll).
      Returns objects with Pid, Name and Started. Empty on any failure, so callers degrade gracefully.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not ('Cascade.RestartManager' -as [type])) {
        Add-Type -Namespace Cascade -Name RestartManager -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct RM_PROCESS_INFO {
    public RM_UNIQUE_PROCESS Process;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
    public int ApplicationType; public uint AppStatus; public uint TSSessionId; [MarshalAs(UnmanagedType.Bool)] public bool bRestartable; }
[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
[DllImport("rstrtmgr.dll")] public static extern int RmEndSession(uint pSessionHandle);
[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] public static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
[DllImport("rstrtmgr.dll")] public static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);
public static int[] Holders(string path) {
    uint handle; var key = Guid.NewGuid().ToString();
    if (RmStartSession(out handle, 0, key) != 0) return new int[0];
    try {
        if (RmRegisterResources(handle, 1, new[] { path }, 0, null, 0, null) != 0) return new int[0];
        uint needed = 0, count = 0, reasons = 0;
        int rc = RmGetList(handle, out needed, ref count, null, ref reasons);
        if (rc == 0 && needed == 0) return new int[0];
        if (rc != 234 && rc != 0) return new int[0]; // 234 = ERROR_MORE_DATA
        var info = new RM_PROCESS_INFO[needed]; count = needed;
        if (RmGetList(handle, out needed, ref count, info, ref reasons) != 0) return new int[0];
        var ids = new int[count]; for (int i = 0; i < count; i++) ids[i] = info[i].Process.dwProcessId; return ids;
    } finally { RmEndSession(handle); }
}
'@ -ErrorAction Stop
    }
    $out = @()
    try {
        foreach ($id in [Cascade.RestartManager]::Holders($Path)) {
            $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
            $started = $null; try { if ($proc) { $started = $proc.StartTime } } catch { }
            $out += [pscustomobject]@{ Pid = [int]$id; Name = $(if ($proc) { $proc.ProcessName } else { '?' }); Started = $started }
        }
    } catch { }
    return $out
}

function Stop-CascadeProcesses {
    <#
    .SYNOPSIS
      Stops every running Cascade engine (and, with -IncludeHolders, every other PowerShell that has
      Cascade.Core.dll loaded) and returns the PIDs stopped. Access-denied (started from an elevated
      window) is reported, not thrown.
    #>
    [CmdletBinding()]
    param([string]$Root, [switch]$IncludeHolders, [switch]$NoElevate)
    $stopped = New-Object System.Collections.Generic.List[int]
    $denied = New-Object System.Collections.Generic.List[int]
    foreach ($p in Get-CascadeProcesses -Root $Root -IncludeHolders:$IncludeHolders) {
        $what = if ($p.Kind -eq 'holder') { ('process holding Cascade.Core.dll (' + $p.Name + ')') } else { 'Cascade engine' }
        try {
            Stop-Process -Id $p.Pid -Force -ErrorAction Stop
            $stopped.Add($p.Pid)
        } catch {
            if ($_.Exception.Message -match 'denied') { $denied.Add($p.Pid) }
            else { Write-Warning ('Could not stop ' + $what + ' PID ' + $p.Pid + ': ' + $_.Exception.Message) }
        }
    }
    if ($denied.Count -gt 0 -and -not $NoElevate) {
        # Elevated holders (an admin PowerShell that imported the module) need an elevated stop: one UAC prompt.
        Write-Host ('PID ' + ($denied -join ', ') + ' run elevated; asking Windows for permission to stop them (UAC prompt).') -ForegroundColor Yellow
        try {
            $cmd = 'Stop-Process -Id ' + ($denied -join ',') + ' -Force -ErrorAction SilentlyContinue'
            Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-WindowStyle', 'Hidden', '-Command', $cmd) -Verb RunAs -Wait -ErrorAction Stop
            foreach ($id in $denied) { if (-not (Get-Process -Id $id -ErrorAction SilentlyContinue)) { $stopped.Add($id) } }
        } catch {
            Write-Warning ('Elevation was declined or failed; PID ' + ($denied -join ', ') + ' still hold the DLL. Close those windows or stop them from an administrator PowerShell.')
        }
    } elseif ($denied.Count -gt 0) {
        Write-Warning ('PID ' + ($denied -join ', ') + ' run elevated and were not stopped. Close those windows or stop them from an administrator PowerShell.')
    }
    if ($stopped.Count -gt 0) { Start-Sleep -Milliseconds 700 }  # let the DLL unlock
    return $stopped
}
