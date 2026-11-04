#requires -Version 5.1
<#
.SYNOPSIS
  Builds the leadership dashboard (HTML) from the latest snapshot and emails it, or saves it to a file.
.DESCRIPTION
  Reads config\halyard.env, optionally takes a fresh snapshot of the configured source repositories, renders
  the one-page leadership view (feature % complete, velocity, ready backlog, capacity, blockers) and delivers it:
    -Via Outlook   through the Outlook desktop app (COM; no SMTP settings needed, works on a workstation).
                   Add -Draft to open the message in Outlook for review instead of sending it.
    -Via Smtp      through Send-MailMessage with -SmtpServer / -From
    -Via Webhook   posts a short text summary to the Teams/Slack webhook in HALYARD_REPORT_WEBHOOK (the HTML is still saved)
    -Via File      saved only (default). The HTML is always saved under output\reports\.
  Schedule it weekly with scripts\Register-HalyardReportSchedule.ps1.
.EXAMPLE
  .\scripts\Send-HalyardReport.ps1 -Sync -Via Outlook -To lead@example.org -Subject "Platform weekly"
.EXAMPLE
  .\scripts\Send-HalyardReport.ps1 -Via Outlook -Draft
  Opens the dashboard as a new Outlook message; a person adds recipients and presses Send.
.EXAMPLE
  .\scripts\Send-HalyardReport.ps1 -Via File
#>
[CmdletBinding()]
param(
    [string[]]$To,
    [string]$Subject,
    [ValidateSet('Outlook', 'Smtp', 'File', 'Webhook')][string]$Via = 'File',
    [string]$SmtpServer,
    [string]$From,
    [string]$Title = 'Halyard Leadership Dashboard',
    [string]$LabelFilter,
    [switch]$Sync,
    [ValidateSet('source', 'sandbox')][string]$SyncRepository = 'source',
    [string]$OutFile,
    [switch]$NoLazarette,
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\module\Halyard\Halyard.psd1') -Force
$env = Initialize-HalyardEnvironment -UseLazarette $(if ($NoLazarette) { 'Never' } else { 'Auto' })
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

if ($Via -eq 'Webhook') {
    [void][Halyard.Core.Facade]::NotifyWebhook($settings, $LabelFilter, $Title, $false)
    Write-Host 'Summary posted to the configured webhook.' -ForegroundColor Green
    return $OutFile
}
if ($Draft -and $Via -ne 'Outlook') { throw '-Draft works with -Via Outlook only.' }
$delivery = Send-HalyardReportMessage -Via $Via -Html $html -Subject $Subject -To $To -SmtpServer $SmtpServer -From $From -Draft:$Draft
switch ($delivery.Via) {
    'Outlook' { if ($delivery.Draft) { Write-Host 'Opened in Outlook for review; nothing was sent.' -ForegroundColor Green } else { Write-Host ('Sent through Outlook to ' + ($delivery.To -join ', ')) -ForegroundColor Green } }
    'Smtp' { Write-Host ('Sent through ' + $delivery.Server + ' to ' + ($delivery.To -join ', ')) -ForegroundColor Green }
    default { }
}
return $OutFile
