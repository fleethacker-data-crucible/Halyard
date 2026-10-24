#requires -Version 5.1
# Invoke-AgentGoal v2026-10-21d - AI-connected tools after approval
param(
    [string]$Goal = '',
    [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
    [switch]$AutoApprove,
    [switch]$Execute,
    [switch]$ApplySandbox
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
foreach ($extra in @('AgentRuntime.ps1','AgentTools.ps1','GenAI.ps1','GitLab.ps1','WritePolicy.ps1','Schema.ps1','ArcBridge.ps1')) {
    $p = Join-Path $RepoRoot ("src\Cascade\{0}" -f $extra)
    if (Test-Path -LiteralPath $p) { . $p }
}

if (-not $Goal) { $Goal = Read-Host 'Product Owner goal' }
if ([string]::IsNullOrWhiteSpace($Goal)) { Write-Host 'Cancelled.'; return }

$assess = Join-Path $RepoRoot 'output\assess_findings.json'
$ws = Join-Path $RepoRoot 'output\agent_runs'
if (-not (Test-Path -LiteralPath $ws)) { New-Item -ItemType Directory -Path $ws -Force | Out-Null }

Write-Host '=== Agent Goal (v2026-10-21d) ===' -ForegroundColor Cyan
Write-Host 'Plan first. After you approve, tools may call GenAI and write LOCAL files.' -ForegroundColor DarkGray
Write-Host 'Sandbox writes only if you opt in. CBR promote stays blocked by default.' -ForegroundColor DarkGray
Write-Host ("Goal: {0}" -f $Goal) -ForegroundColor Cyan

$assessPath = ''
if (Test-Path -LiteralPath $assess) {
    $assessPath = $assess
    Write-Host ("Using assess pack: {0}" -f $assess) -ForegroundColor DarkGray
} else {
    Write-Host 'No assess_findings.json (optional context).' -ForegroundColor Yellow
}

$run = Start-CascadeAgentGoal -Goal $Goal -AssessJsonPath $assessPath -Workspace $ws -Profile $Profile -AutoApprove:$AutoApprove -Execute:$Execute -ApplySandbox:$ApplySandbox

$actionCount = 0
try { if ($null -ne $run.actions) { $actionCount = $run.actions.Count } } catch {}
Write-Host ("Run id: {0}  status: {1}  actions: {2}" -f $run.id, $run.status, $actionCount) -ForegroundColor Green
Write-Host ("Workspace: {0}\{1}" -f $ws, $run.id)

if (($run.status -eq 'AwaitingApproval') -and (-not $Execute)) {
    Write-Host 'Actions:' -ForegroundColor Cyan
    $i = 0
    foreach ($a in $run.actions) {
        $i++
        Write-Host ("  [{0}] {1} | {2} | {3}" -f $i, $a.tool, $a.finding, $a.reason)
    }
    Write-Host ''
    Write-Host 'Y = approve and EXECUTE tools (GenAI for ScanStoryPoints / SuggestParentLinks; local files).' -ForegroundColor Yellow
    Write-Host 'N = cancel (nothing runs).' -ForegroundColor Yellow
    $ans = Read-Host 'Approve ALL and execute? [Y/N]'
    if ($ans -match '^(Y|y)') {
        $null = Set-CascadeAgentApproveAll -Workspace $ws -RunId $run.id
        $apply = $false
        if ($Goal -match '(?i)point|estimate') {
            $a2 = Read-Host 'Also APPLY non-TBD points to SANDBOX issues? [y/N]'
            if ($a2 -match '^(Y|y)') { $apply = $true }
        }
        Write-Host 'Executing...' -ForegroundColor Cyan
        $run = Invoke-CascadeAgentRun -Workspace $ws -RunId $run.id -Profile $Profile -ApplySandbox:$apply
        $ev = Get-CascadeAgentEvaluation -Workspace $ws -RunId $run.id
        Write-Host ($ev | ConvertTo-Json -Compress)
        Write-Host ("Done. Open: {0}\{1}" -f $ws, $run.id) -ForegroundColor Green
        if (Test-Path (Join-Path $ws "$($run.id)\points_estimates.md")) {
            Write-Host 'See points_estimates.md for AI estimates.' -ForegroundColor Cyan
        }
        if (Test-Path (Join-Path $ws "$($run.id)\parent_suggestions.json")) {
            Write-Host 'See parent_suggestions.json for parent proposals.' -ForegroundColor Cyan
        }
    } else {
        Write-Host 'Cancelled - no tools executed.' -ForegroundColor Yellow
    }
}
