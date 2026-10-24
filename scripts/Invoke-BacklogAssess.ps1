#requires -Version 5.1
# Assess-v2026-10-21b : PS 5.1 safe (no if($collection)/if($psobject) binder traps)
param(
    [ValidateSet('Sandbox','Cbr','Both')][string]$Source = 'Sandbox',
    [string]$Labels = 'area::data-crucible',
    [int]$MaxIssues = 80,
    [switch]$DraftToSandbox,
    [switch]$Force,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

function Write-AssessError {
    param([string]$Message, [string]$Detail = '')
    $path = Join-Path $outDir 'cascade_last_error.txt'
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('=== CASCADE ERROR LOG (paste this file or block) ===')
    [void]$lines.Add("timestamp : $ts")
    [void]$lines.Add('operation : Invoke-BacklogAssess')
    [void]$lines.Add("message   : $Message")
    [void]$lines.Add('script    : Assess-v2026-10-21b')
    [void]$lines.Add('--- detail ---')
    if ($Detail) { [void]$lines.Add($Detail) }
    [void]$lines.Add('=== END CASCADE ERROR LOG ===')
    try {
        [System.IO.File]::WriteAllText($path, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding $false))
    } catch {}
    Write-Host ("Error log -> {0}" -f $path) -ForegroundColor Yellow
    Write-Host $Message -ForegroundColor Red
}

function Get-SafeString {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [System.Array]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($v in $Value) { [void]$parts.Add("$v") }
        return ($parts -join "`n")
    }
    return [string]$Value
}

try {
    Write-Host '=== Cascade Assess backlog (v2026-10-21b) ===' -ForegroundColor Cyan

    Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
    foreach ($extra in @('WritePolicy.ps1','Schema.ps1','GitLab.ps1','Integrity.ps1','Logging.ps1')) {
        $p = Join-Path $RepoRoot ("src\Cascade\{0}" -f $extra)
        if (Test-Path -LiteralPath $p) { . $p }
    }

    $envFile = Join-Path $RepoRoot 'config\cascade.env'
    if (Test-Path -LiteralPath $envFile) {
        Get-Content -LiteralPath $envFile | ForEach-Object {
            if ($_ -match '^\s*([^=#]+)=(.*)$') {
                Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"')
            }
        }
    }

    $ctx = $null
    try { $ctx = Write-CascadeWriteBanner } catch {}

    Import-CascadeGitLabEnv -ErrorAction SilentlyContinue

    $url = 'https://web.git.mil'
    if ($env:GITLAB_URL) {
        $url = [string]$env:GITLAB_URL
        $url = $url.TrimEnd('/')
    }

    $tok = Get-CascadeGitLabToken -AllowPrompt
    if ([string]::IsNullOrWhiteSpace($tok)) { throw 'No GitLab token (ARC or cascade.env)' }
    $headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }

    $readId = 16629
    $writeId = 52999
    if ($null -ne $ctx) {
        try { if ($ctx.ReadProjectId) { $readId = [int]$ctx.ReadProjectId } } catch {}
        try { if ($ctx.WriteProjectId) { $writeId = [int]$ctx.WriteProjectId } } catch {}
    }

    function Get-ProjectIssues {
        param([int]$ProjectId, [string]$LabelFilter, [int]$Max)
        $base = "$url/api/v4/projects/$ProjectId"
        $flat = New-Object System.Collections.Generic.List[object]
        $page = 1
        do {
            $uri = "$base/issues?state=opened&per_page=50&page=$page&order_by=updated_at&sort=desc"
            if (-not [string]::IsNullOrWhiteSpace($LabelFilter)) {
                $uri = $uri + '&labels=' + [uri]::EscapeDataString($LabelFilter)
            }
            $raw = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 120
            $batchCount = 0
            if ($null -ne $raw) {
                $enum = @()
                if ($raw -is [System.Array]) { $enum = $raw } else { $enum = @($raw) }
                foreach ($b in $enum) {
                    $batchCount++
                    if ($null -eq $b) { continue }
                    if ($b -is [System.Array]) {
                        foreach ($i in $b) {
                            if ($null -ne $i -and $null -ne $i.iid) { [void]$flat.Add($i) }
                        }
                    } elseif ($null -ne $b.iid) {
                        [void]$flat.Add($b)
                    }
                }
            }
            $page++
        } while (($batchCount -eq 50) -and ($page -le 20) -and ($flat.Count -lt $Max))

        $result = New-Object System.Collections.Generic.List[object]
        $n = 0
        foreach ($x in $flat) {
            if ($n -ge $Max) { break }
            [void]$result.Add($x)
            $n++
        }
        return $result
    }

    $allIssues = New-Object System.Collections.Generic.List[object]

    if (($Source -eq 'Sandbox') -or ($Source -eq 'Both')) {
        Write-Host ("Fetching sandbox {0}..." -f $writeId) -ForegroundColor DarkGray
        $sb = Get-ProjectIssues -ProjectId $writeId -LabelFilter 'refine::sandbox-review' -Max $MaxIssues
        if ($sb.Count -eq 0) {
            $sb = Get-ProjectIssues -ProjectId $writeId -LabelFilter $Labels -Max $MaxIssues
        }
        foreach ($i in $sb) {
            [void]$allIssues.Add([pscustomobject]@{ project = $writeId; scope = 'sandbox'; issue = $i })
        }
    }
    if (($Source -eq 'Cbr') -or ($Source -eq 'Both')) {
        Write-Host ("Fetching CBR {0}..." -f $readId) -ForegroundColor DarkGray
        $cb = Get-ProjectIssues -ProjectId $readId -LabelFilter $Labels -Max $MaxIssues
        foreach ($i in $cb) {
            [void]$allIssues.Add([pscustomobject]@{ project = $readId; scope = 'cbr'; issue = $i })
        }
    }

    Write-Host ("Issues loaded: {0}" -f $allIssues.Count) -ForegroundColor Cyan
    if ($allIssues.Count -eq 0) {
        Write-Host 'Nothing to assess.' -ForegroundColor Yellow
        return
    }

    # Build plain Object[] for integrity (avoid List binder issues)
    $issueList = New-Object System.Collections.Generic.List[object]
    foreach ($row in $allIssues) {
        if ($null -eq $row) { continue }
        if ($null -eq $row.issue) { continue }
        if ($row.issue -is [System.Array]) { continue }
        [void]$issueList.Add($row.issue)
    }
    $issueArr = $issueList.ToArray()

    $integrity = $null
    $integrityValid = $true
    try {
        if (Get-Command Test-CascadeHierarchyIntegrity -ErrorAction SilentlyContinue) {
            $integrity = Test-CascadeHierarchyIntegrity -Issues $issueArr
            if ($null -ne $integrity) {
                try {
                    $integrityValid = [bool]($integrity.IsValid)
                } catch {
                    $integrityValid = $true
                }
            }
        }
    } catch {
        Write-Host ("Integrity check skipped: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        $integrity = $null
        $integrityValid = $true
    }

    $findings = New-Object System.Collections.Generic.List[object]

    foreach ($row in $allIssues) {
        if ($null -eq $row) { continue }
        $iss = $row.issue
        if ($null -eq $iss) { continue }
        if ($iss -is [System.Array]) { continue }

        $iid = 0
        try { $iid = [int]$iss.iid } catch { $iid = 0 }

        $title = Get-SafeString $iss.title
        $body = ''
        try {
            if ($null -ne $iss.PSObject.Properties['description']) {
                $body = Get-SafeString $iss.description
            }
        } catch { $body = '' }

        $meta = $null
        try { $meta = Get-CascadeMeta -Body $body } catch { $meta = $null }

        $type = ''
        if ($null -ne $meta) {
            try {
                if ($meta.ContainsKey('type')) { $type = Get-SafeString $meta['type'] }
            } catch {
                try { $type = Get-SafeString $meta.type } catch { $type = '' }
            }
        }
        if ([string]::IsNullOrWhiteSpace($type)) {
            if ($title -match '^(THEME|EPIC|FEATURE|STORY)\s*::') { $type = $Matches[1] }
        }

        if ([string]::IsNullOrWhiteSpace($title)) {
            [void]$findings.Add([pscustomobject]@{
                scope = [string]$row.scope; project = [int]$row.project; iid = $iid
                code = 'MISSING_TITLE'; severity = 'High'; detail = 'No title'; title = ''
            })
        }
        if ([string]::IsNullOrWhiteSpace($body) -or ($body.Length -lt 40)) {
            [void]$findings.Add([pscustomobject]@{
                scope = [string]$row.scope; project = [int]$row.project; iid = $iid
                code = 'THIN_DESCRIPTION'; severity = 'Medium'; detail = 'Description missing or very short'; title = $title
            })
        }
        if (($type -match 'STORY') -or ($title -match '(?i)STORY\s*::')) {
            if ($body -notmatch '(?i)acceptance\s+criteria') {
                [void]$findings.Add([pscustomobject]@{
                    scope = [string]$row.scope; project = [int]$row.project; iid = $iid
                    code = 'MISSING_AC'; severity = 'Medium'; detail = 'Story lacks Acceptance criteria'; title = $title
                })
            }
        }
        if (($type -match 'FEATURE') -or ($title -match '(?i)FEATURE\s*::')) {
            if ($body -notmatch '(?i)(definition of done|done-when|Proposal for Work)') {
                [void]$findings.Add([pscustomobject]@{
                    scope = [string]$row.scope; project = [int]$row.project; iid = $iid
                    code = 'FEATURE_THIN'; severity = 'Medium'; detail = 'Feature lacks DoD or PfW'; title = $title
                })
            }
        }
        if ($null -ne $meta) {
            $parentVal = ''
            try {
                if ($meta.ContainsKey('parent')) { $parentVal = Get-SafeString $meta['parent'] }
            } catch {}
            if (($parentVal -eq '') -and ($type -match 'FEATURE|STORY')) {
                [void]$findings.Add([pscustomobject]@{
                    scope = [string]$row.scope; project = [int]$row.project; iid = $iid
                    code = 'MISSING_PARENT'; severity = 'Medium'; detail = 'parent meta empty'; title = $title
                })
            }
        }
        if (($body -notmatch '(?i)cascade-meta') -and ($body -notmatch '<!--')) {
            [void]$findings.Add([pscustomobject]@{
                scope = [string]$row.scope; project = [int]$row.project; iid = $iid
                code = 'NO_CASCADE_META'; severity = 'Low'; detail = 'No cascade-meta block'; title = $title
            })
        }
    }

    # Merge integrity findings WITHOUT if ($collection) boolean coercion
    if ($null -ne $integrity) {
        $intFindings = $null
        try { $intFindings = $integrity.Findings } catch { $intFindings = $null }
        if ($null -ne $intFindings) {
            # Enumerate via for-loop compatible path
            $tmp = New-Object System.Collections.Generic.List[object]
            try {
                foreach ($f in $intFindings) { [void]$tmp.Add($f) }
            } catch {
                try {
                    $c = [int]$intFindings.Count
                    for ($i = 0; $i -lt $c; $i++) { [void]$tmp.Add($intFindings[$i]) }
                } catch {}
            }
            foreach ($f in $tmp) {
                if ($null -eq $f) { continue }
                $fCode = ''; $fDetail = ''; $fId = ''
                try { $fCode = Get-SafeString $f.Code } catch {}
                try { $fDetail = Get-SafeString $f.Detail } catch {}
                try { $fId = Get-SafeString $f.Id } catch {}
                [void]$findings.Add([pscustomobject]@{
                    scope = 'integrity'; project = 0; iid = 0
                    code = $fCode; severity = 'High'; detail = $fDetail; title = $fId
                })
            }
        }
    }

    $findingsArr = $findings.ToArray()

    $pack = New-Object System.Collections.Specialized.OrderedDictionary
    [void]$pack.Add('generated_at', (Get-Date).ToString('o'))
    [void]$pack.Add('source', $Source)
    [void]$pack.Add('issue_count', [int]$allIssues.Count)
    [void]$pack.Add('integrity_valid', [bool]$integrityValid)
    [void]$pack.Add('finding_count', [int]$findings.Count)
    [void]$pack.Add('findings', $findingsArr)
    [void]$pack.Add('recommendations', [string[]]@(
        'Review High severity findings first.',
        'Use [J] -FromCbrDirect to refresh schema mirrors (upsert by source_iid).',
        'Use [N] Agent Goal for multi-step local proposals.',
        'Do not edit CBR until promote is explicit.'
    ))
    [void]$pack.Add('script_version', 'Assess-v2026-10-21b')

    $jsonPath = Join-Path $outDir 'assess_findings.json'
    $mdPath = Join-Path $outDir 'assess_report.md'

    try {
        $jsonText = $pack | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($jsonPath, $jsonText, (New-Object System.Text.UTF8Encoding $false))
    } catch {
        $msg = $_.Exception.Message -replace '"', "'"
        [System.IO.File]::WriteAllText($jsonPath, ('{{"error":"{0}"}}' -f $msg), (New-Object System.Text.UTF8Encoding $false))
    }

    $mdLines = New-Object System.Collections.Generic.List[string]
    [void]$mdLines.Add('# Cascade backlog assessment')
    [void]$mdLines.Add('')
    [void]$mdLines.Add(('- Script: Assess-v2026-10-21b'))
    [void]$mdLines.Add(('- Generated: {0}' -f $pack['generated_at']))
    [void]$mdLines.Add(('- Source: {0}' -f $Source))
    [void]$mdLines.Add(('- Issues: {0}' -f $pack['issue_count']))
    [void]$mdLines.Add(('- Findings: {0}' -f $pack['finding_count']))
    [void]$mdLines.Add(('- Integrity valid: {0}' -f $pack['integrity_valid']))
    [void]$mdLines.Add('')
    [void]$mdLines.Add('## Findings')

    # Manual sort by severity then code (avoid Sort-Object binder edge cases)
    $sevOrder = @{ 'High' = 0; 'Medium' = 1; 'Low' = 2 }
    $indexed = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $findingsArr.Length; $i++) {
        $f = $findingsArr[$i]
        $s = 'Low'
        try { $s = [string]$f.severity } catch {}
        $ord = 9
        if ($sevOrder.ContainsKey($s)) { $ord = [int]$sevOrder[$s] }
        $c = ''
        try { $c = [string]$f.code } catch {}
        [void]$indexed.Add([pscustomobject]@{ Ord = $ord; Code = $c; Idx = $i; Item = $f })
    }
    $sortedIdx = $indexed | Sort-Object Ord, Code, Idx
    if ($null -eq $sortedIdx) { $sortedIdx = @() }

    foreach ($entry in $sortedIdx) {
        $f = $entry.Item
        $sev = Get-SafeString $f.severity
        $code = Get-SafeString $f.code
        $scope = Get-SafeString $f.scope
        $fid = Get-SafeString $f.iid
        $det = Get-SafeString $f.detail
        [void]$mdLines.Add(('- **[{0}]** `{1}` {2} !{3} - {4}' -f $sev, $code, $scope, $fid, $det))
    }

    [System.IO.File]::WriteAllText($mdPath, ($mdLines -join "`r`n"), (New-Object System.Text.UTF8Encoding $false))

    Write-Host ''
    Write-Host '=== Assess summary ===' -ForegroundColor Cyan
    Write-Host ("Findings: {0}  IntegrityValid: {1}" -f $findings.Count, $integrityValid)
    Write-Host ("JSON: {0}" -f $jsonPath)
    Write-Host ("Report: {0}" -f $mdPath)

    $show = 0
    foreach ($f in $findingsArr) {
        if ($show -ge 25) { break }
        Write-Host ("  [{0}] {1} {2} !{3}" -f $f.severity, $f.code, $f.scope, $f.iid)
        $show++
    }
    Write-Host 'Assess complete.' -ForegroundColor Green
}
catch {
    $detail = $_.Exception.ToString()
    try {
        if ($_.InvocationInfo) {
            $detail = $detail + "`r`nAt line: " + $_.InvocationInfo.ScriptLineNumber
            $detail = $detail + "`r`nLine: " + $_.InvocationInfo.Line
        }
        if ($_.ScriptStackTrace) { $detail = $detail + "`r`n" + $_.ScriptStackTrace }
    } catch {}
    Write-AssessError -Message $_.Exception.Message -Detail $detail
    throw
}
