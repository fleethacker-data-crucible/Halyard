#requires -Version 5.1
# Cascade agent tools - AI-connected, sandbox-first. Never silent CBR writes.

function Get-CascadeAgentRepoRoot {
    $root = $null
    try {
        if ($PSScriptRoot) {
            $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
            if (-not (Test-Path (Join-Path $root 'scripts'))) {
                $root = Split-Path $PSScriptRoot -Parent
            }
        }
    } catch {}
    if (-not $root) { $root = (Get-Location).Path }
    return $root
}

function Get-CascadeSandboxIssuesForAgent {
    param([int]$Max = 80, [string]$Label = 'refine::sandbox-review')
    $root = Get-CascadeAgentRepoRoot
    $envFile = Join-Path $root 'config\cascade.env'
    if (Test-Path -LiteralPath $envFile) {
        Get-Content -LiteralPath $envFile | ForEach-Object {
            if ($_ -match '^\s*([^=#]+)=(.*)$') {
                Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"')
            }
        }
    }
    Import-CascadeGitLabEnv -ErrorAction SilentlyContinue
    $url = 'https://web.git.mil'
    if ($env:GITLAB_URL) { $url = $env:GITLAB_URL.TrimEnd('/') }
    $tok = Get-CascadeGitLabToken -AllowPrompt
    if ([string]::IsNullOrWhiteSpace($tok)) { throw 'No GitLab token' }
    $headers = @{ 'PRIVATE-TOKEN' = $tok; 'Accept' = 'application/json' }
    $writeId = 52999
    try {
        $ctx = Get-CascadeWriteContext
        if ($ctx -and $ctx.WriteProjectId) { $writeId = [int]$ctx.WriteProjectId }
    } catch {}
    $base = "$url/api/v4/projects/$writeId"
    $flat = New-Object System.Collections.Generic.List[object]
    $page = 1
    do {
        $uri = "$base/issues?state=opened&per_page=50&page=$page&order_by=updated_at&sort=desc"
        if ($Label) { $uri = $uri + '&labels=' + [uri]::EscapeDataString($Label) }
        $raw = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 120
        $batch = @()
        if ($null -eq $raw) { $batch = @() }
        elseif ($raw -is [System.Array]) { $batch = @($raw) }
        else { $batch = @($raw) }
        $n = 0
        foreach ($b in $batch) {
            $n++
            if ($null -eq $b) { continue }
            if ($b -is [System.Array]) {
                foreach ($i in $b) { if ($null -ne $i -and $null -ne $i.iid) { [void]$flat.Add($i) } }
            } elseif ($null -ne $b.iid) { [void]$flat.Add($b) }
        }
        $page++
    } while (($n -eq 50) -and ($page -le 20) -and ($flat.Count -lt $Max))
    $out = New-Object System.Collections.Generic.List[object]
    $c = 0
    foreach ($x in $flat) {
        if ($c -ge $Max) { break }
        [void]$out.Add($x)
        $c++
    }
    return @{ Issues = $out.ToArray(); ProjectId = $writeId; Url = $url; Headers = $headers }
}

function Test-CascadeStoryDefinableForPoints {
    param([string]$Title, [string]$Body)
    $t = [string]$Title
    $b = [string]$Body
    if ([string]::IsNullOrWhiteSpace($t)) { return $false }
    if ($t -match '(?i)TBD|placeholder|TODO') { return $false }
    $score = 0
    if ($b.Length -gt 80) { $score++ }
    if ($b -match '(?i)acceptance\s+criteria') { $score++ }
    if ($b -match '(?i)(as a |i want |so that )') { $score++ }
    if ($b -match '(?i)(task|checklist|definition of done|done-when)') { $score++ }
    if ($b -match '(?i)cascade-meta') { $score++ }
    return ($score -ge 2)
}

function Get-CascadeMetaPointsValue {
    param($Meta)
    if ($null -eq $Meta) { return '' }
    try {
        if ($Meta.ContainsKey('points') -and -not [string]::IsNullOrWhiteSpace([string]$Meta['points'])) {
            return [string]$Meta['points']
        }
        if ($Meta.ContainsKey('story_points') -and -not [string]::IsNullOrWhiteSpace([string]$Meta['story_points'])) {
            return [string]$Meta['story_points']
        }
    } catch {}
    return ''
}


function ConvertFrom-CascadePointsAiText {
    <#
    .SYNOPSIS
      Parse story-points / effort from GenAI text.
      Handles: JSON arrays, JSON objects, markdown tables (Gemini default), regex lines.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Text,
        [string]$RunDir = '',
        [string]$Tag = 'points'
    )
    $raw = [string]$Text
    if ($RunDir -and (Test-Path -LiteralPath $RunDir)) {
        try {
            $p = Join-Path $RunDir ('ai_raw_{0}_{1}.txt' -f $Tag, [guid]::NewGuid().ToString('N').Substring(0, 8))
            [System.IO.File]::WriteAllText($p, $raw, (New-Object System.Text.UTF8Encoding $false))
        } catch {}
    }
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }

    $rows = New-Object System.Collections.Generic.List[object]

    # --- 1) JSON path ---
    $clean = $raw.Trim()
    $clean = [regex]::Replace($clean, '^\s*```(?:json|JSON)?\s*', '')
    $clean = [regex]::Replace($clean, '\s*```\s*$', '')
    $clean = $clean.Replace([char]0x201C, '"').Replace([char]0x201D, '"')
    $clean = [regex]::Replace($clean, ',\s*([\]\}])', '$1')

    $parsed = $null
    try { $parsed = $clean | ConvertFrom-Json } catch { $parsed = $null }
    if ($null -eq $parsed) {
        $m = [regex]::Match($clean, '\[[\s\S]*\]')
        if ($m.Success) {
            try { $parsed = $m.Value | ConvertFrom-Json } catch { $parsed = $null }
        }
    }
    if ($null -eq $parsed) {
        $m2 = [regex]::Match($clean, '\{[\s\S]*\}')
        if ($m2.Success) {
            try { $parsed = $m2.Value | ConvertFrom-Json } catch { $parsed = $null }
        }
    }
    if ($null -ne $parsed) {
        $list = $null
        if ($parsed -is [System.Array]) { $list = $parsed }
        else {
            foreach ($prop in @('estimates', 'items', 'results', 'data', 'stories', 'features')) {
                try {
                    if ($parsed.PSObject.Properties[$prop] -and $null -ne $parsed.$prop) {
                        $list = $parsed.$prop
                        break
                    }
                } catch {}
            }
            if ($null -eq $list) { $list = @($parsed) }
        }
        if ($list -isnot [System.Array]) { $list = @($list) }
        foreach ($row in $list) {
            if ($null -ne $row) { [void]$rows.Add($row) }
        }
    }

    # --- 2) Markdown table rows (Gemini style) ---
    # Matches lines like: | **264** | title | theme | **5** | reason |
    #                  or: | 276 | title | theme | 5 | reason |
    #                  or: | **256** | title | **2 pts** | reason |
    if ($rows.Count -eq 0) {
        $lineArr = $raw -split "`r?`n"
        foreach ($line in $lineArr) {
            $ln = [string]$line
            if ($ln -notmatch '\|') { continue }
            if ($ln -match '^\s*\|?\s*-+') { continue }  # separator
            if ($ln -match '(?i)\|\s*IID\s*\|') { continue }  # header
            if ($ln -match '(?i)\|\s*Item ID\s*\|') { continue }

            # Extract first integer cell as iid
            $iid = 0
            $mIid = [regex]::Match($ln, '\|\s*\**\s*(?:IID\s*)?(\d{2,6})\s*\**\s*\|')
            if (-not $mIid.Success) { continue }
            $iid = [int]$mIid.Groups[1].Value

            # Prefer a cell that looks like points/effort near end of row
            $points = ''
            $effort = ''

            # **5** or **3 Story Points** or **2 pts** or plain | 5 |
            $mPts = [regex]::Matches($ln, '\|\s*\**\s*(1|2|3|5|8|13|21|TBD)\s*(?:pts?|points?|story\s*points?)?\s*\**\s*\|', 'IgnoreCase')
            if ($mPts.Count -gt 0) {
                $points = $mPts[$mPts.Count - 1].Groups[1].Value
            }
            # Effort S/M/L/XL in a cell
            $mEff = [regex]::Match($ln, '\|\s*\**\s*(S|M|L|XL|XXL)\s*\**\s*(?:\([^)]*\))?\s*\|', 'IgnoreCase')
            if ($mEff.Success) {
                $effort = $mEff.Groups[1].Value.ToUpperInvariant()
            }
            # Alternate: "Recommended Effort Tag | **S**"
            if ([string]::IsNullOrWhiteSpace($effort)) {
                $mEff2 = [regex]::Match($ln, '(?i)effort[^|]*\|\s*\**\s*(S|M|L|XL|XXL)')
                if ($mEff2.Success) { $effort = $mEff2.Groups[1].Value.ToUpperInvariant() }
            }

            if ([string]::IsNullOrWhiteSpace($points) -and [string]::IsNullOrWhiteSpace($effort)) {
                # try "3 Story Points" outside strict cells
                $mLoose = [regex]::Match($ln, '(?i)\b(1|2|3|5|8|13)\s*(?:story\s*)?points?\b')
                if ($mLoose.Success) { $points = $mLoose.Groups[1].Value }
            }

            if ([string]::IsNullOrWhiteSpace($points) -and [string]::IsNullOrWhiteSpace($effort)) { continue }

            [void]$rows.Add([pscustomobject]@{
                iid        = $iid
                points     = $points
                effort     = $effort
                confidence = 'medium'
                reason     = 'parsed-from-markdown-table'
                source     = 'markdown'
            })
        }
    }

    # --- 3) Regex fallback across full text ---
    if ($rows.Count -eq 0) {
        $rx = [regex]::Matches($raw, '(?i)(?:iid\s*)?(\d{2,6})[^\n]{0,120}?(?:points?|estimate|effort)\s*[:=]?\s*\**\s*(1|2|3|5|8|13|TBD|S|M|L|XL)')
        foreach ($m in $rx) {
            $iid = [int]$m.Groups[1].Value
            $val = $m.Groups[2].Value
            [void]$rows.Add([pscustomobject]@{
                iid = $iid; points = $val; effort = $val
                confidence = 'low'; reason = 'regex-fallback'; source = 'regex'
            })
        }
    }

    return $rows.ToArray()
}

function Limit-CascadeStoryPoints {
    param([string]$Raw)
    if ([string]::IsNullOrWhiteSpace($Raw)) { return 'TBD' }
    $p = $Raw.Trim().Trim('"').ToUpperInvariant()
    $p = $p -replace 'PTS?', '' -replace 'POINTS?', '' -replace 'STORY', ''
    $p = $p.Trim()
    if ($p -match '^(1|2|3|5|8|TBD)$') { return $p }
    # Cap oversize Fibonacci into allowed set for Cascade policy
    if ($p -eq '13' -or $p -eq '21') { return '8' }
    if ($p -match '^\d+$') {
        $n = [int]$p
        if ($n -le 1) { return '1' }
        if ($n -eq 2) { return '2' }
        if ($n -eq 3) { return '3' }
        if ($n -le 5) { return '5' }
        return '8'
    }
    return 'TBD'
}

function Limit-CascadeFeatureEffort {
    param([string]$Raw)
    if ([string]::IsNullOrWhiteSpace($Raw)) { return 'TBD' }
    $e = $Raw.Trim().Trim('"').ToUpperInvariant()
    if ($e -match '^(S|M|L|TBD)$') { return $e }
    if ($e -match 'XS|X-SMALL|EXTRA\s*SMALL') { return 'S' }
    if ($e -match 'XL|XXL|EPIC') { return 'L' }  # policy: S/M/L only
    return 'TBD'
}

function Invoke-CascadeAgentScanStoryPoints {
    <#
    .SYNOPSIS
      AI-assisted story point proposals for sandbox STORYs. Local files only unless Apply is used later.
      PS 5.1 safe: no @($List) / no if($collection) binder traps.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RunDir,
        [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
        [int]$MaxStories = 25
    )
    $pack = Get-CascadeSandboxIssuesForAgent -Max 100
    $candidates = New-Object System.Collections.Generic.List[object]

    $issueArr = $pack.Issues
    if ($null -eq $issueArr) { $issueArr = @() }
    elseif ($issueArr -isnot [System.Array]) { $issueArr = [object[]]@($issueArr) }

    foreach ($iss in $issueArr) {
        if ($null -eq $iss) { continue }
        $title = [string]$iss.title
        $body = ''
        try { $body = [string]$iss.description } catch { $body = '' }
        $meta = $null
        try { $meta = Get-CascadeMeta -Body $body } catch { $meta = $null }
        $type = ''
        if ($null -ne $meta) {
            try {
                if ($meta.ContainsKey('type')) { $type = [string]$meta['type'] }
            } catch {}
        }
        if ([string]::IsNullOrWhiteSpace($type)) {
            if ($title -match '^(THEME|EPIC|FEATURE|STORY)\s*::') { $type = $Matches[1] }
        }
        if ($type -notmatch 'STORY') { continue }

        $pts = Get-CascadeMetaPointsValue -Meta $meta
        $needs = $true
        if (-not [string]::IsNullOrWhiteSpace($pts)) {
            if ($pts -notmatch '^(TBD|0|\?)$') { $needs = $false }
        }
        if (-not $needs) { continue }

        $definable = Test-CascadeStoryDefinableForPoints -Title $title -Body $body
        $snip = $body
        if ($snip.Length -gt 400) { $snip = $snip.Substring(0, 400) }
        [void]$candidates.Add([pscustomobject]@{
            iid = [int]$iss.iid
            title = $title
            definable = [bool]$definable
            bodySnippet = $snip
            currentPoints = $pts
        })
        if ($candidates.Count -ge $MaxStories) { break }
    }

    $toAi = New-Object System.Collections.Generic.List[object]
    $deferred = New-Object System.Collections.Generic.List[object]
    for ($ci = 0; $ci -lt $candidates.Count; $ci++) {
        $c = $candidates[$ci]
        if ($c.definable) { [void]$toAi.Add($c) } else { [void]$deferred.Add($c) }
    }

    $estimates = New-Object System.Collections.Generic.List[object]
    for ($di = 0; $di -lt $deferred.Count; $di++) {
        $d = $deferred[$di]
        [void]$estimates.Add([pscustomobject]@{
            iid = [int]$d.iid
            title = [string]$d.title
            points = 'TBD'
            confidence = 'low'
            reason = 'Insufficient definition for AI estimate; human review required'
            source = 'rule'
        })
    }

    if ($toAi.Count -gt 0) {
        $batchSize = 8
        for ($i = 0; $i -lt $toAi.Count; $i += $batchSize) {
            $end = $i + $batchSize - 1
            if ($end -ge $toAi.Count) { $end = $toAi.Count - 1 }

            $chunk = New-Object System.Collections.Generic.List[object]
            for ($j = $i; $j -le $end; $j++) { [void]$chunk.Add($toAi[$j]) }

            $lineParts = New-Object System.Collections.Generic.List[string]
            for ($j = 0; $j -lt $chunk.Count; $j++) {
                $c = $chunk[$j]
                $bs = ([string]$c.bodySnippet) -replace '\s+', ' '
                [void]$lineParts.Add(('IID {0} | {1} | snippet: {2}' -f $c.iid, $c.title, $bs))
            }
            $sys = 'CRITICAL: Reply with ONLY a pure JSON array. No markdown, no tables, no prose. Allowed points: 1,2,3,5,8,TBD only (never 13). Example: [{"iid":123,"points":"3","confidence":"medium","reason":"short"}]'
            $user = "Estimate story points for these work items:`n" + ($lineParts -join "`n")

            try {
                $r = Invoke-CascadeChat -Profile $Profile -MaxTokens 2000 -TimeoutSec 180 -Messages @(
                    @{ role = 'system'; content = $sys }
                    @{ role = 'user'; content = $user }
                )
                $text = ''
                try { $text = [string]$r.Content } catch { $text = '' }
                if ([string]::IsNullOrWhiteSpace($text)) {
                    try { $text = [string](Get-CascadeChatTextFromResponse -Response $r.Raw) } catch {}
                }
                $parsedRows = @(ConvertFrom-CascadePointsAiText -Text $text -RunDir $RunDir -Tag 'story_points')
                if ($parsedRows.Count -gt 0) {
                    foreach ($row in $parsedRows) {
                        $p = ''
                        try { $p = [string]$row.points } catch { $p = '' }
                        if ([string]::IsNullOrWhiteSpace($p)) {
                            try { $p = [string]$row.story_points } catch {}
                        }
                        if ([string]::IsNullOrWhiteSpace($p)) {
                            try { $p = [string]$row.estimate } catch {}
                        }
                        $p = $p.Trim().Trim('"')
                        $p = Limit-CascadeStoryPoints -Raw $p
                        $iidVal = 0
                        try { $iidVal = [int]$row.iid } catch { $iidVal = 0 }
                        if ($iidVal -eq 0) { continue }
                        $conf = 'medium'; try { if ($row.confidence) { $conf = [string]$row.confidence } } catch {}
                        $rsn = ''; try { $rsn = [string]$row.reason } catch {}
                        $src = 'ai'; try { if ($row.source) { $src = [string]$row.source } } catch {}
                        [void]$estimates.Add([pscustomobject]@{
                            iid = $iidVal
                            title = ''
                            points = $p
                            confidence = $conf
                            reason = $rsn
                            source = $src
                        })
                    }
                } else {
                    Write-Host '    [!] AI returned text but JSON parse failed - see ai_raw_*.txt in run folder' -ForegroundColor Yellow
                    if ($text.Length -gt 0) {
                        Write-Host ('    preview: {0}' -f $text.Substring(0, [Math]::Min(180, $text.Length))) -ForegroundColor DarkGray
                    }
                    for ($j = 0; $j -lt $chunk.Count; $j++) {
                        $c = $chunk[$j]
                        [void]$estimates.Add([pscustomobject]@{
                            iid = [int]$c.iid; title = [string]$c.title; points = 'TBD'
                            confidence = 'low'; reason = 'AI response parse failed'; source = 'fallback'
                        })
                    }
                }
            } catch {
                $errMsg = $_.Exception.Message
                for ($j = 0; $j -lt $chunk.Count; $j++) {
                    $c = $chunk[$j]
                    [void]$estimates.Add([pscustomobject]@{
                        iid = [int]$c.iid; title = [string]$c.title; points = 'TBD'
                        confidence = 'low'; reason = ('AI error: {0}' -f $errMsg); source = 'fallback'
                    })
                }
            }
        }
    }

    # Also scan FEATURES for effort (S/M/L) - same session goal support
    $featureEffort = New-Object System.Collections.Generic.List[object]
    foreach ($iss in $issueArr) {
        if ($null -eq $iss) { continue }
        $title = [string]$iss.title
        $body = ''; try { $body = [string]$iss.description } catch {}
        $meta = $null; try { $meta = Get-CascadeMeta -Body $body } catch {}
        $type = ''
        if ($null -ne $meta) {
            try { if ($meta.ContainsKey('type')) { $type = [string]$meta['type'] } } catch {}
        }
        if ([string]::IsNullOrWhiteSpace($type) -and ($title -match '^(FEATURE)\s*::')) { $type = 'FEATURE' }
        if ($type -notmatch 'FEATURE') { continue }
        $eff = ''
        if ($null -ne $meta) {
            try {
                if ($meta.ContainsKey('effort')) { $eff = [string]$meta['effort'] }
                elseif ($meta.ContainsKey('effort_band')) { $eff = [string]$meta['effort_band'] }
            } catch {}
        }
        $needsE = ([string]::IsNullOrWhiteSpace($eff) -or ($eff -match '^(TBD|\?)$'))
        if (-not $needsE) { continue }
        $definable = ($body.Length -gt 80) -or ($title.Length -gt 20)
        [void]$featureEffort.Add([pscustomobject]@{
            iid = [int]$iss.iid
            title = $title
            definable = [bool]$definable
            bodySnippet = $(if ($body.Length -gt 300) { $body.Substring(0,300) } else { $body })
        })
        if ($featureEffort.Count -ge 15) { break }
    }

    $effortEstimates = New-Object System.Collections.Generic.List[object]
    $featAi = New-Object System.Collections.Generic.List[object]
    for ($fi = 0; $fi -lt $featureEffort.Count; $fi++) {
        $f = $featureEffort[$fi]
        if ($f.definable) { [void]$featAi.Add($f) }
        else {
            [void]$effortEstimates.Add([pscustomobject]@{
                iid=[int]$f.iid; title=[string]$f.title; effort='TBD'; source='rule'
                reason='Insufficient definition'
            })
        }
    }
    if ($featAi.Count -gt 0) {
        $lineParts = New-Object System.Collections.Generic.List[string]
        for ($j = 0; $j -lt $featAi.Count; $j++) {
            $f = $featAi[$j]
            [void]$lineParts.Add(('IID {0} | {1} | {2}' -f $f.iid, $f.title, (($f.bodySnippet -replace '\s+',' '))))
        }
        $sys = 'CRITICAL: Reply with ONLY a pure JSON array. No markdown, no tables. Allowed effort: S, M, L, TBD only (never XL). Example: [{"iid":1,"effort":"M","reason":"short"}]'
        $user = "Estimate FEATURE effort:`n" + ($lineParts -join "`n")
        try {
            $r = Invoke-CascadeChat -Profile $Profile -MaxTokens 1500 -TimeoutSec 180 -Messages @(
                @{ role='system'; content=$sys }
                @{ role='user'; content=$user }
            )
            $text = ''; try { $text = [string]$r.Content } catch {}
            if ([string]::IsNullOrWhiteSpace($text)) {
                try { $text = [string](Get-CascadeChatTextFromResponse -Response $r.Raw) } catch {}
            }
            $parsedRows = @(ConvertFrom-CascadePointsAiText -Text $text -RunDir $RunDir -Tag 'feature_effort')
            if ($parsedRows.Count -eq 0) {
                Write-Host '    [!] Feature effort AI parse failed - see ai_raw_*.txt' -ForegroundColor Yellow
            }
            foreach ($row in $parsedRows) {
                $e = ''
                try { $e = [string]$row.effort } catch {}
                if ([string]::IsNullOrWhiteSpace($e)) { try { $e = [string]$row.points } catch {} }
                $e = $e.Trim().Trim('"').ToUpperInvariant()
                $e = Limit-CascadeFeatureEffort -Raw $e
                $iidVal = 0; try { $iidVal = [int]$row.iid } catch {}
                if ($iidVal -eq 0) { continue }
                $rsn = ''; try { $rsn = [string]$row.reason } catch {}
                $src = 'ai'; try { if ($row.source) { $src = [string]$row.source } } catch {}
                [void]$effortEstimates.Add([pscustomobject]@{
                    iid=$iidVal; title=''; effort=$e; reason=$rsn; source=$src
                })
            }
        } catch {
            $errMsg = $_.Exception.Message
            for ($j = 0; $j -lt $featAi.Count; $j++) {
                $f = $featAi[$j]
                [void]$effortEstimates.Add([pscustomobject]@{
                    iid = [int]$f.iid
                    title = [string]$f.title
                    effort = 'TBD'
                    reason = ('AI error: {0}' -f $errMsg)
                    source = 'fallback'
                })
            }
        }
    }

$estArr = $estimates.ToArray()
    $effArr = $effortEstimates.ToArray()

    $result = New-Object System.Collections.Specialized.OrderedDictionary
    [void]$result.Add('generated_at', (Get-Date).ToString('o'))
    [void]$result.Add('project_id', [int]$pack.ProjectId)
    [void]$result.Add('candidate_count', [int]$candidates.Count)
    [void]$result.Add('ai_count', [int]$toAi.Count)
    [void]$result.Add('deferred_count', [int]$deferred.Count)
    [void]$result.Add('estimates', $estArr)
    [void]$result.Add('feature_effort', $effArr)
    [void]$result.Add('note', 'Local proposals. ApplySandboxPoints writes story points only; effort apply is separate.')

    $path = Join-Path $RunDir 'points_estimates.json'
    $jsonText = $result | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($path, $jsonText, (New-Object System.Text.UTF8Encoding $false))

    $md = New-Object System.Collections.Generic.List[string]
    [void]$md.Add('# Story points and feature effort proposals (local)')
    [void]$md.Add('')
    [void]$md.Add(('Stories: candidates={0} ai={1} deferred={2}' -f $candidates.Count, $toAi.Count, $deferred.Count))
    [void]$md.Add('')
    [void]$md.Add('## Story points')
    for ($i = 0; $i -lt $estArr.Length; $i++) {
        $e = $estArr[$i]
        [void]$md.Add(('- !{0} -> **{1}** ({2}) {3}' -f $e.iid, $e.points, $e.source, $e.reason))
    }
    [void]$md.Add('')
    [void]$md.Add('## Feature effort')
    for ($i = 0; $i -lt $effArr.Length; $i++) {
        $e = $effArr[$i]
        [void]$md.Add(('- !{0} -> **{1}** ({2}) {3}' -f $e.iid, $e.effort, $e.source, $e.reason))
    }
    $mdPath = Join-Path $RunDir 'points_estimates.md'
    [System.IO.File]::WriteAllText($mdPath, ($md -join "`r`n"), (New-Object System.Text.UTF8Encoding $false))

    Write-Host ("    Points estimates: {0}  Feature effort: {1}" -f $estArr.Length, $effArr.Length) -ForegroundColor Green
    return [pscustomobject]@{
        path = $path
        mdPath = $mdPath
        count = [int]$estArr.Length
        ai = [int]$toAi.Count
        effortCount = [int]$effArr.Length
    }
}

function Invoke-CascadeAgentSuggestParents {
    param([Parameter(Mandatory)][string]$RunDir, [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi', [int]$Max = 30)
    $pack = Get-CascadeSandboxIssuesForAgent -Max 100
    $need = New-Object System.Collections.Generic.List[object]
    $features = New-Object System.Collections.Generic.List[object]
    foreach ($iss in $pack.Issues) {
        if ($null -eq $iss) { continue }
        $title = [string]$iss.title
        $body = ''; try { $body = [string]$iss.description } catch {}
        $meta = $null; try { $meta = Get-CascadeMeta -Body $body } catch {}
        $type = ''
        if ($meta -and $meta['type']) { $type = [string]$meta['type'] }
        elseif ($title -match '^(THEME|EPIC|FEATURE|STORY)\s*::') { $type = $Matches[1] }
        $parent = ''
        if ($meta -and $meta['parent']) { $parent = [string]$meta['parent'] }
        $id = ''
        if ($meta -and $meta['id']) { $id = [string]$meta['id'] }
        if ($type -match 'FEATURE') {
            [void]$features.Add([pscustomobject]@{ iid=[int]$iss.iid; id=$id; title=$title })
        }
        if (($type -match 'STORY|FEATURE') -and [string]::IsNullOrWhiteSpace($parent)) {
            [void]$need.Add([pscustomobject]@{ iid=[int]$iss.iid; type=$type; id=$id; title=$title })
            if ($need.Count -ge $Max) { break }
        }
    }

    $suggestions = New-Object System.Collections.Generic.List[object]
    # Heuristic: first feature as weak default + AI when available
    $featList = ($features | Select-Object -First 15 | ForEach-Object { '{0} ({1})' -f $_.title, $_.id }) -join '; '

    if ($need.Count -gt 0 -and (Get-Command Invoke-CascadeChat -ErrorAction SilentlyContinue)) {
        $lines = ($need | ForEach-Object { 'IID {0} TYPE {1} TITLE {2}' -f $_.iid, $_.type, $_.title }) -join "`n"
        $sys = 'Suggest parent ids for Cascade hierarchy Theme>Epic>Feature>Story. Reply ONLY JSON array [{"iid":1,"parent":"FEATURE-xyz","note":"why"}]. Use existing feature ids when possible: ' + $featList
        try {
            $r = Invoke-CascadeChat -Profile $Profile -MaxTokens 3000 -TimeoutSec 180 -Messages @(
                @{ role='system'; content=$sys }
                @{ role='user'; content=$lines }
            )
            $text = [string]$r.Content
            $clean = $text -replace '^\s*```(?:json)?\s*', '' -replace '\s*```\s*$', ''
            $parsed = $null
            try { $parsed = $clean | ConvertFrom-Json } catch {
                $m = [regex]::Match($clean, '\[(?:[^\[\]]|\[(?:[^\[\]]|\[[^\[\]]*\])*\])*\]', 'Singleline')
                if ($m.Success) { try { $parsed = $m.Value | ConvertFrom-Json } catch {} }
            }
            if ($null -ne $parsed) {
                foreach ($row in @($parsed)) {
                    [void]$suggestions.Add([pscustomobject]@{
                        iid=[int]$row.iid; parent=[string]$row.parent; note=[string]$row.note; source='ai'
                    })
                }
            }
        } catch {
            Write-Host ("Parent AI skip: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }

    if ($suggestions.Count -eq 0) {
        $defaultParent = 'FEATURE-unassigned'
        if ($features.Count -gt 0 -and $features[0].id) { $defaultParent = [string]$features[0].id }
        foreach ($n in $need) {
            [void]$suggestions.Add([pscustomobject]@{
                iid=$n.iid; parent=$defaultParent; note='Heuristic placeholder - human review'; source='heuristic'
            })
        }
    }

    $result = [ordered]@{
        generated_at = (Get-Date).ToString('o')
        need_count = $need.Count
        suggestions = $suggestions.ToArray()
        note = 'Local parent suggestions only. Apply via ApplySandboxParents after review.'
    }
    $path = Join-Path $RunDir 'parent_suggestions.json'
    [System.IO.File]::WriteAllText($path, ($result | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding $false))
    return [pscustomobject]@{ path = $path; count = $suggestions.Count }
}

function Update-CascadeBodyPoints {
    <#
    .SYNOPSIS
      Keep cascade-meta and human-visible sections in sync when setting story points.
      Meta is source of truth; mirror/sizing must not stay TBD after apply.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Body,
        [Parameter(Mandatory)][string]$Points,
        [string]$Sprint = ''
    )
    $newBody = [string]$Body
    $pts = [string]$Points

    # 1) cascade-meta: points: line
    if ($newBody -match '(?im)^\s*points:\s*\S+') {
        $newBody = [regex]::Replace($newBody, '(?im)^\s*points:\s*\S+', ('points: {0}' -f $pts))
    }
    elseif ($newBody -match '(?i)~~~cascade-meta') {
        $newBody = [regex]::Replace(
            $newBody,
            '(?i)(~~~cascade-meta\s*)',
            ('$1points: {0}{1}' -f $pts, [Environment]::NewLine),
            1
        )
    }
    else {
        $newBody = $newBody + ("`r`n`r`npoints: {0}`r`n" -f $pts)
    }

    # 2) ### Cascade mirror - Points / Sprint line
    if ($newBody -match '(?im)^\s*-\s*\*\*Points\s*/\s*Sprint:\*\*') {
        $sprintPart = 'TBD'
        if (-not [string]::IsNullOrWhiteSpace($Sprint)) { $sprintPart = $Sprint }
        elseif ($newBody -match '(?im)^\s*sprint:\s*(\S+)') {
            $sprintPart = $Matches[1]
            if ($sprintPart -eq 'TBD' -or [string]::IsNullOrWhiteSpace($sprintPart)) { $sprintPart = 'TBD' }
        }
        $newBody = [regex]::Replace(
            $newBody,
            '(?im)^\s*-\s*\*\*Points\s*/\s*Sprint:\*\*\s*.*$',
            ('- **Points / Sprint:** {0} / {1}' -f $pts, $sprintPart)
        )
    }
    # Story points bullet (alternate mirror style)
    if ($newBody -match '(?im)^\s*-\s*\*\*Story points:\*\*') {
        $newBody = [regex]::Replace(
            $newBody,
            '(?im)^\s*-\s*\*\*Story points:\*\*\s*.*$',
            ('- **Story points:** {0}' -f $pts)
        )
    }
    elseif ($newBody -match '(?im)^\s*-\s*\*\*Points:\*\*') {
        $newBody = [regex]::Replace(
            $newBody,
            '(?im)^\s*-\s*\*\*Points:\*\*\s*.*$',
            ('- **Points:** {0}' -f $pts)
        )
    }

    # 3) ## Sizing section (common bulk-push template)
    $newBody = [regex]::Replace(
        $newBody,
        '(?im)^(\s*[\*\-]\s*Points:\s*)TBD\b(.*)$',
        { param($m) return ($m.Groups[1].Value + $pts + $m.Groups[2].Value) }
    )

    return $newBody
}

function Update-CascadeBodyEffort {
    param(
        [Parameter(Mandatory)][string]$Body,
        [Parameter(Mandatory)][string]$Effort
    )
    $newBody = [string]$Body
    $eff = [string]$Effort

    if ($newBody -match '(?im)^\s*effort:\s*\S+') {
        $newBody = [regex]::Replace($newBody, '(?im)^\s*effort:\s*\S+', ('effort: {0}' -f $eff))
    }
    elseif ($newBody -match '(?i)~~~cascade-meta') {
        $newBody = [regex]::Replace(
            $newBody,
            '(?i)(~~~cascade-meta\s*)',
            ('$1effort: {0}{1}' -f $eff, [Environment]::NewLine),
            1
        )
    }

    if ($newBody -match '(?im)^\s*-\s*\*\*Effort:\*\*') {
        $newBody = [regex]::Replace(
            $newBody,
            '(?im)^\s*-\s*\*\*Effort:\*\*\s*.*$',
            ('- **Effort:** {0}' -f $eff)
        )
    }
    $newBody = [regex]::Replace(
        $newBody,
        '(?im)^(\s*[\*\-]\s*Effort:\s*)TBD\b(.*)$',
        { param($m) return ($m.Groups[1].Value + $eff + $m.Groups[2].Value) }
    )
    return $newBody
}

function Invoke-CascadeAgentApplySandboxPoints {
    param(
        [Parameter(Mandatory)][string]$EstimatesPath,
        [switch]$DryRun,
        [switch]$AlsoEffort
    )
    if (-not (Test-Path -LiteralPath $EstimatesPath)) { throw "Missing estimates file: $EstimatesPath" }
    $data = Get-Content -LiteralPath $EstimatesPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $pack = Get-CascadeSandboxIssuesForAgent -Max 150
    $null = Assert-CascadeWriteTarget -ProjectId $pack.ProjectId
    $byIid = @{}
    foreach ($iss in $pack.Issues) {
        if ($null -ne $iss -and $null -ne $iss.iid) {
            $byIid[[int]$iss.iid] = $iss
        }
    }
    $updated = 0
    $skipped = 0

    $estList = @()
    try { $estList = @($data.estimates) } catch { $estList = @() }
    foreach ($e in $estList) {
        if ($null -eq $e) { continue }
        $iid = 0
        try { $iid = [int]$e.iid } catch { continue }
        $pts = [string]$e.points
        if ($pts -eq 'TBD' -or [string]::IsNullOrWhiteSpace($pts)) { $skipped++; continue }
        if (-not $byIid.ContainsKey($iid)) { $skipped++; continue }
        $iss = $byIid[$iid]
        $body = [string]$iss.description
        $newBody = Update-CascadeBodyPoints -Body $body -Points $pts

        if ($DryRun) {
            Write-Host ("DRYRUN would set !{0} points={1} (meta+mirror+sizing)" -f $iid, $pts) -ForegroundColor DarkGray
            $updated++
            continue
        }
        $uri = "{0}/api/v4/projects/{1}/issues/{2}" -f $pack.Url, $pack.ProjectId, $iid
        $payload = @{ description = $newBody } | ConvertTo-Json -Depth 5
        try {
            Invoke-RestMethod -Method Put -Uri $uri -Headers $pack.Headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 60 | Out-Null
            Write-Host ("  Updated sandbox !{0} points={1} (synced meta + visible body)" -f $iid, $pts) -ForegroundColor Green
            $updated++
        } catch {
            Write-Host ("  Failed !{0}: {1}" -f $iid, $_.Exception.Message) -ForegroundColor Red
            $skipped++
        }
    }

    if ($AlsoEffort) {
        $effList = @()
        try { $effList = @($data.feature_effort) } catch { $effList = @() }
        foreach ($e in $effList) {
            if ($null -eq $e) { continue }
            $iid = 0
            try { $iid = [int]$e.iid } catch { continue }
            $eff = [string]$e.effort
            if ($eff -eq 'TBD' -or [string]::IsNullOrWhiteSpace($eff)) { $skipped++; continue }
            if (-not $byIid.ContainsKey($iid)) { $skipped++; continue }
            $iss = $byIid[$iid]
            # refresh body from dict if we already updated same iid (unlikely for features)
            $body = [string]$iss.description
            $newBody = Update-CascadeBodyEffort -Body $body -Effort $eff
            if ($DryRun) {
                Write-Host ("DRYRUN would set !{0} effort={1}" -f $iid, $eff) -ForegroundColor DarkGray
                $updated++
                continue
            }
            $uri = "{0}/api/v4/projects/{1}/issues/{2}" -f $pack.Url, $pack.ProjectId, $iid
            $payload = @{ description = $newBody } | ConvertTo-Json -Depth 5
            try {
                Invoke-RestMethod -Method Put -Uri $uri -Headers $pack.Headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 60 | Out-Null
                Write-Host ("  Updated sandbox !{0} effort={1} (synced meta + visible body)" -f $iid, $eff) -ForegroundColor Green
                $updated++
            } catch {
                Write-Host ("  Failed effort !{0}: {1}" -f $iid, $_.Exception.Message) -ForegroundColor Red
                $skipped++
            }
        }
    }

    return [pscustomobject]@{ updated = $updated; skipped = $skipped; dryRun = [bool]$DryRun }
}

function Invoke-CascadeAgentPromoteCbr {
    param([string]$Reason = '')
    $mode = 'sandbox'
    try { $mode = Get-CascadeWriteMode } catch {}
    if ($mode -ne 'production') {
        return [pscustomobject]@{
            allowed = $false
            message = 'CBR promote blocked: CASCADE_WRITE_MODE is not production. Set mode + CASCADE_ALLOW_PRODUCTION=1 and explicit human confirm. Default remains sandbox-only.'
        }
    }
    if ($env:CASCADE_ALLOW_PRODUCTION -ne '1') {
        return [pscustomobject]@{
            allowed = $false
            message = 'CBR promote blocked: CASCADE_ALLOW_PRODUCTION is not 1.'
        }
    }
    return [pscustomobject]@{
        allowed = $false
        message = ('CBR promote is not auto-executed in this build. Export a diff/plan for human apply. Reason logged: {0}' -f $Reason)
    }
}
