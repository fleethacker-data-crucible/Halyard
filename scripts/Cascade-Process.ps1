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
    param([string]$Root)
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
                    $found[[int]$_.ProcessId] = [pscustomobject]@{ Pid = [int]$_.ProcessId; Port = $null; Url = $null; Started = $_.CreationDate }
                }
            }
    } catch { }
    return @($found.Values | Sort-Object Pid)
}

function Stop-CascadeProcesses {
    <#
    .SYNOPSIS
      Stops every running Cascade engine and returns the PIDs stopped. Access-denied (an engine started
      from an elevated window) is reported, not thrown.
    #>
    [CmdletBinding()]
    param([string]$Root)
    $stopped = New-Object System.Collections.Generic.List[int]
    foreach ($p in Get-CascadeProcesses -Root $Root) {
        try {
            Stop-Process -Id $p.Pid -Force -ErrorAction Stop
            $stopped.Add($p.Pid)
        } catch {
            Write-Warning ('Could not stop Cascade engine PID ' + $p.Pid + ': ' + $_.Exception.Message + ' (started from an elevated window? stop it from one.)')
        }
    }
    if ($stopped.Count -gt 0) { Start-Sleep -Milliseconds 700 }  # let the DLL unlock
    return $stopped
}
