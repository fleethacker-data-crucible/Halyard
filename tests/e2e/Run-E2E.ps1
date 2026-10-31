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
param([int]$Total = 30, [int]$FakePort = 0, [int]$Port = 0)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$node = Get-Command node -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $node) { throw 'node is required for the fake GitHub server and the loop script.' }

# Ports are chosen per run unless given: several CI runs share one runner, and a fixed port made them collide (EADDRINUSE).
function Get-FreePort { $l = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0; $l.Start(); try { return ([Net.IPEndPoint]$l.LocalEndpoint).Port } finally { $l.Stop() } }
if ($FakePort -eq 0) { $FakePort = Get-FreePort }
if ($Port -eq 0) { $Port = Get-FreePort }

$work = Join-Path ([IO.Path]::GetTempPath()) ('halyard-e2e-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
Copy-Item (Join-Path $root 'config') (Join-Path $work 'config') -Recurse   # team.json, skills.json, Shellback cards
$fake = Start-Process -FilePath $node.Source -ArgumentList @(('"' + (Join-Path $root 'tests\fakes\fake-github.mjs') + '"'), $FakePort) -PassThru -NoNewWindow
$hostObj = $null
try {
    $deadline = (Get-Date).AddSeconds(15)
    $up = $false
    while (-not $up -and (Get-Date) -lt $deadline) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $FakePort); $c.Close(); $up = $true } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $up) { throw 'fake GitHub did not start' }

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
    $hostObj = [Halyard.Core.Facade]::CreateHost($settings, $null, $Port, $null)
    $info = $hostObj.Start()
    & $node.Source (Join-Path $root 'tests\e2e\sandbox-loop.mjs') ('http://127.0.0.1:' + $info.Port) $info.SessionToken $Total '--fake' ("http://127.0.0.1:$FakePort")
    if ($LASTEXITCODE -ne 0) { throw "end-to-end loop failed ($LASTEXITCODE)" }
} finally {
    if ($hostObj) { $hostObj.Dispose() }
    if ($fake -and -not $fake.HasExited) { Stop-Process -Id $fake.Id -Force }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
