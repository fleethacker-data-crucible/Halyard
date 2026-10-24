<#
.SYNOPSIS
  Test path: pull issues, normalize ONE as logical dup, GenAI propose.
  Does NOT write to GitLab.

  Step log goes to host + output\ai_restructure_steps.log
#>
param(
    [ValidateSet('Auto', 'Grok', 'GenAi')]
    [string]$Profile = 'GenAi',
    [int]$SourceIid = 0,
    [int]$SourceProjectId = 0,
    [string]$Labels = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot



function Write-Step {
    param([string]$Step, [string]$Message, [ValidateSet('INFO','WARN','ERROR','OK')][string]$Level = 'INFO')
    $ts = Get-Date -Format 'HH:mm:ss'
    $line = '[{0}] [{1}] {2}: {3}' -f $ts, $Level, $Step, $Message
    $c = switch ($Level) {
        'ERROR' { 'Red' }
        'WARN'  { 'Yellow' }
        'OK'    { 'Green' }
        default { 'DarkGray' }
    }
    Write-Host $line -ForegroundColor $c
    try {
        $logPath = Join-Path $RepoRoot 'output\ai_restructure_steps.log'
        $dir = Split-Path $logPath -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        Add-Content -Path $logPath -Value $line -Encoding UTF8
    } catch {}
}

function Import-LocalCascadeEnv {
    $envFile = Join-Path $RepoRoot 'config\cascade.env'
    Write-Step 'ENV' ("Looking for {0}" -f $envFile)
    if (-not (Test-Path -LiteralPath $envFile)) {
        Write-Step 'ENV' 'cascade.env not found' 'WARN'
        return
    }
    $n = 0
    Get-Content -LiteralPath $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or $line -eq '') { return }
        if ($line -match '^\s*([^=]+)=(.*)$') {
            $k = $Matches[1].Trim()
            $v = $Matches[2].Trim().Trim('"').Trim("'")
            Set-Item -Path ("Env:{0}" -f $k) -Value $v
            $n++
            if ($k -match 'TOKEN|SECRET|PASSWORD') {
                Write-Step 'ENV' ("set {0}=(redacted, len={1})" -f $k, $v.Length)
            } else {
                Write-Step 'ENV' ("set {0}={1}" -f $k, $v)
            }
        }
    }
    Write-Step 'ENV' ("Loaded {0} key(s) from cascade.env" -f $n) 'OK'
}

function Ensure-GitLabProjectId {
    if (-not [string]::IsNullOrWhiteSpace($env:GITLAB_PROJECT_ID)) {
        Write-Step 'PROJECT' ("GITLAB_PROJECT_ID already set: {0}" -f $env:GITLAB_PROJECT_ID) 'OK'
        return
    }
    Write-Step 'PROJECT' 'GITLAB_PROJECT_ID missing - will try defaults / picker' 'WARN'
    # Known SMPG CBR id used in this engagement
    $fallback = '16629'
    if ([Environment]::UserInteractive) {
        Write-Host ''
        Write-Host 'GITLAB_PROJECT_ID is not set.' -ForegroundColor Yellow
        Write-Host ("  Press Enter for default {0} (SMPG-Central-Business-Repository)" -f $fallback) -ForegroundColor Cyan
        Write-Host '  Or type a numeric project id' -ForegroundColor DarkGray
        $ans = Read-Host '  Project id'
        if ([string]::IsNullOrWhiteSpace($ans)) { $ans = $fallback }
        $env:GITLAB_PROJECT_ID = $ans.Trim()
    } else {
        $env:GITLAB_PROJECT_ID = $fallback
    }
    Write-Step 'PROJECT' ("Using GITLAB_PROJECT_ID={0}" -f $env:GITLAB_PROJECT_ID) 'OK'
    try {
        $envPath = Join-Path $RepoRoot 'config\cascade.env'
        $line = 'GITLAB_PROJECT_ID={0}' -f $env:GITLAB_PROJECT_ID
        if (Test-Path -LiteralPath $envPath) {
            $raw = Get-Content -LiteralPath $envPath -Raw
            if ($raw -match '(?m)^GITLAB_PROJECT_ID=') {
                $raw = [regex]::Replace($raw, '(?m)^GITLAB_PROJECT_ID=.*$', $line)
            } else {
                $raw = $raw.TrimEnd() + "`r`n" + $line + "`r`n"
            }
            Set-Content -Path $envPath -Value $raw -Encoding UTF8
            Write-Step 'PROJECT' 'Saved GITLAB_PROJECT_ID to cascade.env' 'OK'
        } else {
            $dir = Split-Path $envPath -Parent
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
            Set-Content -Path $envPath -Value ("# cascade local`r`n{0}`r`n" -f $line) -Encoding UTF8
            Write-Step 'PROJECT' 'Created cascade.env with GITLAB_PROJECT_ID' 'OK'
        }
    } catch {
        Write-Step 'PROJECT' ("Could not save project id: {0}" -f $_.Exception.Message) 'WARN'
    }
}

# --- main ---
try {
    Write-Host '=== Cascade AI restructure TEST (1 duplicate issue) ===' -ForegroundColor Cyan
    Write-Step 'START' ("Profile={0} SourceIid={1}" -f $Profile, $SourceIid)

    Import-LocalCascadeEnv
    if ([string]::IsNullOrWhiteSpace($env:GITLAB_URL)) {
        $env:GITLAB_URL = 'https://web.git.mil'
        Write-Step 'ENV' 'GITLAB_URL defaulted to https://web.git.mil'
    }

    Write-Step 'TOKEN' 'Resolving GITLAB_TOKEN (must be gitmil ARC item)'
    try {
        $tok = Get-CascadeGitLabToken -AllowPrompt
        if ([string]::IsNullOrWhiteSpace($tok)) { throw 'Empty GITLAB_TOKEN after resolve' }
        Write-Step 'TOKEN' ("GITLAB_TOKEN present (len={0})" -f $tok.Length) 'OK'
    } catch {
        Write-Step 'TOKEN' $_.Exception.Message 'ERROR'
        Write-Step 'TOKEN' 'Fix: [S]->GitLab->3 gitmil  OR set CASCADE_ARC_ITEM_GitLab=gitmil in cascade.env' 'ERROR'
        throw
    }

    Ensure-GitLabProjectId

    # READ source (CBR) vs WRITE sandbox
    $writePid = 0
    $tmp = 0
    if ($env:GITLAB_PROJECT_ID -and [int]::TryParse("$($env:GITLAB_PROJECT_ID)".Trim(), [ref]$tmp)) { $writePid = $tmp }
    $readPid = 0
    if ($SourceProjectId -gt 0) { $readPid = $SourceProjectId }
    elseif ($env:GITLAB_SOURCE_PROJECT_ID -and [int]::TryParse("$($env:GITLAB_SOURCE_PROJECT_ID)".Trim(), [ref]$tmp)) { $readPid = $tmp }
    if ($readPid -le 0 -or $readPid -eq 52999) { $readPid = 16629 }
    if ($writePid -eq 52999 -and $readPid -eq $writePid) { $readPid = 16629 }
    Write-Step 'PROJECT' ("READ source={0} WRITE sandbox/target={1}" -f $readPid, $writePid) 'OK'

    if (-not $Labels) {
        $Labels = if ($env:GITLAB_LABELS) { $env:GITLAB_LABELS } else { 'area::data-crucible' }
    }

    $pick = $null
    $all = New-Object System.Collections.Generic.List[object]

    # Cluster/[P] with explicit iid: fetch ONE issue from CBR - do not list sandbox
    if ($SourceIid -gt 0) {
        Write-Step 'PULL' ("GET single issue !{0} from project {1}" -f $SourceIid, $readPid)
        $url = if ($env:GITLAB_URL) { $env:GITLAB_URL.TrimEnd('/') } else { 'https://web.git.mil' }
        $tok = Get-CascadeGitLabToken -AllowPrompt
        $headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }
        $uri = "{0}/api/v4/projects/{1}/issues/{2}" -f $url, $readPid, $SourceIid
        try {
            $pick = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -TimeoutSec 120
            Write-Step 'PULL' ("Got !{0} {1}" -f $pick.iid, $pick.title) 'OK'
            [void]$all.Add($pick)
        } catch {
            Write-Step 'PULL' $_.Exception.Message 'ERROR'
            throw ("Failed to fetch source !{0} from project {1}: {2}" -f $SourceIid, $readPid, $_.Exception.Message)
        }
    } else {
        # Interactive: list from READ project (force env for Get-CascadeIssuesFromGitLab)
        $savedPid = $env:GITLAB_PROJECT_ID
        $env:GITLAB_PROJECT_ID = "$readPid"
        Write-Step 'PULL' ("GET issues state=all labels={0} project={1}" -f $Labels, $readPid)
        try {
            $raw = Get-CascadeIssuesFromGitLab -State all -Labels $Labels
            Write-Step 'PULL' 'Get-CascadeIssuesFromGitLab returned' 'OK'
        } catch {
            $env:GITLAB_PROJECT_ID = $savedPid
            Write-Step 'PULL' $_.Exception.Message 'ERROR'
            throw
        }
        $env:GITLAB_PROJECT_ID = $savedPid
        if ($raw -is [System.Array]) { foreach ($x in $raw) { if ($null -ne $x) { [void]$all.Add($x) } } }
        elseif ($null -ne $raw) { [void]$all.Add($raw) }
        Write-Step 'PULL' ("Issue count={0}" -f $all.Count) $(if ($all.Count -gt 0) { 'OK' } else { 'WARN' })
        if ($all.Count -eq 0) {
            throw "No issues returned for labels=$Labels project=$readPid (CBR). Sandbox 52999 is write-only."
        }
        $pick = $all | Where-Object { [string]$_.title -notmatch '\[CASCADE-DUP\]' } | Select-Object -First 1
        if (-not $pick) { $pick = $all | Select-Object -First 1 }
    }
    Write-Step 'PICK' ("Selected iid=#{0} title={1}" -f $pick.iid, $pick.title) 'OK'

    Write-Step 'NORM' 'Convert-CascadeGitLabIssueToNormalized -AsDuplicate'
    $norm = Convert-CascadeGitLabIssueToNormalized -Issue $pick -AsDuplicate
    $defList = @($norm.deficiencies | ForEach-Object { "$_" })
    Write-Step 'NORM' ("type={0} id={1} deficiencies={2}" -f $norm.type, $norm.id, ($defList -join ', ')) 'OK'

    $outDir = Join-Path $RepoRoot 'output'
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    $norm | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $outDir 'normalized_one_dup.json') -Encoding UTF8
    Write-Step 'PKG' 'Building AI package'
    $pkg = New-CascadeAiWorkPackage -NormalizedIssues @($norm)
    $pkg | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $outDir 'ai_package_one_dup.json') -Encoding UTF8
    Write-Step 'PKG' 'Wrote ai_package_one_dup.json' 'OK'

    Write-Step 'LLM' ("Calling profile={0}" -f $Profile)
    if (Get-Command Convert-CascadeAiPackageToChatMessages -ErrorAction SilentlyContinue) {
        $msgs = Convert-CascadeAiPackageToChatMessages -Package $pkg
        Write-Step 'LLM' 'Built messages via Convert-CascadeAiPackageToChatMessages' 'OK'
    } else {
        $sys = 'You are Cascade. OUTPUT FORMAT: prose optional, then markers:\n***<<< JSON PAYLOAD >>>***\n{json}\n***<<< END JSON >>>***\nReturn JSON with analysis and proposals. Hierarchy EPIC>FEATURE>STORY. Prefer catalog parents. Actions: set_parent, normalize_meta, draft_acceptance_criteria, draft_task_checklist.'
        $user = ($pkg | ConvertTo-Json -Depth 10 -Compress)
        if ($user.Length -gt 80000) { $user = $user.Substring(0, 80000) }
        $msgs = @(
            @{ role = 'system'; content = $sys },
            @{ role = 'user'; content = ("Source iid={0}. Return JSON only.`n{1}" -f $pick.iid, $user) }
        )
        Write-Step 'LLM' 'Built messages via inline fallback' 'WARN'
    }
    # Ensure source iid is explicit in user message
    if ($msgs.Count -ge 2 -and $msgs[1].content -notmatch [string]$pick.iid) {
        $msgs[1].content = ("Source GitLab iid={0}.`n{1}" -f $pick.iid, $msgs[1].content)
    }

    $chat = Invoke-CascadeChat -Profile $Profile -Messages $msgs -MaxTokens 20000 -Temperature 0.1
    $text = [string]$chat.Content
    if ([string]::IsNullOrWhiteSpace($text) -and $chat -is [string]) { $text = [string]$chat }
    $rawPath = Join-Path $outDir 'ai_response_one_dup_raw.txt'
    $text | Set-Content $rawPath -Encoding UTF8
    Write-Step 'LLM' ("Raw response length={0} profile={1} model={2} -> {3}" -f $text.Length, $chat.Profile, $chat.Model, $rawPath) 'OK'

    $parsed = $null
    try {
        $clean = $text.Trim()
        if ($clean -match '(?s)```(?:json)?\s*(.*?)```') { $clean = $Matches[1].Trim() }
        $parsed = $clean | ConvertFrom-Json
        $parsed | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $outDir 'ai_response_one_dup.json') -Encoding UTF8
        if ($SourceIid -gt 0) {
            $per = Join-Path $outDir ("ai_response_iid_{0}.json" -f $SourceIid)
            $parsed | ConvertTo-Json -Depth 12 | Set-Content $per -Encoding UTF8
            Write-Step 'LLM' ("Per-iid response -> {0}" -f $per) 'OK'
            # stamp analysis.iid if missing so apply matcher works
            try {
                if (-not $parsed.analysis) {
                    $parsed | Add-Member -NotePropertyName analysis -NotePropertyValue ([pscustomobject]@{ iid = $SourceIid }) -Force
                } elseif (-not $parsed.analysis.iid) {
                    $parsed.analysis | Add-Member -NotePropertyName iid -NotePropertyValue $SourceIid -Force
                }
                $parsed | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $outDir 'ai_response_one_dup.json') -Encoding UTF8
                $parsed | ConvertTo-Json -Depth 12 | Set-Content $per -Encoding UTF8
            } catch { }
        }
        Write-Step 'LLM' 'Parsed JSON OK' 'OK'
    } catch {
        Write-Step 'LLM' ("JSON parse failed: {0}" -f $_.Exception.Message) 'WARN'
        Write-Step 'LLM' 'Raw text saved; open ai_response_one_dup_raw.txt' 'WARN'
    }

    Write-Host '--- analysis ---' -ForegroundColor Cyan
    if ($parsed.analysis) { $parsed.analysis | ConvertTo-Json -Depth 6 | Write-Host }
    Write-Host '--- proposals ---' -ForegroundColor Cyan
    if ($parsed.proposals) { $parsed.proposals | ConvertTo-Json -Depth 6 | Write-Host }

    Write-Host ''
    Write-Step 'DONE' 'No GitLab writes. Next: [M] -> 5 with SAME source iid for safe dup apply.' 'OK'
    Write-Host ("Source iid for [M]->5: {0}" -f $pick.iid) -ForegroundColor Green
    Write-Host 'Step log: output\ai_restructure_steps.log' -ForegroundColor DarkGray
}
catch {
    Write-Step 'FAIL' $_.Exception.Message 'ERROR'
    Write-Step 'FAIL' ("Exception type: {0}" -f $_.Exception.GetType().FullName) 'ERROR'
    if ($_.ScriptStackTrace) {
        Write-Step 'FAIL' ($_.ScriptStackTrace -replace '\r?\n', ' | ') 'ERROR'
    }
    Write-Host ''
    Write-Host 'Diagnostics:' -ForegroundColor Yellow
    Write-Host ("  GITLAB_URL         = {0}" -f $env:GITLAB_URL)
    Write-Host ("  GITLAB_PROJECT_ID  = {0}" -f $env:GITLAB_PROJECT_ID)
    Write-Host ("  GITLAB_LABELS      = {0}" -f $env:GITLAB_LABELS)
    Write-Host ("  ARC_ITEM_GitLab    = {0}" -f $env:CASCADE_ARC_ITEM_GitLab)
    Write-Host ("  GITLAB_TOKEN set   = {0}" -f (-not [string]::IsNullOrWhiteSpace($env:GITLAB_TOKEN)))
    Write-Host '  See output\ai_restructure_steps.log' -ForegroundColor DarkGray
    throw
}
