$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src/Cascade/Cascade.psd1') -Force
$temp=Join-Path ([System.IO.Path]::GetTempPath()) ('cascade-agent-test-'+[guid]::NewGuid().ToString('N'))
try {
 $null=New-Item -ItemType Directory -Path $temp -Force
 $fixture=Join-Path $temp 'fixture.json'
 @{issues=@(@{id=1;title='Example';description='';labels=@()})} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fixture -Encoding UTF8
 $run=New-CascadeAgentRun -Workspace $temp -FixturePath $fixture
 $plan=New-CascadeAgentPlan -Workspace $temp -RunId $run.id
 if(@($plan.actions).Count -ne 2){throw 'Expected two findings'}
 $blocked=$false
 try {New-CascadeAgentPlan -Workspace $temp -RunId $run.id | Out-Null}catch{$blocked=$true}
 if(-not $blocked){throw 'Replanning should be blocked'}
 $null=Set-CascadeAgentApproval -Workspace $temp -RunId $run.id -ActionId $plan.actions[0].id -Decision Approved
 $result=Invoke-CascadeAgentRun -Workspace $temp -RunId $run.id
 $evaluation=Get-CascadeAgentEvaluation -Workspace $temp -RunId $run.id
 if($evaluation.completed -ne 1 -or $evaluation.skipped -ne 1 -or $evaluation.remoteWrites -ne 0){throw 'Unexpected evaluation'}
 if(-not(Test-Path -LiteralPath $result.actions[0].result.path)){throw 'Missing proposal'}
 $failed=$false
 try {Invoke-CascadeAgentRun -Workspace $temp -RunId $run.id | Out-Null}catch{$failed=$true}
 if(-not $failed){throw 'Replay should be blocked'}
 Write-Host 'PASS: lifecycle, approval, offline proposal, replay protection'
} finally {Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue}
