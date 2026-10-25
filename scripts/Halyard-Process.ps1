#requires -Version 5.1
# Dot-sourced helpers for finding and stopping running Halyard engines. Used by Stop-Halyard.ps1,
# Build-Halyard.ps1 and Start-Halyard.ps1 (the single-instance guard).

function Get-HalyardProcesses {
    <#
    .SYNOPSIS
      Running Halyard engines: PID, port and URL, from the runtime files under logs\ plus any
      PowerShell whose command line runs Start-Halyard.ps1. Stale runtime files are removed.
    #>
    [CmdletBinding()]
    param([string]$Root, [switch]$IncludeHolders)
    $found = @{}
    $logs = Join-Path $Root 'logs'
    if (Test-Path $logs) {
        foreach ($f in Get-ChildItem -Path $logs -Filter 'halyard-server-*.json' -ErrorAction SilentlyContinue) {
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
            Where-Object { $_.ProcessId -ne $mine -and $_.CommandLine -and $_.CommandLine -match 'Start-Halyard\.ps1' } |
            ForEach-Object {
                if (-not $found.ContainsKey([int]$_.ProcessId)) {
                    $found[[int]$_.ProcessId] = [pscustomobject]@{ Pid = [int]$_.ProcessId; Port = $null; Url = $null; Started = $_.CreationDate; Kind = 'engine' }
                }
            }
    } catch { }
    foreach ($v in $found.Values) { if (-not $v.PSObject.Properties['Kind']) { $v | Add-Member -NotePropertyName Kind -NotePropertyValue 'engine' } }
    # Any other process that loaded Halyard.Core.dll (a seed, teardown, report or test run left open) locks the DLL too.
    # Process.Modules does not list .NET-mapped assemblies, so ask Windows which processes have the file open
    # (Get-FileHolders), which needs no admin and also sees elevated holders from a normal window.
    if ($IncludeHolders) {
        foreach ($dll in Get-HalyardDllPaths -Root $Root) {
            foreach ($h in Get-FileHolders -Path $dll) {
                if ($h.Pid -eq $PID -or $found.ContainsKey($h.Pid)) { continue }
                $found[$h.Pid] = [pscustomobject]@{ Pid = $h.Pid; Port = $null; Url = $null; Started = $h.Started; Kind = 'holder'; Name = $h.Name }
            }
        }
    }
    return @($found.Values | Sort-Object Pid)
}

function Get-HalyardDllPaths {
    [CmdletBinding()]
    param([string]$Root)
    $paths = @()
    foreach ($rel in @('src\Halyard.Core\bin\Release\net48\Halyard.Core.dll', 'src\Halyard.Core\bin\Debug\net48\Halyard.Core.dll', 'dist\Halyard\lib\Halyard.Core.dll', 'lib\Halyard.Core.dll')) {
        $p = Join-Path $Root $rel
        if (Test-Path $p) { $paths += (Resolve-Path $p).Path }
    }
    return $paths
}

function Get-FileHolders {
    <#
    .SYNOPSIS
      Processes that have the file open or mapped, via NtQueryInformationFile with
      FileProcessIdsUsingFileInformation (class 47). Needs only FILE_READ_ATTRIBUTES on the file, so it
      works without admin in any session (including a standard-user headless runner in session 0), and
      it also reports elevated holders from a normal window.
      Returns objects with Pid, Name and Started. A file that does not exist has no holders (empty, no
      warning). Any other failure (the file cannot be opened, the query fails, or the helper cannot be
      compiled) is reported with Write-Warning naming the path and the error code, and returns empty
      instead of throwing, so callers degrade gracefully.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not ('Halyard.FileHolderQuery' -as [type])) {
        try {
            Add-Type -Namespace Halyard -Name FileHolderQuery -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
[StructLayout(LayoutKind.Sequential)] public struct IO_STATUS_BLOCK { public IntPtr Status; public IntPtr Information; }
[DllImport("ntdll.dll")] public static extern int NtQueryInformationFile(Microsoft.Win32.SafeHandles.SafeFileHandle FileHandle, out IO_STATUS_BLOCK IoStatusBlock, IntPtr FileInformation, int Length, int FileInformationClass);
// Process IDs that have the file open or mapped. Returns null and sets error when the file cannot be
// opened or the query fails; returns an empty array when the file does not exist (nothing can hold it).
public static int[] Holders(string path, out string error) {
    error = null;
    // FILE_READ_ATTRIBUTES (0x80), share read/write/delete (7), OPEN_EXISTING (3)
    using (var h = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
        if (h.IsInvalid) {
            int err = Marshal.GetLastWin32Error();
            if (err == 2 || err == 3) return new int[0]; // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
            error = "CreateFileW failed with Win32 error " + err + " (" + new System.ComponentModel.Win32Exception(err).Message + ")";
            return null;
        }
        int len = 4096;
        while (true) {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try {
                IO_STATUS_BLOCK io;
                int st = NtQueryInformationFile(h, out io, buf, len, 47); // FileProcessIdsUsingFileInformation
                if (st == unchecked((int)0xC0000004) || st == unchecked((int)0x80000005)) { // STATUS_INFO_LENGTH_MISMATCH, STATUS_BUFFER_OVERFLOW
                    if (len >= (1 << 24)) { error = "NtQueryInformationFile(FileProcessIdsUsingFileInformation) returned NTSTATUS 0x" + st.ToString("X8") + " at a 16 MB buffer"; return null; }
                    len *= 2; continue;
                }
                if (st != 0) { error = "NtQueryInformationFile(FileProcessIdsUsingFileInformation) returned NTSTATUS 0x" + st.ToString("X8"); return null; }
                // FILE_PROCESS_IDS_USING_FILE_INFORMATION: ULONG count, then ULONG_PTR ids (aligned to pointer size)
                int n = Marshal.ReadInt32(buf);
                if (n < 0 || (long)(n + 1) * IntPtr.Size > len) { error = "NtQueryInformationFile(FileProcessIdsUsingFileInformation) returned an invalid count " + n; return null; }
                var ids = new int[n];
                for (int i = 0; i < n; i++) ids[i] = (int)Marshal.ReadIntPtr(buf, IntPtr.Size * (i + 1)).ToInt64();
                return ids;
            } finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
'@ -ErrorAction Stop
        } catch {
            Write-Warning ('Get-FileHolders: cannot list processes holding ' + $Path + ': compiling the file-holder helper failed: ' + $_.Exception.Message)
            return @()
        }
    }
    $out = @()
    $err = $null
    $ids = $null
    try { $ids = [Halyard.FileHolderQuery]::Holders($Path, [ref]$err) }
    catch { $err = $_.Exception.Message }
    if ($null -ne $err) {
        Write-Warning ('Get-FileHolders: cannot list processes holding ' + $Path + ': ' + $err)
        return @()
    }
    try {
        foreach ($id in $ids) {
            $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
            $started = $null; try { if ($proc) { $started = $proc.StartTime } } catch { }
            $out += [pscustomobject]@{ Pid = [int]$id; Name = $(if ($proc) { $proc.ProcessName } else { '?' }); Started = $started }
        }
    } catch { }
    return $out
}

function Stop-HalyardProcesses {
    <#
    .SYNOPSIS
      Stops every running Halyard engine (and, with -IncludeHolders, every other PowerShell that has
      Halyard.Core.dll loaded) and returns the PIDs stopped. Access-denied (started from an elevated
      window) is reported, not thrown.
    #>
    [CmdletBinding()]
    param([string]$Root, [switch]$IncludeHolders, [switch]$NoElevate)
    $stopped = New-Object System.Collections.Generic.List[int]
    $denied = New-Object System.Collections.Generic.List[int]
    foreach ($p in Get-HalyardProcesses -Root $Root -IncludeHolders:$IncludeHolders) {
        $what = if ($p.Kind -eq 'holder') { ('process holding Halyard.Core.dll (' + $p.Name + ')') } else { 'Halyard engine' }
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
