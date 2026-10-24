# PowerShell-native, offline-first, deterministic PO workflow runtime.
function Save-CascadeAgentJson {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)]$Value)
 $dir=Split-Path -Parent $Path
 if(-not (Test-Path -LiteralPath $dir)){[void](New-Item -ItemType Directory -Path $dir -Force)}
 $tmp=Join-Path $dir ('.'+[guid]::NewGuid().ToString('N')+'.tmp')
 try {
   $json=ConvertTo-Json -InputObject $Value -Depth 80
   [System.IO.File]::WriteAllText($tmp,$json,(New-Object System.Text.UTF8Encoding($false)))
   Move-Item -LiteralPath $tmp -Destination $Path -Force
 } finally {if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Force}}
}
function Read-CascadeAgentJson {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Path)
 if(-not(Test-Path -LiteralPath $Path)){throw "File not found: $Path"}
 return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Write-CascadeAgentEvent {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$RunId,[Parameter(Mandatory)][string]$Event,[object]$Data)
 $dir=Join-Path $Workspace $RunId
 if(-not(Test-Path -LiteralPath $dir)){[void](New-Item -ItemType Directory -Path $dir -Force)}
 $record=[ordered]@{timestamp=[datetime]::UtcNow.ToString('o');runId=$RunId;event=$Event;data=$Data}
 $line=ConvertTo-Json -InputObject $record -Depth 30 -Compress
 [System.IO.File]::AppendAllText((Join-Path $dir 'events.jsonl'),$line+[Environment]::NewLine,(New-Object System.Text.UTF8Encoding($false)))
}
function Get-CascadeAgentRun {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$RunId)
 if($RunId -notmatch '^[a-zA-Z0-9-]+$'){throw 'Invalid RunId'}
 Read-CascadeAgentJson -Path (Join-Path (Join-Path $Workspace $RunId) 'run.json')
}
function Save-CascadeAgentRun {
 param([string]$Workspace,$Run)
 Save-CascadeAgentJson -Path (Join-Path (Join-Path $Workspace $Run.id) 'run.json') -Value $Run
}
function New-CascadeAgentRun {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$FixturePath,[string]$Goal='Audit and refine backlog')
 $fixture=Read-CascadeAgentJson -Path $FixturePath
 if(-not $fixture.PSObject.Properties['issues']){throw 'Fixture requires issues array'}
 $id=[guid]::NewGuid().ToString('N');$now=[datetime]::UtcNow.ToString('o')
 $run=[pscustomobject]@{schemaVersion=1;id=$id;goal=$Goal;createdAt=$now;updatedAt=$now;status='Planned';sourcePath=[System.IO.Path]::GetFullPath($FixturePath);sourceSha256=(Get-FileHash -LiteralPath $FixturePath -Algorithm SHA256).Hash;actions=@();evaluation=$null}
 Save-CascadeAgentRun -Workspace $Workspace -Run $run
 Write-CascadeAgentEvent -Workspace $Workspace -RunId $id -Event 'run.created' -Data @{goal=$Goal;sourceSha256=$run.sourceSha256}
 return $run
}
function Get-CascadeBacklogFindings {
 [CmdletBinding()]param([Parameter(Mandatory)][object[]]$Issues)
 $findings=(New-Object 'System.Collections.Generic.List[object]')
 foreach($item in $Issues){
   $id=[string]$item.id;$title=[string]$item.title;$body=[string]$item.description
   if([string]::IsNullOrWhiteSpace($title)){$findings.Add([pscustomobject]@{id=$id;code='MISSING_TITLE';severity='High';detail='Work item has no title'})}
   if([string]::IsNullOrWhiteSpace($body)){$findings.Add([pscustomobject]@{id=$id;code='MISSING_DESCRIPTION';severity='Medium';detail='Work item has no description'})}
   elseif($body -notmatch '(?i)acceptance\s+criteria|given\s+.+when\s+.+then\s+'){
     $labels=@($item.labels | ForEach-Object {[string]$_})
     if(@($labels | Where-Object {$_ -match '(?i)story|type::story'}).Count -gt 0){$findings.Add([pscustomobject]@{id=$id;code='MISSING_ACCEPTANCE_CRITERIA';severity='Medium';detail='Story appears to lack acceptance criteria'})}
   }
   if(@($item.labels).Count -eq 0){$findings.Add([pscustomobject]@{id=$id;code='NO_LABELS';severity='Low';detail='Work item has no labels'})}
 }
 return @($findings.ToArray())
}
function New-CascadeAgentPlan {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$RunId)
 $run=Get-CascadeAgentRun -Workspace $Workspace -RunId $RunId
 if($run.status -ne 'Planned'){throw "Cannot plan a run in state $($run.status)"}
 if ($run.sourcePath -and $run.sourceSha256) {
    if ((Get-FileHash -LiteralPath $run.sourcePath -Algorithm SHA256).Hash -ne $run.sourceSha256) { throw 'Source fixture changed since run creation' }
  }
 $source=Read-CascadeAgentJson -Path $run.sourcePath
 $findings=@(Get-CascadeBacklogFindings -Issues @($source.issues))
 $actions=@(foreach($finding in $findings){
   [pscustomobject]@{id=[guid]::NewGuid().ToString('N');tool='CreateRefinementProposal';targetId=$finding.id;finding=$finding.code;reason=$finding.detail;status='Proposed';approval='Pending';effect='LocalProposalOnly';result=$null}
 })
 $run.actions=$actions;$run.status='AwaitingApproval';$run.updatedAt=[datetime]::UtcNow.ToString('o')
 Save-CascadeAgentRun -Workspace $Workspace -Run $run
 Write-CascadeAgentEvent -Workspace $Workspace -RunId $RunId -Event 'plan.created' -Data @{actionCount=$actions.Count;status=$run.status}
 return $run
}
function Set-CascadeAgentApproval {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$RunId,[Parameter(Mandatory)][string]$ActionId,[Parameter(Mandatory)][ValidateSet('Approved','Rejected')][string]$Decision)
 $run=Get-CascadeAgentRun -Workspace $Workspace -RunId $RunId
 if($run.status -ne 'AwaitingApproval'){throw 'Approvals are only accepted before execution'}
 $matches=@($run.actions | Where-Object {$_.id -eq $ActionId})
 if($matches.Count -ne 1){throw 'Unknown action id'}
 if($matches[0].approval -ne 'Pending'){throw 'Action has already been decided'}
 $matches[0].approval=$Decision;$run.updatedAt=[datetime]::UtcNow.ToString('o')
 Save-CascadeAgentRun -Workspace $Workspace -Run $run
 Write-CascadeAgentEvent -Workspace $Workspace -RunId $RunId -Event 'action.reviewed' -Data @{actionId=$ActionId;decision=$Decision}
 return $matches[0]
}
function Invoke-CascadeAgentRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][string]$RunId,
        [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
        [switch]$ApplySandbox
    )
    $run = Get-CascadeAgentRun -Workspace $Workspace -RunId $RunId
    if ($run.status -notin @('AwaitingApproval','Interrupted')) {
        throw ("Cannot execute run in state {0}" -f $run.status)
    }
    $runDir = Join-Path $Workspace $RunId
    if (-not (Test-Path -LiteralPath $runDir)) {
        New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    }

    # Ensure tools available
    $toolsPath = Join-Path $PSScriptRoot 'AgentTools.ps1'
    if (Test-Path -LiteralPath $toolsPath) { . $toolsPath }

    $run.status = 'Running'
    Save-CascadeAgentRun -Workspace $Workspace -Run $run

    $allowed = @(
        'CreateRefinementProposal','WriteReport','RunIntegrity',
        'ScanStoryPoints','SuggestParentLinks','ApplySandboxPoints','PromoteCbr'
    )

    try {
        foreach ($action in $run.actions) {
            if ($action.status -eq 'Completed' -or $action.status -eq 'Skipped') { continue }
            if ($action.approval -ne 'Approved') {
                $action.status = 'Skipped'
                continue
            }

            $toolName = [string]$action.tool
            if ($toolName -notin $allowed) {
                throw ("Unregistered tool: {0}" -f $toolName)
            }

            Write-Host ("  Exec tool {0} ({1})" -f $toolName, $action.finding) -ForegroundColor Cyan

            if ($toolName -eq 'WriteReport') {
                $path = Join-Path $runDir 'agent_report.md'
                $lines = New-Object System.Collections.Generic.List[string]
                [void]$lines.Add('# Agent report')
                [void]$lines.Add('')
                [void]$lines.Add(('- Goal: {0}' -f $run.goal))
                [void]$lines.Add(('- Run: {0}' -f $run.id))
                [void]$lines.Add(('- Version: {0}' -f $(if ($run.scriptVersion) { $run.scriptVersion } else { 'n/a' })))
                [void]$lines.Add('')
                [void]$lines.Add('## Actions')
                foreach ($a2 in $run.actions) {
                    [void]$lines.Add(('- [{0}] {1} | {2} | {3}' -f $a2.status, $a2.tool, $a2.finding, $a2.reason))
                }
                [System.IO.File]::WriteAllText($path, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding $false))
                $action.result = [pscustomobject]@{ path = $path }
                $action.status = 'Completed'
            }
            elseif ($toolName -eq 'RunIntegrity') {
                $action.result = [pscustomobject]@{ note = 'Use host [E] Assess for live integrity' }
                $action.status = 'Completed'
            }
            elseif ($toolName -eq 'ScanStoryPoints') {
                $r = Invoke-CascadeAgentScanStoryPoints -RunDir $runDir -Profile $Profile
                $action.result = $r
                $action.status = 'Completed'
                Write-Host ("    Points file: {0} (count={1})" -f $r.path, $r.count) -ForegroundColor Green
            }
            elseif ($toolName -eq 'SuggestParentLinks') {
                $r = Invoke-CascadeAgentSuggestParents -RunDir $runDir -Profile $Profile
                $action.result = $r
                $action.status = 'Completed'
                Write-Host ("    Parents file: {0} (count={1})" -f $r.path, $r.count) -ForegroundColor Green
            }
            elseif ($toolName -eq 'ApplySandboxPoints') {
                $est = Join-Path $runDir 'points_estimates.json'
                if (-not (Test-Path -LiteralPath $est)) {
                    $action.result = [pscustomobject]@{ note = 'No points_estimates.json - run ScanStoryPoints first' }
                    $action.status = 'Skipped'
                } else {
                    $r = Invoke-CascadeAgentApplySandboxPoints -EstimatesPath $est -DryRun:(-not $ApplySandbox)
                    $action.result = $r
                    $action.status = 'Completed'
                }
            }
            elseif ($toolName -eq 'PromoteCbr') {
                $r = Invoke-CascadeAgentPromoteCbr -Reason $action.reason
                $action.result = $r
                $action.status = 'Completed'
                Write-Host ("    {0}" -f $r.message) -ForegroundColor Yellow
            }
            else {
                # CreateRefinementProposal - local JSON
                $proposal = [ordered]@{
                    schemaVersion = 1
                    actionId = $action.id
                    targetId = $action.targetId
                    finding = $action.finding
                    reason = $action.reason
                    recommendation = 'Review and apply via sandbox tools when ready'
                    requiresHumanReview = $true
                    gitLabWritePerformed = $false
                    aiConnected = $false
                }
                $path = Join-Path $runDir ('proposal-' + $action.id + '.json')
                Save-CascadeAgentJson -Path $path -Value $proposal
                $action.result = [pscustomobject]@{ path = $path }
                $action.status = 'Completed'
            }

            $run.updatedAt = [datetime]::UtcNow.ToString('o')
            Save-CascadeAgentRun -Workspace $Workspace -Run $run
            Write-CascadeAgentEvent -Workspace $Workspace -RunId $RunId -Event 'action.completed' -Data @{
                actionId = $action.id
                tool = $toolName
                status = $action.status
            }
        }
        $run.status = 'Completed'
    } catch {
        $run.status = 'Interrupted'
        Write-CascadeAgentEvent -Workspace $Workspace -RunId $RunId -Event 'run.failed' -Data @{ message = $_.Exception.Message }
        throw
    } finally {
        $run.updatedAt = [datetime]::UtcNow.ToString('o')
        Save-CascadeAgentRun -Workspace $Workspace -Run $run
    }
    Write-CascadeAgentEvent -Workspace $Workspace -RunId $RunId -Event 'run.completed' -Data @{ status = $run.status }
    return $run
}

function Get-CascadeAgentEvaluation {
 [CmdletBinding()]param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$RunId)
 $run=Get-CascadeAgentRun -Workspace $Workspace -RunId $RunId
 $actions=$run.actions
 [pscustomobject]@{runId=$RunId;status=$run.status;total=$actions.Count;completed=@($actions|Where-Object status -eq 'Completed').Count;skipped=@($actions|Where-Object status -eq 'Skipped').Count;pending=@($actions|Where-Object approval -eq 'Pending').Count;remoteWrites=0;sourceSha256=$run.sourceSha256}
}


function Set-CascadeAgentApproveAll {
    param([string]$Workspace, [string]$RunId, [switch]$RejectLow)
    $run = Get-CascadeAgentRun -Workspace $Workspace -RunId $RunId
    foreach ($a in $run.actions) {
        if ($a.approval -eq 'Pending') {
            if ($RejectLow -and ("$($a.finding)" -match 'NO_LABELS')) {
                $a.approval = 'Rejected'
                $a.status = 'Skipped'
            } else {
                $a.approval = 'Approved'
            }
        }
    }
    $run.updatedAt = [datetime]::UtcNow.ToString('o')
    Save-CascadeAgentRun -Workspace $Workspace -Run $run
    return $run
}

function Get-CascadeAgentWorkspace {
    param([string]$Root)
    if (-not $Root) {
        $Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        if (-not $Root) { $Root = (Get-Location).Path }
    }
    $ws = Join-Path $Root 'output\agent_runs'
    if (-not (Test-Path -LiteralPath $ws)) { New-Item -ItemType Directory -Path $ws -Force | Out-Null }
    return $ws
}

function Start-CascadeAgentGoal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Goal,
        [string]$AssessJsonPath = '',
        [string]$Workspace = '',
        [switch]$AutoApprove,
        [switch]$Execute,
        [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
        [switch]$ApplySandbox
    )
    # AgentGoal-v2026-10-21d : AI tools (ScanStoryPoints, SuggestParentLinks) + gated sandbox apply
    if ([string]::IsNullOrWhiteSpace($Workspace)) {
        $Workspace = Get-CascadeAgentWorkspace
    }
    $id = [guid]::NewGuid().ToString('N')
    $now = [datetime]::UtcNow.ToString('o')
    $actionList = New-Object System.Collections.Generic.List[object]
    $g = $Goal.ToLowerInvariant()

    $wantPoints = [bool]($g -match 'point|estimate|sizing|story point')
    $wantDwi    = [bool]($g -match 'dwi|defined work|work instruction')
    $wantParent = [bool]($g -match 'parent|hierarch|link')
    $wantPromote = [bool]($g -match 'promote|cbr|production')

    [void]$actionList.Add([pscustomobject]@{
        id=[guid]::NewGuid().ToString('N'); tool='WriteReport'; targetId='report'
        finding='GOAL_REPORT'; reason=('Report for goal: {0}' -f $Goal)
        status='Proposed'; approval='Pending'; effect='LocalFileOnly'; result=$null
    })

    if ($wantPoints) {
        [void]$actionList.Add([pscustomobject]@{
            id=[guid]::NewGuid().ToString('N'); tool='CreateRefinementProposal'; targetId='goal:points-policy'
            finding='GOAL_POINTS_POLICY'
            reason='Policy: STORY points 1/2/3/5/8 when defined; else TBD. FEATURE uses Effort S/M/L.'
            status='Proposed'; approval='Pending'; effect='LocalProposalOnly'; result=$null
        })
        [void]$actionList.Add([pscustomobject]@{
            id=[guid]::NewGuid().ToString('N'); tool='ScanStoryPoints'; targetId='sandbox:stories'
            finding='GOAL_POINTS_SCAN'
            reason='AI scan sandbox STORYs missing points; write points_estimates.json (local).'
            status='Proposed'; approval='Pending'; effect='AiLocal'; result=$null
        })
        [void]$actionList.Add([pscustomobject]@{
            id=[guid]::NewGuid().ToString('N'); tool='ApplySandboxPoints'; targetId='sandbox:apply-points'
            finding='GOAL_POINTS_APPLY'
            reason='Optional: apply non-TBD estimates to sandbox descriptions (requires ApplySandbox on execute).'
            status='Proposed'; approval='Pending'; effect='SandboxWriteOptional'; result=$null
        })
    }
    if ($wantDwi) {
        [void]$actionList.Add([pscustomobject]@{
            id=[guid]::NewGuid().ToString('N'); tool='CreateRefinementProposal'; targetId='goal:dwi'
            finding='GOAL_DWI'
            reason='DWI: use [F] refine for full DWI drafts; agent records policy here.'
            status='Proposed'; approval='Pending'; effect='LocalProposalOnly'; result=$null
        })
    }
    if ($wantParent -or ($g -match 'missing parent')) {
        [void]$actionList.Add([pscustomobject]@{
            id=[guid]::NewGuid().ToString('N'); tool='SuggestParentLinks'; targetId='sandbox:parents'
            finding='GOAL_PARENT_SCAN'
            reason='AI/heuristic parent suggestions for empty parent meta -> parent_suggestions.json'
            status='Proposed'; approval='Pending'; effect='AiLocal'; result=$null
        })
    }
    if ($wantPromote) {
        [void]$actionList.Add([pscustomobject]@{
            id=[guid]::NewGuid().ToString('N'); tool='PromoteCbr'; targetId='cbr'
            finding='GOAL_PROMOTE'
            reason='CBR promote is human-gated and blocked unless production flags set.'
            status='Proposed'; approval='Pending'; effect='BlockedByDefault'; result=$null
        })
    }

    # If goal is generic, still offer points scan as useful default when assess exists
    if (-not $wantPoints -and -not $wantParent -and -not $wantDwi) {
        [void]$actionList.Add([pscustomobject]@{
            id=[guid]::NewGuid().ToString('N'); tool='ScanStoryPoints'; targetId='sandbox:stories'
            finding='GOAL_POINTS_SCAN'
            reason='Default helpful action: AI story points scan (local file).'
            status='Proposed'; approval='Pending'; effect='AiLocal'; result=$null
        })
    }

    $hasAssess = $false
    if (-not [string]::IsNullOrWhiteSpace($AssessJsonPath) -and (Test-Path -LiteralPath $AssessJsonPath)) {
        $hasAssess = $true
    }

    $sourcePath = ''
    $sourceSha = ''
    if ($hasAssess) {
        $sourcePath = $AssessJsonPath
        try { $sourceSha = [string](Get-FileHash -LiteralPath $AssessJsonPath -Algorithm SHA256).Hash } catch {}
    }

    $actionsArr = $actionList.ToArray()
    $run = New-Object psobject -Property @{
        schemaVersion = 1
        id = $id
        goal = $Goal
        createdAt = $now
        updatedAt = $now
        status = 'AwaitingApproval'
        sourcePath = $sourcePath
        sourceSha256 = $sourceSha
        actions = $actionsArr
        evaluation = $null
        mode = 'goal'
        scriptVersion = 'AgentGoal-v2026-10-21d'
        profile = $Profile
    }

    Save-CascadeAgentRun -Workspace $Workspace -Run $run
    Write-CascadeAgentEvent -Workspace $Workspace -RunId $id -Event 'goal.started' -Data @{
        goal = $Goal
        actions = $actionList.Count
        aiTools = $true
    }

    if ($AutoApprove) {
        $null = Set-CascadeAgentApproveAll -Workspace $Workspace -RunId $id
    }
    if ($Execute) {
        if (-not $AutoApprove) { throw 'Execute requires AutoApprove or prior approvals' }
        return (Invoke-CascadeAgentRun -Workspace $Workspace -RunId $id -Profile $Profile -ApplySandbox:$ApplySandbox)
    }
    return $run
}
