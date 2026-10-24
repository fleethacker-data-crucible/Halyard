
# Read-only local Git discovery. Compatible with Windows PowerShell 5.1.
function Get-CascadeGitActivity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Roots,
        [int]$SinceDays = 30,
        [int]$MaxCommitsPerRepo = 300
    )
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git CLI is required for repository discovery.' }
    $repos = New-Object System.Collections.ArrayList
    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Repository root not found: $root" }
        $resolved = (Resolve-Path -LiteralPath $root).Path
        $candidates = @($resolved)
        $candidates += @(Get-ChildItem -LiteralPath $resolved -Directory -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq '.git' } | ForEach-Object { $_.Parent.FullName })
        foreach ($candidate in ($candidates | Select-Object -Unique)) {
            $gitdir = Join-Path $candidate '.git'
            if (-not (Test-Path -LiteralPath $gitdir)) { continue }
            $null = $repos.Add($candidate)
        }
    }
    $items = New-Object System.Collections.ArrayList
    foreach ($repo in ($repos | Select-Object -Unique)) {
        $since = (Get-Date).AddDays(-$SinceDays).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        $format = '%H%x1f%P%x1f%aN%x1f%aE%x1f%aI%x1f%s%x1e'
        $raw = & git -C $repo log --all "--since=$since" "--max-count=$MaxCommitsPerRepo" "--format=$format" 2>$null
        if ($LASTEXITCODE -ne 0) { continue }
        foreach ($record in (($raw -join "`n") -split [char]30)) {
            $fields = $record.Trim() -split [char]31
            if ($fields.Count -lt 6) { continue }
            $sha = $fields[0].Trim()
            if ($sha -notmatch '^[0-9a-f]{40,64}$') { continue }
            $paths = @(& git -C $repo diff-tree --no-commit-id --name-only -r --root $sha 2>$null |
                Where-Object { $_ -and $_.Trim() })
            $null = $items.Add([pscustomobject]@{
                EvidenceId = "git:$sha"
                Repository = $repo
                Commit = $sha
                Parents = $fields[1]
                Author = $fields[2]
                AuthorEmail = $fields[3]
                Timestamp = $fields[4]
                Subject = $fields[5]
                Files = @($paths)
                Source = 'git'
            })
        }
    }
    return @($items.ToArray())
}

function Get-CascadeEvidenceFingerprint {
    param([Parameter(Mandatory)]$Evidence)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(
        [string]$Evidence.Repository + '|' + [string]$Evidence.Commit)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-','').ToLowerInvariant() }
    finally { $sha256.Dispose() }
}

function ConvertTo-CascadeDiscoveryPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Activity)
    $groups = @($Activity | Group-Object Repository)
    $proposals = New-Object System.Collections.ArrayList
    foreach ($group in $groups) {
        $commits = @($group.Group | Sort-Object Timestamp)
        $paths = @($commits | ForEach-Object { $_.Files } | Sort-Object -Unique)
        $subjects = @($commits | ForEach-Object { $_.Subject })
        $first = if ($commits.Count) { $commits[0].Timestamp } else { $null }
        $last = if ($commits.Count) { $commits[$commits.Count - 1].Timestamp } else { $null }
        # A repository-level candidate is deliberately not called a Story or a completed Feature.
        # Semantic classification must be corroborated with existing backlog and tests.
        $null = $proposals.Add([pscustomobject]@{
            ProposalId = 'repo:' + (Split-Path $group.Name -Leaf)
            Repository = $group.Name
            CandidateType = 'UNCLASSIFIED'
            SuggestedTitle = 'Review development activity in ' + (Split-Path $group.Name -Leaf)
            State = 'NEEDS_TRIAGE'
            Completion = 'UNVERIFIED'
            EstimatedHours = $null
            Confidence = 'UNASSESSED'
            FirstObserved = $first
            LastObserved = $last
            EvidenceIds = @($commits | ForEach-Object { $_.EvidenceId })
            ChangedFiles = @($paths)
            CommitSubjects = @($subjects)
            ProposedAction = 'REVIEW'
            Reason = 'Commit evidence alone cannot establish intent, hours worked, acceptance or completion.'
        })
    }
    return @($proposals.ToArray())
}

function Export-CascadeDiscovery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Roots,
        [string]$OutputDirectory = (Join-Path (Get-Location).Path 'output'),
        [int]$SinceDays = 30
    )
    if (-not (Test-Path -LiteralPath $OutputDirectory)) {
        $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    }
    $activity = @(Get-CascadeGitActivity -Roots $Roots -SinceDays $SinceDays)
    $proposals = @(ConvertTo-CascadeDiscoveryPlan -Activity $activity)
    $result = [pscustomobject]@{
        SchemaVersion = 1
        GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        ReadOnly = $true
        Activity = @($activity)
        Proposals = @($proposals)
    }
    $json = Join-Path $OutputDirectory 'discovery_plan.json'
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $json -Encoding UTF8
    $lines = New-Object System.Collections.ArrayList
    $null = $lines.Add('# Cascade engineering activity brief')
    $null = $lines.Add('')
    $null = $lines.Add('Generated from local Git history. Not a verified measure of developer hours or completion.')
    $null = $lines.Add('')
    $null = $lines.Add("Repositories with observed activity: $($proposals.Count)")
    $null = $lines.Add("Commits observed: $($activity.Count)")
    $null = $lines.Add('')
    foreach ($p in $proposals) {
        $null = $lines.Add("## $($p.SuggestedTitle)")
        $null = $lines.Add("- Evidence: $($p.EvidenceIds.Count) commits")
        $null = $lines.Add("- Status: needs triage; completion unverified")
        $null = $lines.Add("- Last observed: $($p.LastObserved)")
        $null = $lines.Add('')
    }
    $brief = Join-Path $OutputDirectory 'engineering_activity_brief.md'
    $lines | Set-Content -LiteralPath $brief -Encoding UTF8
    return [pscustomobject]@{ JsonPath = $json; BriefPath = $brief; Repositories = $proposals.Count; Commits = $activity.Count }
}
