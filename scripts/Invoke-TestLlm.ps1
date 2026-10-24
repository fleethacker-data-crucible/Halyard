<#
.SYNOPSIS
  Test GenAI connectivity (Grok / GenAi).
.PARAMETER Profile
  Auto | Grok | GenAi
  Grok  = Advana Databricks (needs Databricks PAT)
  GenAi = api.genai.mil (needs ARC genapi / GENAI_TOKEN)
#>
param(
    [ValidateSet('Auto', 'Grok', 'GenAi')]
    [string]$Profile = 'Auto'
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
            $k = $Matches[1].Trim()
            $v = $Matches[2].Trim().Trim('"').Trim("'")
            if ($k) { Set-Item -Path "Env:$k" -Value $v }
        }
    }
    Write-Host "Loaded config\cascade.env" -ForegroundColor DarkGray
} else {
    Write-Host "No config\cascade.env (optional). Copy from config\cascade.env.example" -ForegroundColor DarkGray
}

$hasTok = -not [string]::IsNullOrWhiteSpace($env:LLM_TOKEN) -or
          -not [string]::IsNullOrWhiteSpace($env:DATABRICKS_TOKEN) -or
          -not [string]::IsNullOrWhiteSpace($env:GENAI_TOKEN)
$arc = [bool](Get-Command Get-PmStarkApiKey -ErrorAction SilentlyContinue)
Write-Host ("Token in env: {0} | ARC helpers loaded: {1}" -f $hasTok, $arc) -ForegroundColor DarkGray
Write-Host ""
Write-Host "Profiles:" -ForegroundColor Cyan
Write-Host "  Grok  = Advana Databricks Grok  (Databricks PAT from playground)" -ForegroundColor Gray
Write-Host "  GenAi = api.genai.mil            (ARC vault item genapi)" -ForegroundColor Gray
Write-Host ("Current Profile param: {0}" -f $Profile) -ForegroundColor Gray
if ($Profile -eq 'Auto') {
    Write-Host "Auto currently prefers Grok when no LLM_URL is set." -ForegroundColor Yellow
    Write-Host "If you only have ARC genapi, run with -Profile GenAi" -ForegroundColor Yellow
}
Write-Host ""

# Isolate: clear cached GenAi token so ARC vs paste can be tested cleanly
if ($Profile -eq 'GenAi') {
    Write-Host ''
    Write-Host 'GenAi token source for this test:' -ForegroundColor Cyan
    Write-Host '  [1] ARC genapi (default)'
    Write-Host '  [2] Clear cache + PASTE key manually (isolates ARC vs key)'
    $src = Read-Host '  Choice (1/2)'
    if ($src -eq '2') {
        Remove-Item Env:GENAI_TOKEN -ErrorAction SilentlyContinue
        # Temporarily ignore remembered ARC name so we go to paste after Skip
        $script:__savedArcGenAi = $env:CASCADE_ARC_ITEM_GenAi
        Remove-Item Env:CASCADE_ARC_ITEM_GenAi -ErrorAction SilentlyContinue
        Write-Host 'Cleared GENAI_TOKEN and CASCADE_ARC_ITEM_GenAi for this process.' -ForegroundColor Yellow
        Write-Host 'At ARC list: type S (Skip), then paste the key.' -ForegroundColor Green
    }
}

Test-CascadeLlmConnection -Profile $Profile
