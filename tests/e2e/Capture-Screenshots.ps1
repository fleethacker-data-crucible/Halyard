#requires -Version 5.1
<#
.SYNOPSIS
  Captures the README screenshots (Overview, Night watch and Chart room) and the walkthrough images
  (docs\assets\walkthrough) with mock data only: starts the fake GitHub, starts the Halyard engine against it
  in this process with a throwaway work folder, and runs tests/e2e/screenshots.mjs, which seeds and
  synchronizes the synthetic backlog through the UI. No token, no model key, no real repository, no
  config\halyard.env. Pass '-' as -OutDir or -WalkthroughDir to skip that set.
  Needs node, a built core and web\dist, and Playwright in web (npm install --no-save playwright).
.EXAMPLE
  pwsh tests/e2e/Capture-Screenshots.ps1
#>
[CmdletBinding()]
param([int]$FakePort = 8790, [int]$ProxyPort = 8791, [int]$Port = 8773, [string]$OutDir, [string]$WalkthroughDir)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$node = Get-Command node -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $node) { throw 'node is required for the fake GitHub server and the capture script.' }
if (-not $OutDir) { $OutDir = Join-Path $root 'docs\screenshots' }
if (-not $WalkthroughDir) { $WalkthroughDir = Join-Path $root 'docs\assets\walkthrough' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('halyard-shots-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
Copy-Item (Join-Path $root 'config') (Join-Path $work 'config') -Recurse   # demo team.json, skills.json, Shellback cards
Remove-Item (Join-Path $work 'config\halyard.env') -Force -ErrorAction SilentlyContinue
$fake = Start-Process -FilePath $node.Source -ArgumentList @(('"' + (Join-Path $root 'tests\fakes\fake-github.mjs') + '"'), $FakePort) -PassThru -NoNewWindow
$hostObj = $null
try {
    $deadline = (Get-Date).AddSeconds(15)
    $up = $false
    while (-not $up -and (Get-Date) -lt $deadline) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $FakePort); $c.Close(); $up = $true } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $up) { throw 'fake GitHub did not start' }

    # Invented repository names; the engine reaches the fake through the proxy in screenshots.mjs.
    $vars = @{
        HALYARD_PROVIDER = 'github'; GITHUB_API_URL = "http://127.0.0.1:$ProxyPort"; GITHUB_TOKEN = 'fake'
        HALYARD_SANDBOX_REPO = 'example-org/backlog-sandbox'; HALYARD_SOURCE_REPO = ''
        HALYARD_WRITE_DELAY_MS = '0'; HALYARD_WRITE_MODE = 'sandbox'
        HALYARD_FAIRLEAD_URL = "http://127.0.0.1:$FakePort/v1/chat/completions"; HALYARD_FAIRLEAD_MODEL = 'demo-model'
    }
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k], 'Process') }

    Import-Module (Join-Path $root 'module\Halyard\Halyard.psd1') -Force
    Import-HalyardCore -Root $root

    # Model calls read the Fairlead key from Credential Manager. The run uses a fixed fake key (or none) through a
    # delegate on a static method, so no real credential is touched and every host thread can call it.
    Add-Type -TypeDefinition 'public static class E2eFairleadKey { public static string Key = null; public static string Read(string target) { return Key; } }'
    [Halyard.Core.Llm.CredentialReader]::Reader = [Delegate]::CreateDelegate([Func[string,string]], [E2eFairleadKey].GetMethod('Read'))
    # No key installed: walkthrough step 2 shows Settings before Fairlead has a key for Halyard.
    [E2eFairleadKey]::Key = $null
    $settings = [Halyard.Core.Facade]::SettingsFromEnvironment($work)
    $hostObj = [Halyard.Core.Facade]::CreateHost($settings, (Join-Path $root 'web\dist'), $Port, $null, $false)
    $info = $hostObj.Start()
    & $node.Source (Join-Path $root 'tests\e2e\screenshots.mjs') ('http://127.0.0.1:' + $info.Port) $info.SessionToken ("http://127.0.0.1:$FakePort") $ProxyPort $OutDir $WalkthroughDir
    if ($LASTEXITCODE -ne 0) { throw "screenshot capture failed ($LASTEXITCODE)" }
} finally {
    if ($hostObj) { $hostObj.Dispose() }
    if ($fake -and -not $fake.HasExited) { Stop-Process -Id $fake.Id -Force }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
