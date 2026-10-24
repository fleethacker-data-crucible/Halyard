#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src/Cascade/Cascade.psd1') -Force
$module=Get-Module Cascade
if(-not $module){throw 'Cascade module unavailable.'}
# Replace module-local transport functions with synthetic read-only responses.
# This checks URL encoding, page-cap supplementation and dedup without CAC.
& $module {
    $script:LabelTestPaths=New-Object System.Collections.ArrayList
    Set-Item -Path Function:script:Initialize-CascadeGitMilSession -Value {
        return [pscustomobject]@{ArcLoaded=$true;GitLabHost='web.git.mil';GitLabTokenReady=$true}
    }
    Set-Item -Path Function:script:Get-CascadeGitLabApiRoot -Value {return 'https://web.git.mil/api/v4'}
    Set-Item -Path Function:script:Get-CascadeGitLabHeaders -Value {return @{Authorization='TEST-ONLY'}}
    Set-Item -Path Function:script:Invoke-CascadeGitLabApi -Value {
        param([string]$Uri,$Headers)
        if($Uri.EndsWith('/user')){return [pscustomobject]@{id=10;username='offline';name='Mock'}}
        if($Uri.EndsWith('/projects/16629')){return [pscustomobject]@{id=16629;name='Synthetic CBR';path_with_namespace='demo/cbr';web_url='https://web.git.mil/demo/cbr';default_branch='main'}}
        throw 'Unexpected API path: '+$Uri
    }
    Set-Item -Path Function:script:Invoke-CascadeGitMilPaged -Value {
        param([string]$Path,[int]$MaxPages=10,[int]$PerPage=100)
        [void]$script:LabelTestPaths.Add($Path)
        if($Path -eq '/projects/16629/issues?state=all'){
            return @(
                [pscustomobject]@{id=10;iid=1;title='Other team';description='';state='opened';labels=@('team::Other');web_url='https://web.git.mil/demo/1'},
                [pscustomobject]@{id=20;iid=2;title='Existing team';description='';state='opened';labels=@('team::Data-Crucible');web_url='https://web.git.mil/demo/2'}
            )
        }
        if($Path.StartsWith('/projects/16629/issues?state=all&labels=')){
            return @(
                [pscustomobject]@{id=20;iid=2;title='Existing team';description='';state='opened';labels=@('team::Data-Crucible');web_url='https://web.git.mil/demo/2'},
                [pscustomobject]@{id=9990;iid=999;title='Older labeled issue beyond broad page cap';description='';state='opened';labels=@('team::Data-Crucible');web_url='https://web.git.mil/demo/999'},
                [pscustomobject]@{id=1234;iid=345;title='Unexpected unlabelled result';description='';state='opened';labels=@('team::Other');web_url='https://web.git.mil/demo/345'}
            )
        }
        return @()
    }
}
$snapshot=Get-CascadeGitMilSnapshot -ProjectIds @(16629) -SinceDays 30 -MaxPages 1 -IssueLabel 'team::Data-Crucible'
if(@($snapshot.Issues).Count -ne 3){throw 'Targeted label query did not union with broad import and dedupe.'}
if(@($snapshot.Issues | Where-Object {$_.iid -eq 999}).Count -ne 1){throw 'Issue beyond broad page cap was not imported by label.'}
if(@($snapshot.Issues | Where-Object {$_.iid -eq 345}).Count){throw 'Unlabelled issue improperly trusted in label query.'}
if(-not $snapshot.LabelQuery.Performed -or $snapshot.LabelQuery.MatchesReturned -ne 2 -or $snapshot.LabelQuery.AdditionalIssuesImported -ne 1 -or $snapshot.LabelQuery.ProjectFailures -ne 0){
    throw 'Targeted server-side label statistics are wrong.'
}
$paths=@(& $module {$script:LabelTestPaths.ToArray()})
if(@($paths | Where-Object {$_ -eq '/projects/16629/issues?state=all&labels=team%3A%3AData-Crucible'}).Count -ne 1){
    throw 'GitLab-native issues?labels= URL was not correctly encoded.'
}
$plan=ConvertTo-CascadeGitMilPlan -Snapshot $snapshot -IssueLabel 'team::Data-Crucible'
if(@($plan.Issues).Count -ne 2 -or $plan.Scope.ServerLabelAddedIssues -ne 1 -or -not $plan.Scope.ServerLabelQueryPerformed){
    throw 'Scoped Nexus plan did not include supplemental remote label results.'
}
$otherPlan=ConvertTo-CascadeGitMilPlan -Snapshot $snapshot -IssueLabel 'team::Other'
if(@($otherPlan.Issues).Count -ne 1 -or $otherPlan.Scope.ServerLabelQueryPerformed){
    throw 'Changing to an unqueried label must not imply server-side completeness.'
}
$before=[int](& $module {$script:LabelTestPaths.Count})
$unsupported=Get-CascadeGitMilSnapshot -ProjectIds @(16629) -SinceDays 30 -MaxPages 1 -IssueLabel 'Any'
$after=[int](& $module {$script:LabelTestPaths.Count})
if($unsupported.LabelQuery.Performed){throw 'Reserved GitLab Any label must not be sent to label API.'}
$newPaths=@(& $module {$script:LabelTestPaths.ToArray()} | Select-Object -Skip $before)
if(@($newPaths | Where-Object {([string]$_).Contains('&labels=')}).Count -gt 0){
    throw 'Reserved GitLab Any/None names were sent as server-side issue-label filters.'
}
if($after -le $before){throw 'Synthetic source was not queried in reserved-label fallback.'}
Write-Host 'PASS: GitLab-native label query supplements old issues, deduplicates, validates literal labels and preserves honest scope diagnostics.' -ForegroundColor Green
