#requires -Version 5.1
<#
.SYNOPSIS
  Offline end-to-end check: starts the fake GitHub, starts the Cascade engine against it in this process,
  runs tests/e2e/sandbox-loop.mjs, and shuts both down. Needs node on PATH and a built core.
.EXAMPLE
  pwsh tests/e2e/Run-E2E.ps1 -Total 30
#>
[CmdletBinding()]
param([int]$Total = 30, [int]$FakePort = 8790, [int]$Port = 8799)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$node = Get-Command node -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $node) { throw 'node is required for the fake GitHub server and the loop script.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('cascade-e2e-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
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
        CASCADE_PROVIDER = 'github'; GITHUB_API_URL = "http://127.0.0.1:$FakePort"; GITHUB_TOKEN = 'fake'
        CASCADE_SANDBOX_REPO = 'sun-of-anton/cascade-sandbox'; CASCADE_SOURCE_REPO = 'sun-of-anton/cascade-sandbox'
        CASCADE_WRITE_DELAY_MS = '0'; CASCADE_WRITE_MODE = 'sandbox'
        CASCADE_LLM_PROFILE = 'openai'; LLM_URL = "http://127.0.0.1:$FakePort/v1/chat/completions"; LLM_TOKEN = 'fake'; LLM_MODEL = 'fake-model'
    }
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k], 'Process') }

    Import-Module (Join-Path $root 'module\Cascade\Cascade.psd1') -Force
    Import-CascadeCore -Root $root
    $settings = [Cascade.Core.Facade]::SettingsFromEnvironment($work)
    $hostObj = [Cascade.Core.Facade]::CreateHost($settings, $null, $Port, $null)
    $info = $hostObj.Start()
    & $node.Source (Join-Path $root 'tests\e2e\sandbox-loop.mjs') ('http://127.0.0.1:' + $info.Port) $info.SessionToken $Total
    if ($LASTEXITCODE -ne 0) { throw "end-to-end loop failed ($LASTEXITCODE)" }
} finally {
    if ($hostObj) { $hostObj.Dispose() }
    if ($fake -and -not $fake.HasExited) { Stop-Process -Id $fake.Id -Force }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
