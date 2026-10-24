#requires -Version 5.1
<#
.SYNOPSIS
  Single process: ARC + Cascade. Do not Ctrl+C out of ARC - use ARC menu Exit, then Cascade [Q].
#>
$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $RepoRoot
try { $Host.UI.RawUI.WindowTitle = 'Cascade + ARC' } catch { }

function Write-Banner {
    Write-Host ''
    Write-Host ' ========================================================' -ForegroundColor Cyan
    Write-Host '   CASCADE + ARC  (one session - do not Ctrl+C)' -ForegroundColor Cyan
    Write-Host ' ========================================================' -ForegroundColor Cyan
    Write-Host ("   Cascade : {0}" -f $RepoRoot) -ForegroundColor Gray
    Write-Host '   Tip: leave ARC with menu [5] Exit Vault, not Ctrl+C' -ForegroundColor DarkGray
}

function Import-EnvFile {
    $envFile = Join-Path $RepoRoot 'config\cascade.env'
    if (-not (Test-Path -LiteralPath $envFile)) {
        Write-Host '   config\cascade.env : (none)' -ForegroundColor DarkGray
        return
    }
    Get-Content -LiteralPath $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or $line -eq '') { return }
        if ($line -match '^\s*([^=]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"').Trim("'")
        }
    }
    Write-Host '   config\cascade.env : loaded' -ForegroundColor DarkGray
}

function Get-ArcRootPath {
    if ($env:ARC_PATH -and (Test-Path -LiteralPath (Join-Path $env:ARC_PATH 'pm.ps1'))) {
        return [IO.Path]::GetFullPath($env:ARC_PATH)
    }
    $sibling = [IO.Path]::GetFullPath((Join-Path $RepoRoot '..\arc'))
    if (Test-Path -LiteralPath (Join-Path $sibling 'pm.ps1')) { return $sibling }
    if (Get-Command Find-CascadeArcRoot -ErrorAction SilentlyContinue) {
        $f = Find-CascadeArcRoot
        if ($f) { return $f }
    }
    return $null
}

function Test-GenapiReadable {
    if (-not (Get-Command Get-PmStarkApiKey -ErrorAction SilentlyContinue)) { return $false }
    try {
        $k = Get-PmStarkApiKey -VaultName 'genapi'
        return -not [string]::IsNullOrWhiteSpace($k)
    } catch {
        return $false
    }
}

function Initialize-Session {
    Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
    Write-Host '   Cascade module    : loaded' -ForegroundColor Green
    try {
        $wm = if ($env:CASCADE_WRITE_MODE) { $env:CASCADE_WRITE_MODE } else { 'sandbox' }
        Write-Host ("   CASCADE_WRITE_MODE : {0}" -f $wm) -ForegroundColor DarkGray
    } catch { }

    $arcRoot = Get-ArcRootPath
    if (-not $arcRoot) {
        Write-Host '   ARC               : not found' -ForegroundColor Yellow
        return
    }
    Write-Host ("   ARC               : {0}" -f $arcRoot) -ForegroundColor Green

    try {
        if (Import-CascadeArcSession -ArcRoot $arcRoot) {
            Write-Host '   ARC               : module loaded' -ForegroundColor Green
        } else {
            Write-Host '   ARC               : load failed' -ForegroundColor Red
            return
        }
    } catch {
        Write-Host ("   ARC load failed   : {0}" -f $_.Exception.Message) -ForegroundColor Red
        return
    }

    # If genapi already readable, skip menu entirely
    if (Test-GenapiReadable) {
        Write-Host '   ARC vault         : unlocked (genapi OK)' -ForegroundColor Green
        return
    }

    Write-Host '   ARC vault         : genapi not readable yet' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '   [U] Open ARC menu to unlock   [S] Skip' -ForegroundColor Cyan
    Write-Host '   After unlock: use ARC option [5] Exit Vault (returns here).' -ForegroundColor DarkGray
    Write-Host '   Do NOT press Ctrl+C (that kills the window).' -ForegroundColor DarkGray
    $c = Read-Host '   Choice'
    if ($c -ne 'U' -and $c -ne 'u') {
        Write-Host '   Skipped - set GENAI_TOKEN in cascade.env or paste later.' -ForegroundColor DarkGray
        return
    }

    Write-Host ''
    Write-Host '   >>> ARC menu: unlock if needed, then [5] Exit Vault <<<' -ForegroundColor Cyan
    try {
        # Isolate so ARC exit cannot tear down our host via unexpected codes
        if (Get-Command pm -ErrorAction SilentlyContinue) {
            pm
        } else {
            & $pm
        }
    } catch {
        Write-Host ("   ARC returned: {0}" -f $_.Exception.Message) -ForegroundColor DarkGray
    }

    if (Test-GenapiReadable) {
        Write-Host '   ARC vault         : unlocked (genapi OK)' -ForegroundColor Green
    } else {
        Write-Host '   genapi still not readable. Check: pm -List for item name genapi' -ForegroundColor Yellow
    }
}


function Read-CascadeMenu {
    param([string]$Prompt = 'Select')
    $c = Read-Host $Prompt
    if ($null -eq $c) { return 'B' }
    $c = $c.Trim()
    if ($c -eq '') { return 'B' }
    # Single-letter menu keys are case-insensitive input, normalized to upper
    if ($c.Length -eq 1 -and $c -match '^[A-Za-z]$') {
        return $c.ToUpperInvariant()
    }
    if ($c -eq 'q' -or $c -eq 'Q') { return 'Q' }
    if ($c -eq 'b' -or $c -eq 'B') { return 'B' }
    return $c
}

function Invoke-Choice {
    param([string]$Choice)
    $scripts = Join-Path $RepoRoot 'scripts'
    switch -CaseSensitive -Exact ($Choice) {
        '1' { & (Join-Path $scripts 'Invoke-LocalSync.ps1') }
        '2' { & (Join-Path $scripts 'Invoke-LocalMigrateDryRun.ps1') }
        '3' { & (Join-Path $scripts 'Show-SampleMeta.ps1') }
        '4' { & (Join-Path $RepoRoot 'tests\Invoke-CascadeTests.ps1') -Suite Menu }
        '5' { & (Join-Path $scripts 'Invoke-LiveSync.ps1') }
        'I' {
            & (Join-Path $scripts 'Invoke-ChatIntake.ps1') -Profile GenAi
        }
        'Y' {
            & (Join-Path $scripts 'Invoke-TestGitLabGraphQL.ps1')
        }
        'G' {
            $null = Import-CascadeArcSession
            Test-CascadeGitLabConnection -SampleIssues 8 | Out-Null
        }
        '6' {
            Write-Host 'Host shell is already open. Examples:' -ForegroundColor Green
            Write-Host '  Test-CascadeLlmConnection -Profile GenAi'
            Write-Host '  Get-PmStarkApiKey -VaultName genapi'
        }
        '7' {
            Write-Host '  [1] Grok  [2] GenAi/genapi  [3] Auto  [B] Back  [Q] Quit'
            $lp = Read-CascadeMenu '  Profile'
            if ($lp -eq 'Q') { return 'QUIT' }
            if ($lp -eq 'B') { return }
            $p = 'Auto'
            if ($lp -eq '1') { $p = 'Grok' }
            if ($lp -eq '2') { $p = 'GenAi' }
            & (Join-Path $scripts 'Invoke-TestLlm.ps1') -Profile $p
        }
        '8' {
            Write-Host '  [1] Grok  [2] GenAi/genapi  [3] Auto  [B] Back  [Q] Quit'
            $lp = Read-CascadeMenu '  Profile'
            if ($lp -eq 'Q') { return 'QUIT' }
            if ($lp -eq 'B') { return }
            $p = 'Grok'
            if ($lp -eq '2') { $p = 'GenAi' }
            if ($lp -eq '3') { $p = 'Auto' }
            if ($lp -eq '1') { $p = 'Grok' }
            Write-Host '  Suggest parents uses small batches (default 8) to avoid LLM timeouts.' -ForegroundColor DarkGray
            & (Join-Path $scripts 'Invoke-SuggestParents.ps1') -Profile $p
        }
        '9' { & (Join-Path $scripts 'Invoke-PlanToJson.ps1') -BuildPackage }
        'P' {
            Write-Host '  AI restructure TEST on 1 duplicated issue (no Git write)' -ForegroundColor Cyan
            Write-Host '  [1] GenAi  [2] Grok  [3] Auto  [B] Back  [Q] Quit'
            $lp = Read-CascadeMenu '  Profile'
            if ($lp -eq 'Q') { return 'QUIT' }
            if ($lp -eq 'B') { return }
            $p = 'GenAi'
            if ($lp -eq '2') { $p = 'Grok' }
            if ($lp -eq '3') { $p = 'Auto' }
            if ($lp -eq '1') { $p = 'GenAi' }
            & (Join-Path $scripts 'Invoke-AiRestructureTest.ps1') -Profile $p
            Write-Host ''
            Write-Host 'Next SAFE write: [M] then [5], source iid above, type YES. Review dup, then delete it.' -ForegroundColor Cyan
            return
        }
        'M' {
            Write-Host '  [1] Merge parent_suggestions.json into migrate_plan.csv' -ForegroundColor Cyan
            Write-Host '  [2] Preview one plan row (metadata block, no Git write)'
            Write-Host '  [3] Apply ONE approved row to GitLab (writes original iid!)'
            Write-Host '  [4] DUPLICATE source iid then apply thin meta only (safe test)' -ForegroundColor DarkGray
            Write-Host '  [5] DUPLICATE + apply AI proposals (AC, checklist, parent, Story body)' -ForegroundColor Green
            Write-Host '  [B] Back  [Q] Quit'
            $m = Read-CascadeMenu '  Choice'
            if ($m -eq 'Q') { return 'QUIT' }
            if ($m -eq 'B') { return }
            if ($m -eq '1') { & (Join-Path $scripts 'Invoke-MergeParentSuggestions.ps1') }
            elseif ($m -eq '2') {
                $iid = Read-Host '  Iid (blank = first row with parent)'
                if ($iid) { & (Join-Path $scripts 'Invoke-MigrateApplyPreview.ps1') -Iid ([int]$iid) }
                else { & (Join-Path $scripts 'Invoke-MigrateApplyPreview.ps1') }
            }
            elseif ($m -eq '3') {
                $iid = Read-Host '  Iid to apply to GitLab (WARNING: real issue)'
                if (-not $iid) { Write-Host 'Cancelled' -ForegroundColor Yellow }
                else {
                    $yn = Read-Host '  This WRITES to the REAL issue. Type YES to continue'
                    if ($yn -eq 'YES') {
                        & (Join-Path $scripts 'Invoke-MigrateApplyPreview.ps1') -Iid ([int]$iid) -Apply -Force
                    }
                }
            }
            elseif ($m -eq '4') {
                $sid = Read-Host '  Source iid to clone (e.g. 253)'
                if (-not $sid) { Write-Host 'Cancelled' -ForegroundColor Yellow }
                else {
                    Write-Host '  [a] Create duplicate only' -ForegroundColor Cyan
                    Write-Host '  [b] Create duplicate AND apply Cascade meta to the NEW issue'
                    $mode = Read-Host '  Mode'
                    if ($mode -eq 'b' -or $mode -eq 'B') {
                        & (Join-Path $scripts 'Invoke-DuplicateAndApplyTest.ps1') -SourceIid ([int]$sid) -ApplyMeta
                    } else {
                        & (Join-Path $scripts 'Invoke-DuplicateAndApplyTest.ps1') -SourceIid ([int]$sid)
                    }
                }
            }
            elseif ($m -eq '5') {
                $sid = Read-Host '  Source iid (run [P] first)'
                if (-not $sid) { Write-Host 'Cancelled' -ForegroundColor Yellow }
                else {
                    & (Join-Path $scripts 'Invoke-ApplyAiProposalsToDup.ps1') -SourceIid ([int]$sid)
                }
            }
        }
        'm' {
            & (Join-Path $scripts 'Invoke-MergeParentSuggestions.ps1')
        }
        'A' { Initialize-Session }
        'S' {
            Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
            $null = Import-CascadeArcSession
            $cat = @(Get-CascadeArcSecretCatalog -TryLoadArc)
            if ($cat.Count -eq 0) {
                Write-Host 'No secrets listed. Unlock ARC first ([A]), then [S].' -ForegroundColor Yellow
            } else {
                Write-Host ''
                Write-Host 'ARC vault items:' -ForegroundColor Cyan
                $cat | Format-Table Index, Service, User, Type, HasSecret -AutoSize
                Write-Host 'Map a secret for this session:' -ForegroundColor Cyan
                Write-Host '  [1] GenAi (api.genai.mil)'
                Write-Host '  [2] Grok / Databricks'
                Write-Host '  [3] GitLab (web.git.mil)'
                Write-Host '  [B] Back'
                $m = Read-CascadeMenu 'Map (B=back Q=quit)'
                if ($m -eq 'Q') { return 'QUIT' }
                if ($m -eq 'B') { return }
                switch ($m) {
                    '1' { $null = Select-CascadeArcSecret -Purpose 'GenAi / api.genai.mil' -EnvTarget 'GENAI_TOKEN' -RememberKey 'GenAi' -AllowSkip }
                    '2' { $null = Select-CascadeArcSecret -Purpose 'Grok / Databricks' -EnvTarget 'DATABRICKS_TOKEN' -RememberKey 'Grok' -AllowSkip }
                    '3' { $null = Select-CascadeArcSecret -Purpose 'GitLab / web.git.mil' -EnvTarget 'GITLAB_TOKEN' -RememberKey 'GitLab' -AllowSkip }
                }
            }
        }
        's' {
            # alias of S - call same path via re-entry is unsafe; duplicate light path
            $null = Import-CascadeArcSession
            $cat = @(Get-CascadeArcSecretCatalog -TryLoadArc)
            if ($cat.Count -eq 0) {
                Write-Host 'No secrets listed. Unlock ARC first ([A]).' -ForegroundColor Yellow
            } else {
                $cat | Format-Table Index, Service, User, Type, HasSecret -AutoSize
                Write-Host '  [1] GenAi  [2] Grok  [3] GitLab  [B] Back  [Q] Quit'
                $m = Read-CascadeMenu 'Map'
                if ($m -eq 'Q') { return 'QUIT' }
                if ($m -eq 'B') { return }
                switch ($m) {
                    '1' { $null = Select-CascadeArcSecret -Purpose 'GenAi / api.genai.mil' -EnvTarget 'GENAI_TOKEN' -RememberKey 'GenAi' -AllowSkip }
                    '2' { $null = Select-CascadeArcSecret -Purpose 'Grok / Databricks' -EnvTarget 'DATABRICKS_TOKEN' -RememberKey 'Grok' -AllowSkip }
                    '3' { $null = Select-CascadeArcSecret -Purpose 'GitLab / web.git.mil PAT (gitmil)' -EnvTarget 'GITLAB_TOKEN' -RememberKey 'GitLab' -AllowSkip }
                }
            }
        }
        'R' {
            & (Join-Path $scripts 'Invoke-CascadeGapReport.ps1')
            return
        }
        'W' {
            # File picker (OpenFileDialog); path arg optional
            & (Join-Path $scripts 'Invoke-ReadmeToWorkItem.ps1') -Profile GenAi
        }
        'C' {
            & (Join-Path $scripts 'Invoke-ClusterInventory.ps1') -Profile GenAi
        }
        'T' {
            Write-Host '  Sandbox project for Cascade test writes' -ForegroundColor Cyan
            Write-Host '  [1] Use existing aws-infrastructure'
            Write-Host '  [2] Create cascade-test-sandbox under sm-dats-sagemaker'
            Write-Host '  [3] Custom existing path'
            $t = Read-Host '  Choice'
            if ($t -eq '1') {
                & (Join-Path $scripts 'Invoke-CreateSandboxProject.ps1') -UseExistingPath 'nmmes_bip/sm-dats-sagemaker/aws-infrastructure' -SetCascadeEnv
            } elseif ($t -eq '2') {
                & (Join-Path $scripts 'Invoke-CreateSandboxProject.ps1') -GroupPath 'nmmes_bip/sm-dats-sagemaker' -ProjectName 'cascade-test-sandbox' -SetCascadeEnv
            } elseif ($t -eq '3') {
                $p = Read-Host '  project path (group/sub/name)'
                if ($p) { & (Join-Path $scripts 'Invoke-CreateSandboxProject.ps1') -UseExistingPath $p.Trim() -SetCascadeEnv }
            }
            $src = Read-Host '  Source project id for READ (CBR=16629 recommended) - blank keeps/sets 16629'
            if (-not $src -or $src.Trim() -eq '') { $src = '16629' }
            if ($src -match '^\d+$') {
                if ($src -eq '52999') {
                    Write-Host '  52999 is sandbox WRITE only - forcing READ 16629' -ForegroundColor Yellow
                    $src = '16629'
                }
                $env:GITLAB_SOURCE_PROJECT_ID = $src
                $envPath = Join-Path $RepoRoot 'config\cascade.env'
                $line = "GITLAB_SOURCE_PROJECT_ID=$src"
                if (Test-Path $envPath) {
                    $raw = Get-Content $envPath -Raw
                    if ($raw -match '(?m)^GITLAB_SOURCE_PROJECT_ID=') {
                        $raw = [regex]::Replace($raw, '(?m)^GITLAB_SOURCE_PROJECT_ID=.*$', $line)
                    } else { $raw = $raw.TrimEnd() + "`r`n$line`r`n" }
                    Set-Content $envPath $raw -Encoding UTF8
                }
                Write-Host ("READ source project={0} WRITE sandbox={1}" -f $src, $env:GITLAB_PROJECT_ID) -ForegroundColor Green
            }
        }
        'K' {
            & (Join-Path $scripts 'Invoke-ClusterTestApply.ps1') -Profile GenAi
        }
        'F' {
            & (Join-Path $scripts 'Invoke-BacklogRefine.ps1')
        }
        'L' {
            & (Join-Path $scripts 'Invoke-BulkRefineSample.ps1')
        }
        'J' {
            & (Join-Path $scripts 'Invoke-BulkPushSandbox.ps1')
        }
        'E' {
            & (Join-Path $scripts 'Invoke-BacklogAssess.ps1')
        }
        'N' {
            & (Join-Path $scripts 'Invoke-AgentGoal.ps1')
        }
        'U' {
            & (Join-Path $scripts 'Invoke-SeedTaxonomy.ps1')
        }
        'X' {
            & (Join-Path $scripts 'Invoke-SandboxWipe.ps1')
        }
        'r' {
            & (Join-Path $scripts 'Invoke-CascadeGapReport.ps1')
            return
        }
        'w' {
            & (Join-Path $scripts 'Invoke-ReadmeToWorkItem.ps1') -Profile GenAi
        }
        'c' {
            & (Join-Path $scripts 'Invoke-ClusterInventory.ps1') -Profile GenAi
        }
        default { Write-Host 'Unknown' -ForegroundColor Yellow }
    }
}

Write-Banner
Import-EnvFile
if ($env:GENAI_TOKEN) { Write-Host '   GENAI_TOKEN       : set (GenAi only)' -ForegroundColor DarkGray }
if ($env:DATABRICKS_TOKEN) { Write-Host '   DATABRICKS_TOKEN  : set (Grok only)' -ForegroundColor DarkGray }
Initialize-Session

while ($true) {
    Write-Host ''
    Write-Host ' --------------------------------------------------------' -ForegroundColor DarkCyan
    Write-Host '  [1] Local sync          [2] Migrate dry-run'
    Write-Host '  [3] Sample meta         [4] Tests (schema/GitLab/LLM)'
    Write-Host '  [5] Live Git sync       [6] Shell tips'
    Write-Host '  [7] Test LLM            [8] Suggest parents'
    Write-Host '      (LLM I/O saved under output/llm_transcripts/)' -ForegroundColor DarkGray
    Write-Host '  [9] Plan to JSON        [P] AI restructure TEST (1 dup issue)'
    Write-Host '  [M] Merge AI parents / preview / apply one'
    Write-Host '  [A] ARC unlock'
    Write-Host '  [S] ARC secrets (list & map to GenAi/Grok/GitLab)'
    Write-Host '  [G] Test GitLab API     (web.git.mil pull)'
    Write-Host '  [Y] Test GitLab GraphQL (read-only probe)'
    Write-Host '  [I] Chat intake (describe work -> create in Git)'
    Write-Host '  [R] Gap report (READ source/CBR - not sandbox)'
    Write-Host '  [W] README.md -> Story/Feature proposals'
    Write-Host '  [C] Cluster inventory (AI group Stories; dry-run)'
    Write-Host '  [T] Sandbox repo (create or point GITLAB_PROJECT_ID)'
    Write-Host '  [U] Seed taxonomy (Themes + Cascade Epic in sandbox)'
    Write-Host '  [F] Backlog refine (1-at-a-time CURRENT vs PROPOSED)'
    Write-Host '  [L] Bulk refine SAMPLE (CBR table - read-only)'
    Write-Host '  [J] Rebuild sandbox from bulk/CBR (schema + PfW; upsert by source_iid)'
    Write-Host '  [E] Assess backlog (findings + report pack)'
    Write-Host '  [N] Agent Goal (plan / approve / local act)'
    Write-Host '  [K] Cluster TEST apply (Epic>Feature>Story nested)'
    Write-Host '  [X] Sandbox WIPE (lists items for review, then confirm)'
    Write-Host '  [Q] Quit Cascade (safe exit)'
    Write-Host '  Errors: output/cascade_last_error.txt (paste that file when reporting)' -ForegroundColor DarkGray
    Write-Host '  [B] Back (submenus only; on main use Q to exit)' -ForegroundColor DarkGray
    Write-Host ' --------------------------------------------------------' -ForegroundColor DarkCyan
    $choice = Read-CascadeMenu 'Select'
    if ($choice -eq 'Q') {
        Write-Host 'Exiting Cascade safely. Goodbye.' -ForegroundColor Green
        break
    }
    # B = Back in submenus only; on main fall through (unknown unless mapped)
    if ($choice -eq 'B') {
        Write-Host '  [B] is Back in submenus. Use [F]/[L]/[J] for refine/push, [Q] to quit.' -ForegroundColor DarkGray
        continue
    }
    try {
        $rc = Invoke-Choice -Choice $choice
        if ($rc -eq 'QUIT') {
            Write-Host 'Exiting Cascade safely. Goodbye.' -ForegroundColor Green
            break
        }
    } catch {
        Write-Host ("Error: {0}" -f $_.Exception.Message) -ForegroundColor Red
        try {
            $errDir = Join-Path $RepoRoot 'output'
            if (-not (Test-Path $errDir)) { New-Item -ItemType Directory -Path $errDir -Force | Out-Null }
            $errPath = Join-Path $errDir 'cascade_last_error.txt'
            $detail = $_.Exception.ToString()
            if ($_.ScriptStackTrace) { $detail = $detail + "`r`n" + $_.ScriptStackTrace }
            @(
                '=== CASCADE ERROR LOG ==='
                ("timestamp : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
                ("message   : {0}" -f $_.Exception.Message)
                '--- detail ---'
                $detail
                '=== END ==='
            ) -join "`r`n" | Set-Content -Path $errPath -Encoding UTF8
            Write-Host ("Error log -> {0}" -f $errPath) -ForegroundColor Yellow
        } catch {}
        if ($_.Exception.Message -match '401') {
            Write-Host 'If GenAi 401: refresh ARC genapi key or [S] Skip paste. If GitLab 401: [S] map gitmil then [G].' -ForegroundColor Yellow
        }
    }
}

Write-Host 'Cascade session ended. Window can be closed.' -ForegroundColor DarkGray
