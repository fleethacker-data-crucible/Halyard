#requires -Version 5.1
<#
.SYNOPSIS
  Offline end-to-end check: starts the fake GitHub, starts the Halyard engine against it in this process,
  runs tests/e2e/sandbox-loop.mjs, and shuts both down. Needs node on PATH; the engine is compiled from
  source in memory (Import-HalyardCore), exactly as the launcher does.
.EXAMPLE
  pwsh tests/e2e/Run-E2E.ps1 -Total 30
#>
[CmdletBinding()]
param([int]$Total = 30, [ValidateRange(0,65535)][int]$FakePort = 0, [ValidateRange(0,65535)][int]$Port = 0)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$node = Get-Command node -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $node) { throw 'node is required for the fake GitHub server and the loop script.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('halyard-e2e-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
Copy-Item (Join-Path $root 'config') (Join-Path $work 'config') -Recurse   # team.json, skills.json, Shellback cards
$readyFile = Join-Path $work 'fake-port.txt'
$fake = $null
$hostObj = $null
try {
    $fake = Start-Process -FilePath $node.Source -ArgumentList @(('"' + (Join-Path $root 'tests\fakes\fake-github.mjs') + '"'), $FakePort, ('"' + $readyFile + '"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'fake.log') -RedirectStandardError (Join-Path $work 'fake-error.log')
    $deadline = (Get-Date).AddSeconds(15)
    # Only this child's readiness file proves it owns the endpoint. A listening
    # fixed port could belong to another runner or a stale test process.
    while (-not (Test-Path -LiteralPath $readyFile) -and (Get-Date) -lt $deadline) {
        if ($fake.HasExited) { throw ('fake GitHub exited before readiness (exit ' + $fake.ExitCode + ')') }
        Start-Sleep -Milliseconds 200
    }
    if (-not (Test-Path -LiteralPath $readyFile)) { throw 'fake GitHub did not start' }
    $FakePort = [int](Get-Content -LiteralPath $readyFile -Raw)
    if ($FakePort -lt 1 -or $FakePort -gt 65535 -or $fake.HasExited) { throw 'fake GitHub reported invalid readiness' }

    $vars = @{
        HALYARD_PROVIDER = 'github'; GITHUB_API_URL = "http://127.0.0.1:$FakePort"; GITHUB_TOKEN = 'fake'
        HALYARD_SANDBOX_REPO = 'fleethacker-data-crucible/Halyard-Ballast'; HALYARD_SOURCE_REPO = 'sun-of-anton/raw-backlog'
        HALYARD_WRITE_DELAY_MS = '0'; HALYARD_WRITE_MODE = 'sandbox'
        HALYARD_LLM_PROFILE = 'openai'; LLM_URL = "http://127.0.0.1:$FakePort/v1/chat/completions"; LLM_TOKEN = 'fake'; LLM_MODEL = 'fake-model'
    }
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k], 'Process') }

    Import-Module (Join-Path $root 'module\Halyard\Halyard.psd1') -Force
    Import-HalyardCore -Root $root
    $settings = [Halyard.Core.Facade]::SettingsFromEnvironment($work)
    $settings.PreferredPort = $Port
    $hostObj = [Halyard.Core.Facade]::CreateHost($settings, $null, $Port, $null)
    $info = $hostObj.Start()
    & $node.Source (Join-Path $root 'tests\e2e\sandbox-loop.mjs') ('http://127.0.0.1:' + $info.Port) $info.SessionToken $Total '--fake' ("http://127.0.0.1:$FakePort")
    if ($LASTEXITCODE -ne 0) { throw "end-to-end loop failed ($LASTEXITCODE)" }
} finally {
    if ($hostObj) { $hostObj.Dispose() }
    if ($fake -and -not $fake.HasExited) { Stop-Process -Id $fake.Id -Force }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
