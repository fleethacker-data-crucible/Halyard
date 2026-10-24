# Cascade Autonomous Delivery — evidence-first, deterministic orchestration.
# Windows PowerShell 5.1; no third-party runtime dependencies.
function Get-CascadeStableId {
    [CmdletBinding()]param([Parameter(Mandatory)][string]$Text)
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($Text.ToLowerInvariant()); $hex = [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-','').ToLowerInvariant(); return $hex.Substring(0,20) }
    finally { $hash.Dispose() }
}
function Get-CascadeWorkCategory {
    [CmdletBinding()]param([string]$Subject,[string[]]$Files)
    $s = ([string]$Subject).ToLowerInvariant(); $f = (@($Files) -join ' ').ToLowerInvariant()
    if ($s -match '\b(fix|bug|repair|regression|hotfix)\b') { return 'DEFECT' }
    if ($s -match '\b(refactor|cleanup|format|lint|reorganize)\b') { return 'MAINTENANCE' }
    if ($s -match '\b(test|coverage|spec)\b' -or $f -match '(^|[\\/])(tests?|specs?)[\\/]') { return 'QUALITY' }
    if ($s -match '\b(doc|readme|guide|documentation)\b') { return 'DOCUMENTATION' }
    if ($s -match '\b(ci|pipeline|deploy|terraform|cdk|infra)\b' -or $f -match '(\.gitlab-ci|\.github/workflows)') { return 'INFRASTRUCTURE' }
    if ($s -match '\b(add|implement|create|introduce|support|feature)\b') { return 'CAPABILITY' }
    return 'UNKNOWN'
}
function Get-CascadeWorkClusters {
    [CmdletBinding()]param([Parameter(Mandatory)][object[]]$Activity)
    $groups = @($Activity | Group-Object { ([string]$_.Repository).ToLowerInvariant() + '|' + ([string]$_.Subject -replace '(?i)^(feat|fix|chore|docs|test|refactor)(\([^)]*\))?:\s*','' -replace '\s+',' ').Trim().ToLowerInvariant() })
    $output = New-Object System.Collections.ArrayList
    foreach ($group in $groups) {
        $items = @($group.Group | Sort-Object Timestamp)
        if (-not $items.Count) { continue }
        $subject = [string]$items[0].Subject
        $files = @($items | ForEach-Object { $_.Files } | Where-Object { $_ } | Sort-Object -Unique)
        $evidence = @($items | ForEach-Object { $_.EvidenceId } | Sort-Object -Unique)
        $repo = [string]$items[0].Repository
        $key = Get-CascadeStableId -Text ($repo + '|' + $subject.Trim().ToLowerInvariant())
        $category = Get-CascadeWorkCategory -Subject $subject -Files $files
        $null = $output.Add([pscustomobject]@{
            Key = $key; Repository = $repo; Title = $subject; Category = $category
            SuggestedType = 'STORY'; EvidenceIds = $evidence; ChangedFiles = $files
            FirstSeen = [string]$items[0].Timestamp; LastSeen = [string]$items[$items.Count-1].Timestamp
            Confidence = 'LOW'; Completion = 'UNVERIFIED'; EstimatedHours = $null
            Reason = 'Git commits establish activity, not acceptance, effort, or business intent.'
        })
    }
    return @($output.ToArray())
}
function Get-CascadeIssueEvidenceKeys {
    param($Issue)
    $text = [string]$Issue.description
    $found = New-Object System.Collections.ArrayList
    foreach ($m in [regex]::Matches($text,'(?im)^\s*-\s*Cascade evidence key:\s*([a-f0-9]{20})\s*$')) { $null=$found.Add($m.Groups[1].Value) }
    return @($found.ToArray())
}
function New-CascadeReconciliationPlan {
    [CmdletBinding()]param([Parameter(Mandatory)][object[]]$Clusters,[object[]]$Issues=@())
    $actions = New-Object System.Collections.ArrayList
    foreach ($cluster in $Clusters) {
        $matched = @($Issues | Where-Object { (Get-CascadeIssueEvidenceKeys -Issue $_) -contains $cluster.Key })
        $kind = 'PROPOSE_CREATE'; $iid = $null
        if ($matched.Count -gt 1) { $kind = 'CONFLICT'; }
        elseif ($matched.Count -eq 1) { $kind = 'EXISTS'; $iid = $matched[0].iid }
        $desc = @(
            '## Engineering evidence',
            '',
            ('- Cascade evidence key: ' + $cluster.Key),
            ('- Repository: `' + $cluster.Repository + '`'),
            ('- Classification: ' + $cluster.Category + ' (heuristic; review required)'),
            ('- Status: Unverified; no acceptance evidence evaluated'),
            '',
            '### Observed commits'
        )
        foreach ($id in @($cluster.EvidenceIds)) { $desc += ('- `' + $id + '`') }
        $desc += @('', '### Acceptance criteria', '- [ ] Confirm the intended outcome with a product owner', '- [ ] Verify implementation and relevant tests', '- [ ] Confirm integration and acceptance before closing')
        $null=$actions.Add([pscustomobject]@{
            Key=$cluster.Key; Action=$kind; ExistingIid=$iid; Type='STORY'
            Title=('[Discovered] ' + $cluster.Title); Description=($desc -join "`n")
            Labels=@('cascade','type::story','cascade::needs-triage'); EvidenceIds=@($cluster.EvidenceIds)
            Confidence=$cluster.Confidence; RequiresReview=$true; AutoClose=$false
        })
    }
    return @($actions.ToArray())
}
function Test-CascadeReconciliationPlan {
    [CmdletBinding()]param([Parameter(Mandatory)][object[]]$Actions)
    $errors = New-Object System.Collections.ArrayList
    $seen = @{}
    foreach ($a in $Actions) {
        if ([string]$a.Key -notmatch '^[0-9a-f]{20}$') { $null=$errors.Add('Invalid key') }
        if ($seen.ContainsKey([string]$a.Key)) { $null=$errors.Add('Duplicate key: ' + $a.Key) }
        $seen[[string]$a.Key] = $true
        if ($a.Action -notin @('PROPOSE_CREATE','EXISTS','CONFLICT')) { $null=$errors.Add('Invalid action') }
        if ($a.AutoClose -eq $true) { $null=$errors.Add('Automatic closure is not permitted without verified acceptance') }
        if ([string]$a.Description -notmatch [regex]::Escape('Cascade evidence key: '+$a.Key)) { $null=$errors.Add('Missing evidence marker') }
    }
    return [pscustomobject]@{ Valid=($errors.Count -eq 0); Errors=@($errors.ToArray()) }
}
function New-CascadeDeliveryBrief {
    [CmdletBinding()]param([Parameter(Mandatory)][object[]]$Activity,[Parameter(Mandatory)][object[]]$Clusters,[Parameter(Mandatory)][object[]]$Actions,[object[]]$Issues=@())
    $repoCount=@($Activity | ForEach-Object { $_.Repository } | Sort-Object -Unique).Count
    $createCount=@($Actions | Where-Object {$_.Action -eq 'PROPOSE_CREATE'}).Count
    $linkedCount=@($Actions | Where-Object {$_.Action -eq 'EXISTS'}).Count
    $conflicts=@($Actions | Where-Object {$_.Action -eq 'CONFLICT'}).Count
    $lines=@('# Cascade — engineering delivery evidence brief','','**Status:** Observational; not a verified delivery/acceptance report.','',
        ('Generated UTC: '+[datetime]::UtcNow.ToString('o')),'', '## Evidence coverage',
        ('- Repositories observed: '+$repoCount),('- Commits observed: '+$Activity.Count),
        ('- Candidate work clusters: '+$Clusters.Count),('- New candidate work items awaiting triage: '+$createCount),
        ('- Matched existing items: '+$linkedCount),('- Conflicts requiring review: '+$conflicts),'',
        '## Work observed (not verified as completed)','')
    foreach ($c in $Clusters) { $lines += ('- **'+$c.Title+'** — '+$c.Category+'; '+@($c.EvidenceIds).Count+' commit(s); completion unverified; key `'+$c.Key+'`') }
    $lines += @('', '## Risks and data limitations', '- Commit frequency and timestamps are not hours worked.',
        '- No work is declared complete without acceptance and integration evidence.',
        '- Classification is heuristic and requires confirmation.',
        '- This report does not establish schedule adherence, sprint velocity, or individual productivity.',
        '', '## Recommended next steps', '- Review proposed items, resolve duplicates and map each to the correct Feature and Epic.',
        '- Validate CI, merge and deployment evidence before making completion claims.')
    return ($lines -join "`n")
}
function Invoke-CascadeAutonomousCycle {
    [CmdletBinding()]param(
        [Parameter(Mandatory)][string[]]$Roots,
        [Parameter(Mandatory)][string]$Workspace,
        [int]$SinceDays=30,
        [string]$IssuesFixture='',
        [switch]$UseGitLab
    )
    if ($UseGitLab -and $IssuesFixture) { throw 'Select either live GitLab or an issues fixture.' }
    $activity = @(Get-CascadeGitActivity -Roots $Roots -SinceDays $SinceDays)
    $clusters = @(Get-CascadeWorkClusters -Activity $activity)
    $issues=@()
    if ($IssuesFixture) {
        $fixture=Get-Content -LiteralPath $IssuesFixture -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($fixture -is [array]) { $issues=@($fixture) }
        elseif ($fixture.PSObject.Properties['issues']) { $issues=@($fixture.issues) }
        else { throw 'Fixture must be an array or contain an issues array.' }
    }
    if ($UseGitLab) { $issues=@(Get-CascadeIssuesFromGitLab -State all -Labels '') }
    $actions=@(New-CascadeReconciliationPlan -Clusters $clusters -Issues $issues)
    $validation=Test-CascadeReconciliationPlan -Actions $actions
    if (-not $validation.Valid) { throw ('Reconciliation validation failed: '+($validation.Errors -join '; ')) }
    $runId=[datetime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
    $folder=Join-Path $Workspace $runId
    [void](New-Item -ItemType Directory -Path $folder -Force)
    $manifest=[pscustomobject]@{SchemaVersion=1;RunId=$runId;GeneratedUtc=[datetime]::UtcNow.ToString('o');Mode='READ_ONLY';Activity=@($activity);Clusters=@($clusters);Actions=@($actions)}
    Save-CascadeAgentJson -Path (Join-Path $folder 'plan.json') -Value $manifest
    $brief=New-CascadeDeliveryBrief -Activity $activity -Clusters $clusters -Actions $actions -Issues $issues
    [IO.File]::WriteAllText((Join-Path $folder 'leadership-brief.md'),$brief,(New-Object Text.UTF8Encoding($false)))
    return [pscustomobject]@{RunId=$runId;Directory=$folder;ActivityCount=$activity.Count;CandidateCount=$clusters.Count;NewProposals=@($actions|Where-Object{$_.Action -eq 'PROPOSE_CREATE'}).Count;Plan=(Join-Path $folder 'plan.json');Report=(Join-Path $folder 'leadership-brief.md')}
}
function Invoke-CascadeApprovedSandboxPlan {
    [CmdletBinding(SupportsShouldProcess)]param(
        [Parameter(Mandatory)][string]$PlanPath,
        [Parameter(Mandatory)][string[]]$ApprovedKeys,
        [Parameter(Mandatory)][int]$ProjectId,
        [switch]$Execute
    )
    $ctx=Assert-CascadeWriteTarget -ProjectId $ProjectId
    if ($ctx.Mode -ne 'sandbox') { throw 'This workflow only supports sandbox writes.' }
    Import-CascadeGitLabEnv
    if ([string]$env:GITLAB_PROJECT_ID -ne [string]$ProjectId) { throw 'GITLAB_PROJECT_ID must match the explicitly approved sandbox ProjectId.' }
    $plan=Read-CascadeAgentJson -Path $PlanPath
    if ($plan.Mode -ne 'READ_ONLY') { throw 'Unsupported plan format.' }
    $actions=@($plan.Actions)
    $valid=Test-CascadeReconciliationPlan -Actions $actions
    if (-not $valid.Valid) { throw 'Invalid plan.' }
    $allowed=@($actions | Where-Object {$_.Action -eq 'PROPOSE_CREATE' -and $ApprovedKeys -contains $_.Key})
    if (-not $Execute) { return @($allowed | Select-Object Key,Title,Action) }
    if (-not $ApprovedKeys.Count) { throw 'No approved evidence keys supplied.' }
    if (-not $PSCmdlet.ShouldProcess("GitLab sandbox project $ProjectId", "Create $($allowed.Count) approved issue(s)")) { return }
    # Re-read live issues immediately before creating to reduce duplicates.
    $current=@(Get-CascadeIssuesFromGitLab -State all -Labels '')
    foreach ($a in $allowed) {
        if (@($current | Where-Object { (Get-CascadeIssueEvidenceKeys -Issue $_) -contains $a.Key }).Count -gt 0) { Write-Warning ('Skipped existing evidence key '+$a.Key); continue }
        $result=New-CascadeGitLabIssue -Title $a.Title -Description $a.Description -Labels @($a.Labels) -Confirm:$false
        Write-Output $result
        # Update in-memory evidence so repeated approved keys cannot be created twice in one run.
        $current += [pscustomobject]@{iid=$result.iid;description=$a.Description}
    }
}
