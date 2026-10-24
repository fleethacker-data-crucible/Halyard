<#
.SYNOPSIS
  Plan CSV -> JSON package -> LLM -> parent_suggestions.json
#>
param(
    [ValidateSet('Auto', 'Grok', 'GenAi')]
    [string]$Profile = 'GenAi',
    [string]$PlanCsv
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or $line -eq '') { return }
        if ($line -match '^\s*([^=]+)=(.*)$') {
            Set-Item -Path "Env:$($Matches[1].Trim())" -Value $Matches[2].Trim().Trim('"').Trim("'")
        }
    }
    Write-Host 'Loaded config\cascade.env' -ForegroundColor DarkGray
}

if (-not $PlanCsv) { $PlanCsv = Join-Path $RepoRoot 'output\migrate_plan.csv' }
if (-not (Test-Path $PlanCsv)) {
    Write-Host 'Run migrate dry-run first (menu 2).' -ForegroundColor Yellow
    exit 1
}

$outDir = Join-Path $RepoRoot 'output'
$doc = Convert-CascadePlanCsvToJson -PlanCsv $PlanCsv -OutPath (Join-Path $outDir 'migrate_plan.json')
$pkg = New-CascadeParentPackage -PlanDoc $doc -MaxItems 8 -OutPath (Join-Path $outDir 'parent_package.json')

$n = @($pkg.issues_needing_feature_parent).Count + @($pkg.features_needing_epic_parent).Count
Write-Host ("Package: {0} items need parent" -f $n) -ForegroundColor Cyan
if ($n -eq 0) { Write-Host 'Nothing to assign.'; exit 0 }

$messages = Convert-CascadePackageToChatMessages -Package $pkg
# Reinforce JSON-only
$messages = @(
    $messages[0]
    @{
        role    = 'user'
        content = ($messages[1].content + "`n`nCRITICAL: Reply with ONLY a JSON array. No markdown, no tables, no commentary. Example: [{`"iid`":167,`"parent`":`"FEATURE-tooling`",`"note`":`"kb wiki`"}]")
    }
)

Write-Host ("Calling LLM profile={0} ..." -f $Profile) -ForegroundColor Cyan
$r = Invoke-CascadeChat -Profile $Profile -TimeoutSec 300 -MaxTokens 20000 -Messages $messages

$text = [string]$r.Content
$rawPath = Join-Path $outDir 'parent_suggestions_raw.txt'
$text | Set-Content -Path $rawPath -Encoding UTF8

if ([string]::IsNullOrWhiteSpace($text)) {
    Write-Warning "Empty model content. See $rawPath"
    exit 1
}

# Strip fences and extract first JSON array
$clean = $text -replace '^\s*```(?:json)?\s*', '' -replace '\s*```\s*$', ''
$suggestions = $null
try {
    $suggestions = $clean | ConvertFrom-Json
} catch {
    # Pull first [...] block
    $m = [regex]::Match($clean, '\[(?:[^\[\]]|\[(?:[^\[\]]|\[[^\[\]]*\])*\])*\]', 'Singleline')
    if ($m.Success) {
        try { $suggestions = $m.Value | ConvertFrom-Json } catch { }
    }
}

if ($null -eq $suggestions) {
    Write-Warning "JSON parse failed; raw saved to $rawPath"
    Write-Host ($text.Substring(0, [Math]::Min(600, $text.Length)))
    exit 1
}

$outPath = Join-Path $outDir 'parent_suggestions.json'
@($suggestions) | ConvertTo-Json -Depth 6 | Set-Content -Path $outPath -Encoding UTF8
Write-Host "Wrote $outPath" -ForegroundColor Green
Write-Host ("Suggestions: {0}" -f @($suggestions).Count)
@($suggestions) | Select-Object -First 15 | ForEach-Object {
    Write-Host ("  iid={0} -> {1}  ({2})" -f $_.iid, $_.parent, $_.note)
}
