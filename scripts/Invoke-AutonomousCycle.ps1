param(
    [Parameter(Mandatory)][string[]]$Roots,
    [int]$SinceDays=30,
    [string]$Workspace='',
    [string]$IssuesFixture='',
    [switch]$UseGitLab
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src\Cascade\Cascade.psd1') -Force
if (-not $Workspace) { $Workspace=Join-Path $root 'output\autonomous' }
Invoke-CascadeAutonomousCycle -Roots $Roots -SinceDays $SinceDays -Workspace $Workspace -IssuesFixture $IssuesFixture -UseGitLab:$UseGitLab | Format-List
