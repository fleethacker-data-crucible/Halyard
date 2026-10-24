# Cascade offline PR verification. Windows PowerShell 5.1, no dependency installation.
# Keep aligned with .github/workflows/cascade-checks.yml.
[CmdletBinding()]
param(
    [switch]$CoreOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$results = New-Object System.Collections.ArrayList
$overallFailed = $false
$incomplete = $false

function Invoke-CascadeCheck {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Write-Host ('[RUN] ' + $Name) -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $global:LASTEXITCODE = 0
        & $Action
        if ($global:LASTEXITCODE -ne 0) {
            throw ('Command exited with status ' + $global:LASTEXITCODE)
        }
        $watch.Stop()
        $null = $results.Add([pscustomobject]@{ Check=$Name; Result='PASS'; Seconds=[math]::Round($watch.Elapsed.TotalSeconds,1); Detail='' })
        Write-Host ('[PASS] ' + $Name) -ForegroundColor Green
    }
    catch {
        $watch.Stop()
        $script:overallFailed = $true
        $note = $_.Exception.Message
        $null = $results.Add([pscustomobject]@{ Check=$Name; Result='FAIL'; Seconds=[math]::Round($watch.Elapsed.TotalSeconds,1); Detail=$note })
        Write-Host ('[FAIL] ' + $Name + ': ' + $note) -ForegroundColor Red
    }
}

function Assert-CascadeExecutable {
    param([Parameter(Mandatory)][string]$Name)
    $executable = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $executable) { throw ('Required tool not found: ' + $Name + '. No tool installation was attempted.') }
    return $executable.Source
}

function Invoke-CascadeExternal {
    param(
        [Parameter(Mandatory)][string]$CommandName,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $executable = Assert-CascadeExecutable -Name $CommandName
    & $executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw ("$CommandName returned exit code $LASTEXITCODE") }
}

Write-Host 'Cascade local PR checks - zero GitHub-hosted runner minutes' -ForegroundColor Yellow
Write-Host ('Repository: ' + $repoRoot)
Write-Host 'No Git.mil API access, LLM request, token read, or GitLab write is performed.'

Push-Location $repoRoot
try {
    Invoke-CascadeCheck -Name 'Parse all PowerShell source' -Action {
        $files = @(Get-ChildItem -Path $repoRoot -Recurse -File -ErrorAction Stop |
            Where-Object { $_.Extension -in @('.ps1','.psm1','.psd1') -and $_.FullName -notmatch '[\\/]\.git[\\/]' })
        $parseErrors = New-Object System.Collections.ArrayList
        foreach ($file in $files) {
            $tokens = $null
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
            foreach ($err in @($errors)) {
                if ($err) { $null = $parseErrors.Add(($file.FullName + ': ' + $err.Message)) }
            }
        }
        if ($parseErrors.Count -gt 0) { throw ('PowerShell parsing failed: ' + ($parseErrors -join '; ')) }
        Write-Host ('Parsed ' + $files.Count + ' PowerShell source files.')
    }

    Invoke-CascadeCheck -Name 'Offline Windows PowerShell 5.1 tests' -Action {
        $exe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (-not (Test-Path -LiteralPath $exe)) { throw 'Windows PowerShell 5.1 is required for parity with CI.' }
        & $exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tests\Invoke-CascadeTests.ps1') -Suite Offline
        if ($LASTEXITCODE -ne 0) { throw ("Offline PowerShell tests failed with exit code $LASTEXITCODE") }
    }

    if ($CoreOnly) {
        $incomplete = $true
        Write-Warning 'CoreOnly requested: browser and Python verification were not run. This is a PARTIAL verification, not a green PR check.'
    }
    else {
        $nodeChecks = @(
            @{ Name='Nexus browser smoke'; File='tests\test-browser-smoke.js' },
            @{ Name='Complete example hierarchy'; File='tests\test-example-work-items.js' },
            @{ Name='Browser WASM and capability probe'; File='tests\test-stack-compatibility.js' },
            @{ Name='Intelligence review dialog and parent approvals'; File='tests\test-review-dialog.js' },
            @{ Name='Real API wiring'; File='tests\test-api-wiring.js' }
        )
        foreach ($test in $nodeChecks) {
            $testName = [string]$test.Name
            $filePath = Join-Path $repoRoot ([string]$test.File)
            Invoke-CascadeCheck -Name $testName -Action {
                Invoke-CascadeExternal -CommandName 'node' -Arguments @($filePath)
            }
        }
        Invoke-CascadeCheck -Name 'Nexus static integration' -Action {
            Invoke-CascadeExternal -CommandName 'python' -Arguments @((Join-Path $repoRoot 'tests\test-nexus-integration.py'))
        }
    }

    Invoke-CascadeCheck -Name 'Safe host stack probe (read-only)' -Action {
        $exe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (-not (Test-Path -LiteralPath $exe)) { throw 'Windows PowerShell was not found.' }
        & $exe -NoProfile -File (Join-Path $repoRoot 'scripts\Test-CascadeStack.ps1')
        if ($LASTEXITCODE -ne 0) { throw ("Stack probe returned exit code $LASTEXITCODE") }
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host '=== Cascade PR Verification Results ===' -ForegroundColor Yellow
$results | Format-Table Check,Result,Seconds,Detail -AutoSize | Out-Host
if ($overallFailed) {
    Write-Host 'FAIL: one or more PR checks failed. Do not treat this branch as verified.' -ForegroundColor Red
    exit 1
}
if ($incomplete) {
    Write-Host 'PARTIAL: core-only checks passed, but this is not full CI verification.' -ForegroundColor Yellow
    exit 2
}
Write-Host 'PASS: all configured local PR checks completed successfully.' -ForegroundColor Green
exit 0
