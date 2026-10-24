<#
.SYNOPSIS
  README.md -> Story and/or Feature proposal (and optional safe create in current project).
.PARAMETER ReadmePath
  Path to README.md
.PARAMETER PreferType
  STORY | FEATURE | AUTO
.PARAMETER ApplyDup
  If set, after propose, offer [M]-style safe create (YES gate)
#>
param(
    [string]$ReadmePath = '',
    [ValidateSet('STORY','FEATURE','AUTO')][string]$PreferType = 'AUTO',
    [ValidateSet('Auto','Grok','GenAi')][string]$Profile = 'GenAi',
    [switch]$ApplyDup
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot



# load cascade.env
$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^=#]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"')
        }
    }
}


if ([string]::IsNullOrWhiteSpace($ReadmePath)) {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dlg = New-Object System.Windows.Forms.OpenFileDialog
        $dlg.Filter = 'Markdown (*.md)|*.md|All files (*.*)|*.*'
        $dlg.Title = 'Select README.md for Cascade'
        $dlg.CheckFileExists = $true
        if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Host 'Cancelled - no file selected.' -ForegroundColor Yellow
            exit 0
        }
        $ReadmePath = $dlg.FileName
        Write-Host ("Selected: {0}" -f $ReadmePath) -ForegroundColor DarkGray
    } catch {
        $ReadmePath = Read-Host 'OpenFileDialog unavailable - path to README.md'
        if ([string]::IsNullOrWhiteSpace($ReadmePath)) { throw 'No README path' }
    }
}

if (-not (Test-Path -LiteralPath $ReadmePath)) {
    throw "README not found: $ReadmePath"
}
$readmeText = Get-Content -LiteralPath $ReadmePath -Raw -Encoding UTF8
if ($readmeText.Length -gt 120000) {
    $readmeText = $readmeText.Substring(0, 120000) + "`n...[truncated]..."
}

$catalogPath = Join-Path $RepoRoot 'fixtures\hierarchy_catalog.json'
$catalogJson = if (Test-Path $catalogPath) { Get-Content $catalogPath -Raw } else { '{}' }

$system = @'
You are Cascade. From a README, propose GitLab work items for SMPG medallion delivery.
OUTPUT FORMAT: prose optional, then markers:\n***<<< JSON PAYLOAD >>>***\n{json}\n***<<< END JSON >>>***\nReturn JSON: { "analysis": "...", "proposals": [ ... ] }
Each proposal action one of: create_story, create_feature, create_epic, set_parent.
Titles: "STORY :: ...", "FEATURE :: ...", "EPIC :: ..." - no CASCADE-DUP prefix.
Feature effort: Small|Medium|Large|Extra Large only.
Story: points (Fibonacci), time_estimate_hours, acceptance_criteria[], task_checklist[], dwi_markdown, parent feature id from catalog when possible.
Feature: short_description, definition_of_done[], proposal_markdown (substantial), effort, parent epic.
If no Epic fits catalog, create_epic. Prefer existing FEATURE-B/G/GO or standing FEATURE-IM/RD/PI/DB.
product_owner default Cascade AI. No process narration notes.
'@

$user = @"
PreferType: $PreferType
Hierarchy catalog (JSON):
$catalogJson

README path: $ReadmePath

README content:
$readmeText
"@

Write-Host "README -> work item via $Profile ..." -ForegroundColor Cyan
$msgs = @(
    @{ role = 'system'; content = $system },
    @{ role = 'user'; content = $user }
)
$chat = Invoke-CascadeChat -Profile $Profile -Messages $msgs -MaxTokens 20000 -Temperature 0.15
$text = $chat.Content
if (-not $text) { $text = [string]$chat }

# extract JSON
$jsonText = $text
try { $parsedTmp = ConvertFrom-CascadeLlmJson -Text $text; $jsonText = ($parsedTmp | ConvertTo-Json -Depth 12) } catch { if ($text -match '(?s)\{[\s\S]*\}') { $jsonText = $Matches[0] } else { throw } }
$outDir = Join-Path $RepoRoot 'output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$outPath = Join-Path $outDir 'readme_to_workitem_proposals.json'
$jsonText | Set-Content -Path $outPath -Encoding UTF8
Write-Host "Wrote $outPath" -ForegroundColor Green

try {
    $parsed = $jsonText | ConvertFrom-Json
    Write-Host "Analysis: $($parsed.analysis)"
    $i = 0
    foreach ($p in @($parsed.proposals)) {
        $i++
        Write-Host ("[{0}] {1} {2}" -f $i, $p.action, $(if ($p.title) { $p.title } elseif ($p.id) { $p.id } else { '' }))
    }
} catch {
    Write-Host "JSON parse soft-fail - inspect file manually" -ForegroundColor Yellow
}

# Also copy into ai_parent_proposals path shape for [M] if user wants
$aiPath = Join-Path $outDir 'ai_parent_proposals.json'
try {
    $parsed2 = Get-Content $outPath -Raw | ConvertFrom-Json
    # wrap if needed
    $parsed2 | ConvertTo-Json -Depth 12 | Set-Content $aiPath -Encoding UTF8
    Write-Host "Also wrote $aiPath for [M] merge/apply" -ForegroundColor DarkGray
} catch {}

if ($ApplyDup) {
    Write-Host "ApplyDup: use menu [M] -> 5 with a source iid, or create manually from proposals JSON." -ForegroundColor Yellow
}

Write-Host "Done. Review proposals; create via test project when ready." -ForegroundColor Green
