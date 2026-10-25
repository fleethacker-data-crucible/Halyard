#requires -Version 5.1
<#
.SYNOPSIS
  Builds the leadership dashboard (HTML) from the latest snapshot and emails it, or saves it to a file.
.DESCRIPTION
  Reads config\halyard.env, optionally takes a fresh snapshot of the configured source repositories, renders
  the one-page leadership view (feature % complete, velocity, ready backlog, capacity, blockers) and delivers it:
    -Via Outlook   through the Outlook desktop app (COM; no SMTP settings needed, works on a workstation)
    -Via Smtp      through Send-MailMessage with -SmtpServer / -From
    -Via File      saved only (default). The HTML is always saved under output\reports\.
  Schedule it weekly with scripts\Register-HalyardReportSchedule.ps1.
.EXAMPLE
  .\scripts\Send-HalyardReport.ps1 -Sync -Via Outlook -To lead@example.org -Subject "Platform weekly"
.EXAMPLE
  .\scripts\Send-HalyardReport.ps1 -Via File
#>
[CmdletBinding()]
param(
    [string[]]$To,
    [string]$Subject,
    [ValidateSet('Outlook', 'Smtp', 'File')][string]$Via = 'File',
    [string]$SmtpServer,
    [string]$From,
    [string]$Title = 'Halyard Leadership Dashboard',
    [string]$LabelFilter,
    [switch]$Sync,
    [ValidateSet('source', 'sandbox')][string]$SyncRepository = 'source',
    [string]$OutFile,
    [switch]$NoArc
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\module\Halyard\Halyard.psd1') -Force
$env = Initialize-HalyardEnvironment -UseArc $(if ($NoArc) { 'Never' } else { 'Auto' })
$settings = $env.Settings

if ($Sync) {
    Write-Host ('Synchronizing ' + $SyncRepository + ' ...') -ForegroundColor DarkGray
    $snap = [Halyard.Core.Facade]::SyncSnapshot($settings, $SyncRepository, $true)
    Write-Host ('Snapshot ' + $snap.Id + ' with ' + $snap.Items.Count + ' items from ' + $snap.Repository) -ForegroundColor DarkGray
}

$html = [Halyard.Core.Facade]::LeadershipReportHtml($settings, $LabelFilter, $Title)
$reports = $settings.ReportsDirectory
New-Item -ItemType Directory -Force $reports | Out-Null
if (-not $OutFile) { $OutFile = Join-Path $reports ('leadership-' + (Get-Date -Format 'yyyyMMdd-HHmm') + '.html') }
[IO.File]::WriteAllText($OutFile, $html, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ('Report saved: ' + $OutFile) -ForegroundColor Green
if (-not $Subject) { $Subject = $Title + ' - ' + (Get-Date -Format 'dd MMM yyyy') }

switch ($Via) {
    'Outlook' {
        if (-not $To) { throw 'Specify -To for Outlook delivery.' }
        $outlook = $null
        try { $outlook = New-Object -ComObject Outlook.Application } catch { throw 'Outlook is not available on this machine (COM automation failed). Use -Via Smtp or -Via File.' }
        $mail = $outlook.CreateItem(0)
        $mail.To = ($To -join ';')
        $mail.Subject = $Subject
        $mail.HTMLBody = $html
        $mail.Send()
        Write-Host ('Sent through Outlook to ' + ($To -join ', ')) -ForegroundColor Green
    }
    'Smtp' {
        if (-not $To) { throw 'Specify -To for SMTP delivery.' }
        if (-not $SmtpServer -or -not $From) { throw 'Specify -SmtpServer and -From for SMTP delivery.' }
        Send-MailMessage -To $To -From $From -Subject $Subject -Body $html -BodyAsHtml -SmtpServer $SmtpServer -Encoding ([System.Text.Encoding]::UTF8)
        Write-Host ('Sent through ' + $SmtpServer + ' to ' + ($To -join ', ')) -ForegroundColor Green
    }
    default { }
}
return $OutFile
