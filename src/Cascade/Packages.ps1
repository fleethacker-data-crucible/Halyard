# Cascade packages: CSV <-> JSON and LLM wire formats

function Convert-CascadePlanCsvToJson {
    <#
    .SYNOPSIS
      Read migrate_plan.csv (or any plan CSV) into structured JSON objects.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PlanCsv,
        [string]$OutPath
    )
    if (-not (Test-Path -LiteralPath $PlanCsv)) { throw "Plan CSV not found: $PlanCsv" }

    $rows = @(Import-Csv -LiteralPath $PlanCsv)
    $items = foreach ($r in $rows) {
        # tolerate BOM on first column name
        $iid = $null
        foreach ($k in @('Iid', 'iid', 'IID')) {
            if ($r.PSObject.Properties.Name -contains $k -and $r.$k) { $iid = $r.$k; break }
        }
        if (-not $iid) {
            $first = $r.PSObject.Properties | Select-Object -First 1
            if ($first) { $iid = $first.Value }
        }

        [pscustomobject]@{
            iid              = [int]("$iid" -replace '[^\d]', '')
            current_title    = [string]$r.CurrentTitle
            proposed_title   = [string]$r.ProposedTitle
            proposed_type    = [string]$r.ProposedType
            proposed_id      = [string]$r.ProposedId
            proposed_parent  = [string]$r.ProposedParent
            already_has_meta = [bool]("$($r.AlreadyHasMeta)" -match '^(?i)true|1|yes$')
            milestone        = [string]$r.Milestone
            labels           = @([string]$r.Labels -split ';' | Where-Object { $_ })
        }
    }

    $doc = [pscustomobject]@{
        schema      = 'cascade.plan.v1'
        generated   = (Get-Date).ToString('o')
        source_csv  = $PlanCsv
        count       = @($items).Count
        items       = @($items)
    }

    if ($OutPath) {
        $dir = Split-Path -Parent $OutPath
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $doc | ConvertTo-Json -Depth 8 | Set-Content -Path $OutPath -Encoding UTF8
        Write-Host "Wrote $OutPath ($($doc.count) items)"
    }
    return $doc
}

function Get-CascadeHierarchyCatalog {
    [CmdletBinding()]
    param([string]$Path)
    if (-not $Path) {
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        if (-not $root) { $root = Get-Location }
        $Path = Join-Path $root 'fixtures\hierarchy_catalog.json'
        if (-not (Test-Path $Path)) {
            $Path = Join-Path (Get-Location) 'fixtures\hierarchy_catalog.json'
        }
    }
    if (-not (Test-Path -LiteralPath $Path)) { throw "Hierarchy catalog not found: $Path" }
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function New-CascadeParentPackage {
    <#
    .SYNOPSIS
      Build JSON package for LLM parent assignment (Story/Task -> Feature; Feature -> Epic).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$PlanDoc,
        [object]$Catalog,
        [int]$MaxItems = 8,
        [string]$OutPath
    )

    if (-not $Catalog) { $Catalog = Get-CascadeHierarchyCatalog }

    $featureLines = @($Catalog.features | ForEach-Object {
            '{0} | {1} | epic={2}' -f $_.id, $_.title, $_.epic
        })
    $epicLines = @($Catalog.epics | ForEach-Object {
            '{0} | {1} | features=[{2}]' -f $_.id, $_.title, ($_.features -join ', ')
        })

    $needParent = @($PlanDoc.items | Where-Object {
            $_.proposed_type -match 'STORY|TASK' -and [string]::IsNullOrWhiteSpace($_.proposed_parent)
        } | Select-Object -First $MaxItems)

    $needEpic = @($PlanDoc.items | Where-Object {
            $_.proposed_type -eq 'FEATURE' -and (
                [string]::IsNullOrWhiteSpace($_.proposed_parent) -or $_.proposed_parent -notmatch '^EPIC-'
            )
        })

    $pkg = [pscustomobject]@{
        schema            = 'cascade.package.suggest_parents.v1'
        task              = 'suggest_parents'
        generated         = (Get-Date).ToString('o')
        allowed_epics     = @($Catalog.epics)
        allowed_features  = @($Catalog.features)
        epic_catalog_text = ($epicLines -join "`n")
        feature_catalog_text = ($featureLines -join "`n")
        issues_needing_feature_parent = @($needParent)
        features_needing_epic_parent  = @($needEpic)
        constraints       = [pscustomobject]@{
            story_task_parent_must_be_feature = $true
            feature_parent_must_be_epic       = $true
            ids_must_come_from_catalog        = $true
            response_format                   = 'json_array of {iid, parent, note}'
        }
    }

    if ($OutPath) {
        $pkg | ConvertTo-Json -Depth 10 | Set-Content -Path $OutPath -Encoding UTF8
        Write-Host "Wrote package $OutPath"
    }
    return $pkg
}

function Convert-CascadePackageToChatMessages {
    <#
    .SYNOPSIS
      Turn parent package into system/user messages for Invoke-CascadeChat.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Package)

    $system = @(
        'You are Cascade, an AI-agile assistant for SMPG GitLab work items.',
        'Hierarchy: THEME > EPIC > FEATURE > STORY. Effort on FEATURE (Small|Medium|Large). Points+sprint on STORY. Tasks embedded in Story. Milestone is orthogonal. Prefer catalog ids when provided.',
        'NEVER write phrases like AI first pass, draft for human, or revise if scope changes. Write professional backlog text only.',
        'STORY proposals must include: parent (Feature id), acceptance_criteria (testable Given/When/Then style where possible), task_checklist (3-7 steps specific to THIS story), points (Fibonacci 1|2|3|5|8|13), time_estimate_hours (integer hours for GitLab time estimate), dwi_markdown (full Defined Work Instruction markdown: purpose, steps, environments, verification, rollback), title_clean (short professional title without typos).',
        'FEATURE proposals / create_feature must include: parent epic, short_description (2-4 sentences from associated stories), effort (Small|Medium|Large|Extra Large plus optional fte_weeks number), definition_of_done (3-6 bullets), proposal_markdown (full Proposal for Work: Problem, Scope in, Scope out, Approach, Risks, Capacity/Effort, Links), children list if known.',
        'EPIC create_epic must include: outcome (2-4 sentences), success_picture (3-5 bullets), lifecycle (ALPHA|BETA|LIVE|SUSTAINING|SUNSETTING), health (ON_TRACK|WATCH|AT_RISK|OFF_TRACK|BLOCKED|PARKED|IDLE), metrics signals (counts not points), children feature ids.',
        'Effort lives on FEATURE (size and optional FTE weeks). Story carries points + time_estimate_hours only - no effort_band on Stories.',
        'Titles: STORY :: short title; FEATURE :: Name; EPIC :: Name. No CASCADE-DUP in titles.',
        'Proposal for Work must be substantial when context allows: executive summary, problem, options, recommendation, scope in/out, technical approach (stack/scripts if known), risks, effort. If context is only one thin Story, state open questions for Technical Focal/SME instead of inventing architecture.',
        'Default product_owner is Cascade AI. Leave technical_focal and sme empty unless known.',
        'Do not write human must accept, Normalized Cascade metadata, or process narration notes.',
        'Feature effort is Small, Medium, Large, or Extra Large only - never say T-shirt. Put effort in effort field only, not inside proposal_markdown.',
        'CRITICAL OUTPUT FORMAT: Put the JSON between markers exactly:\n***<<< JSON PAYLOAD >>>***\n{ ... your json ... }\n***<<< END JSON >>>***\nAny prose must be OUTSIDE the markers. Cascade parses only between markers.',
        'JSON must include keys analysis and proposals. No secrets.'
    ) -join ' '

    $issueBits = @()
    foreach ($i in @($Package.issues_needing_feature_parent)) {
        $issueBits += ('IID={0} type={1} title={2} milestone={3} labels={4}' -f `
                $i.iid, $i.proposed_type, $i.proposed_title, $i.milestone, ($i.labels -join ';'))
    }
    foreach ($i in @($Package.features_needing_epic_parent)) {
        $issueBits += ('IID={0} type=FEATURE title={1} current_parent={2}' -f `
                $i.iid, $i.proposed_title, $i.proposed_parent)
    }

    $user = @(
        'ALLOWED EPICS:',
        [string]$Package.epic_catalog_text,
        '',
        'ALLOWED FEATURES (with epic):',
        [string]$Package.feature_catalog_text,
        '',
        'ISSUES TO ASSIGN:',
        ($issueBits -join "`n")
    ) -join "`n"

    return @(
        @{ role = 'system'; content = $system }
        @{ role = 'user'; content = $user }
    )
}

function Get-CascadePlaybookText {
    [CmdletBinding()]
    param([string]$Path)
    if (-not $Path) {
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $Path = Join-Path $root 'docs\agile_playbook.md'
        if (-not (Test-Path $Path)) { $Path = Join-Path (Get-Location) 'docs\agile_playbook.md' }
    }
    if (Test-Path -LiteralPath $Path) {
        return (Get-Content -LiteralPath $Path -Raw)
    }
    return 'Cascade hierarchy: EPIC > FEATURE > STORY > TASK. Every item needs type,id,parent,state metadata.'
}

function New-CascadeAiWorkPackage {
    <#
    .SYNOPSIS
      Package playbook + hierarchy catalog + normalized issues for GenAI restructure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$NormalizedIssues,
        [object]$Catalog,
        [string]$PlaybookText,
        [string]$OutPath,
        [int]$MaxIssues = 40
    )

    if (-not $Catalog) { $Catalog = Get-CascadeHierarchyCatalog }
    if (-not $PlaybookText) {
        $pack = Get-CascadeTemplatePack
        $PlaybookText = $pack.playbook
        if ($pack.dor_rules) {
            $PlaybookText = $PlaybookText + "`n`n## DoR rules (JSON)`n" + $pack.dor_rules
        }
        # Keep story+feature templates only in package to limit tokens
        if ($pack.templates['story_template']) {
            $PlaybookText = $PlaybookText + "`n`n## Story template`n" + $pack.templates['story_template']
        }
        if ($pack.templates['feature_template']) {
            $PlaybookText = $PlaybookText + "`n`n## Feature template`n" + $pack.templates['feature_template']
        }
        if ($pack.proposal_names -and @($pack.proposal_names).Count -gt 0) {
            $PlaybookText = $PlaybookText + "`n`n## Available PfW/DWI corpus (filenames under fixtures/proposals)`n"
            $PlaybookText = $PlaybookText + ((@($pack.proposal_names) | ForEach-Object { "- $_" }) -join "`n")
            $PlaybookText = $PlaybookText + "`nIf Feature has no child Stories, propose create_story children (2-5) from Done-When / linked PfW/DWI. Prefer set_parent on existing tickets when they fit."
        }
    }

    $slice = @($NormalizedIssues | Select-Object -First $MaxIssues)

    $needMeta = @($slice | Where-Object { -not $_.already_has_meta })
    $needFeatureParent = @($slice | Where-Object {
            $_.proposed_type -match 'STORY|TASK' -and [string]::IsNullOrWhiteSpace($_.proposed_parent)
        })
    $needEpicParent = @($slice | Where-Object {
            $_.proposed_type -eq 'FEATURE' -and [string]::IsNullOrWhiteSpace($_.proposed_parent)
        })
    $features = @($slice | Where-Object { $_.proposed_type -eq 'FEATURE' })

    $epicLines = @($Catalog.epics | ForEach-Object { '{0} | {1}' -f $_.id, $_.title })
    $featureLines = @($Catalog.features | ForEach-Object {
            '{0} | {1} | epic={2}' -f $_.id, $_.title, $_.epic
        })

    $issueLines = foreach ($n in $slice) {
        [pscustomobject]@{
            iid             = $n.source_iid
            title           = $n.title
            proposed_type   = $n.proposed_type
            proposed_id     = $n.proposed_id
            proposed_parent = $n.proposed_parent
            already_has_meta = $n.already_has_meta
            deficiencies    = @($n.deficiencies)
            labels          = @($n.labels)
            milestone       = $n.milestone
        }
    }

    $pkg = [pscustomobject]@{
        schema              = 'cascade.ai_work_package.v1'
        purpose             = 'analyze_deficiencies_and_propose_hierarchy'
        generated           = (Get-Date).ToString('o')
        playbook            = $PlaybookText
        allowed_epics       = @($Catalog.epics)
        allowed_features    = @($Catalog.features)
        epic_catalog_text   = ($epicLines -join "`n")
        feature_catalog_text = ($featureLines -join "`n")
        issues              = @($issueLines)
        deficiency_summary  = [pscustomobject]@{
            missing_metadata            = $needMeta.Count
            stories_tasks_without_feature = $needFeatureParent.Count
            features_without_epic       = $needEpicParent.Count
            features_in_package         = $features.Count
        }
        constraints         = [pscustomobject]@{
            story_parent_is_feature = $true
            feature_parent_is_epic  = $true
            prefer_catalog_ids      = $true
            response_format         = 'json object with analysis[] and proposals[]'
        }
    }

    if ($OutPath) {
        $dir = Split-Path -Parent $OutPath
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $pkg | ConvertTo-Json -Depth 12 | Set-Content -Path $OutPath -Encoding UTF8
        Write-Host "Wrote AI package $OutPath"
    }
    return $pkg
}

function Convert-CascadeAiPackageToChatMessages {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Package)

    $system = @(
        'You are Cascade, an AI-agile assistant for SMPG GitLab work items.',
        'Hierarchy: THEME > EPIC > FEATURE > STORY. Sprint=14d finished slices; long bake on Epic+Milestone. Effort=Small|Medium|Large on Feature. Points+sprint on Story.',
        'Features use PfW (Proposal for Work). Stories use DWI (Defined Work Instruction) when a written instruction exists; otherwise draft AC is enough for Story DoR.',
        'For each Story proposal include: parent, acceptance_criteria (testable), task_checklist (3-7 steps specific to THIS story - not a generic template), points (Fibonacci 1|2|3|5|8|13), optional effort_band (XS|S|M|L|XL).',
        'If no suitable Feature exists in catalog, propose action create_feature with id, title, parent epic, short_description, definition_of_done (3-6 bullets), capacity_band (S/M/L), and proposal_draft (problem, scope_in, scope_out, approach, risks, done_when).',
        'For create_epic include outcome (1-3 sentences from child Features/Stories), success_picture (3-5 bullets), and optional metrics (feature_count_target, milestone_hint - NOT story points).',
        'Definition of Done (Features) replaces Done-When. Stories keep acceptance_criteria. Epic does not carry Fibonacci points; Feature capacity_band is planning only; Story points are Fibonacci.',
        'Titles: STORY :: short title; FEATURE :: Name; EPIC :: Name. No CASCADE-DUP in titles.',
        'Proposal for Work must be substantial when context allows: executive summary, problem, options, recommendation, scope in/out, technical approach (stack/scripts if known), risks, effort. If context is only one thin Story, state open questions for Technical Focal/SME instead of inventing architecture.',
        'Default product_owner is Cascade AI. Leave technical_focal and sme empty unless known.',
        'Do not write human must accept, Normalized Cascade metadata, or process narration notes.',
        'Feature effort is Small, Medium, Large, or Extra Large only - never say T-shirt. Put effort in effort field only, not inside proposal_markdown.',
        'CRITICAL OUTPUT FORMAT: Put the JSON between markers exactly:\n***<<< JSON PAYLOAD >>>***\n{ ... your json ... }\n***<<< END JSON >>>***\nAny prose must be OUTSIDE the markers. Cascade parses only between markers.',
        'JSON must include keys analysis and proposals. No secrets.'
    ) -join ' '

    $issuesJson = ($Package.issues | ConvertTo-Json -Depth 6 -Compress)
    if ($issuesJson.Length -gt 12000) {
        $issuesJson = ($Package.issues | Select-Object -First 15 | ConvertTo-Json -Depth 6 -Compress)
    }

    $user = @"
PLAYBOOK:
$($Package.playbook)

ALLOWED EPICS:
$($Package.epic_catalog_text)

ALLOWED FEATURES:
$($Package.feature_catalog_text)

DEFICIENCY COUNTS:
$($Package.deficiency_summary | ConvertTo-Json -Compress)

ISSUES (normalized JSON):
$issuesJson

Respond with JSON only. Each Story proposal should include points (1|2|3|5|8|13), effort_band optional, task_checklist tailored to the work, acceptance_criteria, parent catalog id.
{"analysis":[...],"proposals":[...]}
"@

    return @(
        @{ role = 'system'; content = $system }
        @{ role = 'user'; content = $user }
    )
}


function Get-CascadeTemplatePack {
    <#
    .SYNOPSIS
      Load playbook + DoR rules + template markdown for AI packaging.
    #>
    [CmdletBinding()]
    param()
    $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $docs = Join-Path $root 'docs'
    $fixtures = Join-Path $root 'fixtures'
    $playbook = Get-CascadePlaybookText
    $dor = $null
    $dorPath = Join-Path $fixtures 'dor_rules.json'
    if (Test-Path $dorPath) { $dor = Get-Content $dorPath -Raw }
    $templates = @{}
    $tDir = Join-Path $docs 'templates'
    if (Test-Path $tDir) {
        Get-ChildItem $tDir -Filter '*.md' | ForEach-Object {
            $templates[$_.BaseName] = Get-Content $_.FullName -Raw
        }
    }
    $propDir = Join-Path $fixtures 'proposals'
    $propIndex = @()
    if (Test-Path $propDir) {
        Get-ChildItem $propDir -Filter '*.md' | ForEach-Object { $propIndex += $_.BaseName }
    }
    return [pscustomobject]@{
        playbook         = $playbook
        dor_rules        = $dor
        templates        = $templates
        proposal_names   = $propIndex
    }
}
