# Git.mil read-only intelligence. Uses the existing ARC/GitLab token resolver.
function Initialize-CascadeGitMilSession {
    [CmdletBinding()]param()
    Import-CascadeGitLabEnv
    if (-not $env:GITLAB_URL) { $env:GITLAB_URL='https://web.git.mil' }
    $hostName=([uri]$env:GITLAB_URL).Host
    if ($hostName -ne 'web.git.mil') { throw 'Git.mil mode requires GITLAB_URL=https://web.git.mil' }
    $arc=Import-CascadeArcSession -Quiet
    # A browser request must never block on an ARC unlock prompt. ARC may be
    # unlocked separately through its approved GUI in this PowerShell process.
    if ([string]::IsNullOrWhiteSpace($env:GITLAB_TOKEN) -and -not (Test-CascadeArcVaultUnlocked)) {
        throw 'ARC vault is locked or unavailable. Unlock it through the approved ARC application; no browser PIN entry is supported.'
    }
    $token=Get-CascadeGitLabToken
    if (-not $token) { throw 'ARC gitmil token unavailable. Unlock ARC and map gitmil.' }
    return [pscustomobject]@{ArcLoaded=[bool]$arc;GitLabHost=$hostName;GitLabTokenReady=$true}
}
function Invoke-CascadeGitMilPaged {
    [CmdletBinding()]param([Parameter(Mandatory)][string]$Path,[int]$MaxPages=10,[int]$PerPage=100)
    if ($Path -notmatch '^/' -or $Path -match '(?i)https?://|\.\.') { throw 'API path must be relative and cannot contain traversal.' }
    $root=Get-CascadeGitLabApiRoot
    $headers=Get-CascadeGitLabHeaders
    $result=New-Object System.Collections.ArrayList
    for($page=1;$page -le $MaxPages;$page++) {
        $sep=if($Path.Contains('?')){'&'}else{'?'}
        $uri=$root+$Path+$sep+'per_page='+$PerPage+'&page='+$page
        $batch=Invoke-CascadeGitLabGetJson -Uri $uri -Headers $headers -TimeoutSec 90
        $flat=@($batch)
        if($flat.Count -eq 1 -and $flat[0] -is [array]) {$flat=@($flat[0])}
        foreach($item in $flat) { if($null -ne $item){[void]$result.Add($item)} }
        if($flat.Count -lt $PerPage){break}
    }
    # PowerShell pipeline output must be a flat sequence, not an array nested inside another array.
    return @($result.ToArray())
}
function Get-CascadeGitMilAvailableLabels {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int[]]$ProjectIds)
    $ids=@($ProjectIds | Where-Object {$_ -gt 0} | Select-Object -Unique)
    if($ids.Count -lt 1 -or $ids.Count -gt 20){throw 'Choose between 1 and 20 source projects to discover labels.'}
    # Standard GitLab GET /projects/:id/labels; inherited group labels included.
    # Fetch names only. No issue updates or tokens in the response.
    $null=Initialize-CascadeGitMilSession
    $unique=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    $problems=New-Object System.Collections.ArrayList
    foreach($id in $ids){
        try{
            $rows=@(Invoke-CascadeGitMilPaged -Path ('/projects/'+$id+'/labels?include_ancestor_groups=true') -MaxPages 5)
            foreach($row in $rows){
                $name=[string]$row.name
                if($name.Length -gt 0 -and $name.Length -le 100 -and -not $unique.ContainsKey($name)){
                    $unique.Add($name,$name)
                }
            }
        }catch {
            # No raw error body: avoid exposing internal provider details.
            [void]$problems.Add([pscustomobject]@{ProjectId=$id;Code='LABEL_DISCOVERY_FAILED'})
        }
    }
    if($problems.Count -eq $ids.Count){
        throw 'Label discovery failed for every selected project. Verify Git.mil access.'
    }
    return [pscustomobject]@{
        Labels=@($unique.Values | Sort-Object)
        ProjectsChecked=$ids.Count
        ProjectErrors=@($problems.ToArray())
    }
}
function ConvertTo-CascadeGitMilIssueRecord {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Issue,[Parameter(Mandatory)][int]$ProjectId)
    return [pscustomobject]@{
        id=$Issue.id;iid=$Issue.iid;project_id=$ProjectId;title=$Issue.title
        description=$Issue.description;state=$Issue.state;labels=@($Issue.labels)
        web_url=$Issue.web_url;created_at=$Issue.created_at;updated_at=$Issue.updated_at
        closed_at=$Issue.closed_at;milestone=$Issue.milestone;assignees=@($Issue.assignees)
    }
}
function Get-CascadeGitMilSnapshot {
    [CmdletBinding()]param([int[]]$ProjectIds=@(),[int]$SinceDays=30,[int]$MaxProjects=20,[int]$MaxPages=5,[AllowEmptyString()][string]$IssueLabel='')
    if($SinceDays -lt 1 -or $SinceDays -gt 365){throw 'SinceDays must be 1..365'}
    if($MaxProjects -lt 1 -or $MaxProjects -gt 50){throw 'MaxProjects must be 1..50'}
    $selectedLabel=Assert-CascadeIssueLabelFilter -Label $IssueLabel
    # GitLab treats comma as a multi-label operator and Any/None as special
    # query terms. Those literal label names can still be filtered locally.
    $supportedLabel=($selectedLabel.Length -gt 0 -and $selectedLabel.IndexOf(',') -lt 0 -and
        $selectedLabel -notin @('Any','None'))
    $labelPageLimit=12
    $session=Initialize-CascadeGitMilSession
    $root=Get-CascadeGitLabApiRoot
    $headers=Get-CascadeGitLabHeaders
    $user=Invoke-CascadeGitLabApi -Uri ($root+'/user') -Headers $headers
    if(-not $ProjectIds.Count){
        $available=@(Invoke-CascadeGitMilPaged -Path '/projects?membership=true&simple=true&order_by=last_activity_at&sort=desc' -MaxPages 2)
        $ProjectIds=@($available|Select-Object -First $MaxProjects|ForEach-Object {[int]$_.id})
    }
    if($ProjectIds.Count -gt $MaxProjects){throw 'Requested more projects than MaxProjects.'}
    $since=[uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(-$SinceDays).ToString('yyyy-MM-ddTHH:mm:ssZ'))
    $projects=New-Object System.Collections.ArrayList
    $commits=New-Object System.Collections.ArrayList
    $issues=New-Object System.Collections.ArrayList
    $merges=New-Object System.Collections.ArrayList
    $pipelines=New-Object System.Collections.ArrayList
    $errors=New-Object System.Collections.ArrayList
    $seenIssues=@{}
    $remoteMatches=0
    $remoteAdded=0
    $remoteQueried=0
    $remoteFailures=0
    $labelPageLimitPossible=$false
    foreach($id in $ProjectIds){
        if($id -lt 1){continue}
        $base='/projects/'+$id
        try{
            $project=Invoke-CascadeGitLabApi -Uri ($root+$base) -Headers $headers
            [void]$projects.Add([pscustomobject]@{id=$id;name=$project.name;path_with_namespace=$project.path_with_namespace;web_url=$project.web_url;default_branch=$project.default_branch})
            foreach($issue in @(Invoke-CascadeGitMilPaged -Path ($base+'/issues?state=all') -MaxPages $MaxPages)){
                if($null -eq $issue){continue}
                $key=[string]$id+':'+[string]$issue.iid
                if(-not $seenIssues.ContainsKey($key)){
                    [void]$issues.Add((ConvertTo-CascadeGitMilIssueRecord -Issue $issue -ProjectId $id))
                    $seenIssues[$key]=$true
                }
            }
            # IMPORTANT: the default paged import may cover only the first 500
            # issues in a shared CBR. Query GitLab with labels=<name> directly,
            # then merge by project/IID. The original broad import is retained.
            if($supportedLabel){
                $remoteQueried++
                try{
                    $encoded=[uri]::EscapeDataString($selectedLabel)
                    $rows=@(Invoke-CascadeGitMilPaged -Path ($base+'/issues?state=all&labels='+$encoded) -MaxPages $labelPageLimit)
                    if($rows.Count -ge (100*$labelPageLimit)){$labelPageLimitPossible=$true}
                    foreach($issue in $rows){
                        if($null -eq $issue){continue}
                        # Trust only literal issue labels, never inferred project
                        # team membership or GitLab's search term interpretations.
                        if(-not (Test-CascadeIssueHasLabel -Issue $issue -Label $selectedLabel)){continue}
                        $remoteMatches++
                        $key=[string]$id+':'+[string]$issue.iid
                        if(-not $seenIssues.ContainsKey($key)){
                            [void]$issues.Add((ConvertTo-CascadeGitMilIssueRecord -Issue $issue -ProjectId $id))
                            $seenIssues[$key]=$true
                            $remoteAdded++
                        }
                    }
                }catch{
                    $remoteFailures++
                    [void]$errors.Add([pscustomobject]@{ProjectId=$id;Code='LABEL_QUERY_FAILED';Message='Git.mil label query unavailable for this source project. The normal imported issues remain available.'})
                }
            }
            foreach($commit in @(Invoke-CascadeGitMilPaged -Path ($base+'/repository/commits?all=true&since='+$since) -MaxPages $MaxPages)){
                [void]$commits.Add([pscustomobject]@{EvidenceId=('gitmil:'+ $id + ':'+$commit.id);Repository=[string]$project.path_with_namespace;ProjectId=$id;Commit=$commit.id;Author=$commit.author_name;Timestamp=$commit.created_at;Subject=$commit.title;Files=@();Source='gitmil';WebUrl=$commit.web_url})
            }
            foreach($mr in @(Invoke-CascadeGitMilPaged -Path ($base+'/merge_requests?state=all&updated_after='+$since) -MaxPages $MaxPages)){
                [void]$merges.Add([pscustomobject]@{project_id=$id;iid=$mr.iid;title=$mr.title;state=$mr.state;merged_at=$mr.merged_at;updated_at=$mr.updated_at;web_url=$mr.web_url;source_branch=$mr.source_branch;target_branch=$mr.target_branch;sha=$mr.sha})
            }
            foreach($pipeline in @(Invoke-CascadeGitMilPaged -Path ($base+'/pipelines?updated_after='+$since) -MaxPages $MaxPages)){
                [void]$pipelines.Add([pscustomobject]@{project_id=$id;id=$pipeline.id;sha=$pipeline.sha;status=$pipeline.status;ref=$pipeline.ref;web_url=$pipeline.web_url;updated_at=$pipeline.updated_at})
            }
        } catch { [void]$errors.Add([pscustomobject]@{ProjectId=$id;Error=$_.Exception.Message}) }
    }
    return [pscustomobject]@{
        SchemaVersion=2;Source='web.git.mil';GeneratedUtc=[datetime]::UtcNow.ToString('o');SinceDays=$SinceDays
        User=[pscustomobject]@{id=$user.id;username=$user.username;name=$user.name}
        ArcLoaded=$session.ArcLoaded;Projects=@($projects.ToArray());Activity=@($commits.ToArray())
        Issues=@($issues.ToArray());MergeRequests=@($merges.ToArray());Pipelines=@($pipelines.ToArray());Errors=@($errors.ToArray())
        TruncatedPotentially=($MaxPages -gt 0 -or $labelPageLimitPossible);ReadOnly=$true
        LabelQuery=[pscustomobject]@{
            Label=$selectedLabel
            Performed=($supportedLabel -and $remoteQueried -gt 0)
            Supported=$supportedLabel
            ProjectsQueried=$remoteQueried
            ProjectFailures=$remoteFailures
            MatchesReturned=$remoteMatches
            AdditionalIssuesImported=$remoteAdded
            PageLimit=$labelPageLimit
            PageLimitPossiblyReached=$labelPageLimitPossible
            Note='Exact read-only GitLab /projects/:id/issues?labels= filter; broad import retained. Applies to issues, not native GitLab Epic work-item listings.'
        }
    }
}
function ConvertTo-CascadeGitMilPlan {
    [CmdletBinding()]param([Parameter(Mandatory)]$Snapshot,[AllowEmptyString()][string]$IssueLabel='')
    # Preserve the full imported issues for existence-only parent checks.
    # The selected issue label still determines every visible finding.
    $sourceIssues=@($Snapshot.Issues | Where-Object {$null -ne $_})
    $Snapshot=Get-CascadeScopedGitMilSnapshot -Snapshot $Snapshot -Label $IssueLabel
    $topology=Get-CascadeTopologyAudit -Issues @($Snapshot.Issues) -SourceIssues $sourceIssues -IssueLabel $IssueLabel
    # A team may have classified issues but zero attributable commits (e.g.
    # tickets in the shared CBR). This is valid, not a schema/API error.
    $clusters=@()
    if(@($Snapshot.Activity).Count -gt 0){
        $clusters=@(Get-CascadeWorkClusters -Activity @($Snapshot.Activity))
    }
    $actions=@()
    if($clusters.Count -gt 0){
        $actions=@(New-CascadeReconciliationPlan -Clusters $clusters -Issues @($Snapshot.Issues))
    }
    $valid=if($actions.Count -gt 0){Test-CascadeReconciliationPlan -Actions $actions}else{[pscustomobject]@{Valid=$true;Errors=@()}}
    if(-not $valid.Valid){throw ('Invalid reconciliation plan: '+($valid.Errors -join '; '))}
    # An exact label can legitimately match zero issues. PowerShell 5.1
    # rejects an empty array for HierarchyProjection's mandatory Issues param.
    $hierarchy=@()
    if(@($Snapshot.Issues).Count -gt 0){
        $hierarchy=@(Get-CascadeHierarchyProjection -Issues @($Snapshot.Issues))
    }
    return [pscustomobject]@{
        SchemaVersion=2;RunId=[datetime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
        Source='web.git.mil';GeneratedUtc=$Snapshot.GeneratedUtc;Mode='READ_ONLY';Projects=@($Snapshot.Projects)
        Activity=@($Snapshot.Activity);Clusters=@($clusters);Actions=@($actions);Issues=@($Snapshot.Issues)
        MergeRequests=@($Snapshot.MergeRequests);Pipelines=@($Snapshot.Pipelines);Errors=@($Snapshot.Errors)
        User=$Snapshot.User;ArcLoaded=$Snapshot.ArcLoaded;Scope=$Snapshot.Scope;Hierarchy=@($hierarchy);TopologyAudit=$topology;CoverageWarning=('Commit clusters are heuristic. API page limits can truncate data; do not interpret as full historical coverage. '+[string]$Snapshot.Scope.Warning)
    }
}
function Export-CascadeGitMilPlan {
    [CmdletBinding()]param([int[]]$ProjectIds=@(),[int]$SinceDays=30,[Parameter(Mandatory)][string]$Workspace,[AllowEmptyString()][string]$IssueLabel='')
    $snapshot=Get-CascadeGitMilSnapshot -ProjectIds $ProjectIds -SinceDays $SinceDays -MaxProjects 20 -IssueLabel $IssueLabel
    $plan=ConvertTo-CascadeGitMilPlan -Snapshot $snapshot -IssueLabel $IssueLabel
    $dir=Join-Path $Workspace $plan.RunId
    [void](New-Item -ItemType Directory -Path $dir -Force)
    # Preserve raw imports separately from scoped dashboards. Never rewrite Git.mil.
    $snapshot|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $dir 'source-snapshot.json') -Encoding UTF8
    $json=Join-Path $dir 'plan.json'
    $plan|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $json -Encoding UTF8
    $report=if(@($plan.Activity).Count -eq 0){
        "# Cascade - scoped Git.mil review`n`nIssue label: $IssueLabel`n`nNo project-level commits can be safely attributed to this workspace. Work items remain visible for review; no delivery completion is inferred.`n"
    }else{
        New-CascadeDeliveryBrief -Activity @($plan.Activity) -Clusters @($plan.Clusters) -Actions @($plan.Actions) -Issues @($plan.Issues)
    }
    [IO.File]::WriteAllText((Join-Path $dir 'leadership-brief.md'),$report,(New-Object Text.UTF8Encoding($false)))
    return [pscustomobject]@{RunId=$plan.RunId;PlanPath=$json;Directory=$dir;Plan=$plan}
}
