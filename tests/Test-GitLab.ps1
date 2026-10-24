<#
.SYNOPSIS
  App test: GitLab API connection (web.git.mil).
#>
$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $RepoRoot 'src\Cascade\Cascade.psd1') -Force -WarningAction SilentlyContinue
Set-Location $RepoRoot

$envFile = Join-Path $RepoRoot 'config\cascade.env'
if (Test-Path -LiteralPath $envFile) {
    Get-Content -LiteralPath $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or $line -eq '') { return }
        if ($line -match '^\s*([^=]+)=(.*)$') {
            Set-Item -Path ("Env:{0}" -f $Matches[1].Trim()) -Value $Matches[2].Trim().Trim('"').Trim("'")
        }
    }
}

# Prefer ARC already unlocked in parent host; publish helpers to global
if (Get-Command Import-CascadeArcSession -ErrorAction SilentlyContinue) {
    $null = Import-CascadeArcSession
}
if (Get-Command Publish-CascadeArcFunctionsToGlobal -ErrorAction SilentlyContinue) {
    $null = Publish-CascadeArcFunctionsToGlobal
}

Write-Host ''
Write-Host 'TEST: GitLab API connection' -ForegroundColor Cyan
Write-Host '----------------------------------------'
if (-not (Get-Command Test-CascadeGitLabConnection -ErrorAction SilentlyContinue)) {
    Write-Host 'RESULT: FAIL (module not loaded)' -ForegroundColor Red
    exit 1
}
$r = Test-CascadeGitLabConnection -SampleIssues 5
if ($r.Ok) {
    Write-Host 'RESULT: PASS' -ForegroundColor Green
    exit 0
}
Write-Host 'RESULT: FAIL' -ForegroundColor Red
exit 1
