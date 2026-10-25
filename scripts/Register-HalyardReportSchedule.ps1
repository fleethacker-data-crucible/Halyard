#requires -Version 5.1
<#
.SYNOPSIS
  Registers (or removes) a weekly Windows scheduled task that runs Send-HalyardReport.ps1 for the current user.
.DESCRIPTION
  No admin rights are needed for a current-user task. The task runs whether or not Halyard's browser session is
  open; it synchronizes the source repositories, renders the leadership dashboard and sends it.
.EXAMPLE
  .\scripts\Register-HalyardReportSchedule.ps1 -To lead@example.org -Day MON -Time 07:30 -Via Outlook
.EXAMPLE
  .\scripts\Register-HalyardReportSchedule.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string[]]$To,
    [ValidateSet('MON', 'TUE', 'WED', 'THU', 'FRI')][string]$Day = 'MON',
    [string]$Time = '07:30',
    [ValidateSet('Outlook', 'Smtp', 'File')][string]$Via = 'Outlook',
    [string]$SmtpServer,
    [string]$From,
    [string]$Subject,
    [string]$TaskName = 'Halyard leadership report',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if ($Remove) {
    & schtasks.exe /Delete /TN $TaskName /F | Out-Null
    Write-Host ('Removed scheduled task "' + $TaskName + '".') -ForegroundColor Green
    return
}
if ($Via -ne 'File' -and -not $To) { throw 'Specify -To (or -Via File).' }
$script = Join-Path $PSScriptRoot 'Send-HalyardReport.ps1'
$args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $script + '"'), '-Sync', '-Via', $Via)
if ($To) { $args += @('-To', ('"' + ($To -join ',') + '"')) }
if ($SmtpServer) { $args += @('-SmtpServer', $SmtpServer) }
if ($From) { $args += @('-From', $From) }
if ($Subject) { $args += @('-Subject', ('"' + $Subject + '"')) }
$command = 'powershell.exe ' + ($args -join ' ')
& schtasks.exe /Create /F /SC WEEKLY /D $Day /ST $Time /TN $TaskName /TR $command | Out-Null
Write-Host ('Scheduled "' + $TaskName + '" every ' + $Day + ' at ' + $Time + ':') -ForegroundColor Green
Write-Host ('  ' + $command) -ForegroundColor DarkGray
Write-Host 'Run it now to test:  schtasks.exe /Run /TN "' + $TaskName + '"'
