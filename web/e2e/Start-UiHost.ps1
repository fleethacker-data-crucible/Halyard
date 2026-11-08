#requires -Version 5.1
<#
.SYNOPSIS
  The engine the Playwright suite drives (web/e2e/playwright.config.ts starts this as its webServer): the fake GitHub
  (tests/fakes/fake-github.mjs), the Halyard engine compiled in this process against it with web/dist, a seeded and
  synchronized synthetic backlog, and the session token written to -TokenFile. No token, no model key, no real
  repository, no config\halyard.env. Runs until Playwright stops it.
#>
[CmdletBinding()]
param([int]$Port = 47700, [int]$FakePort = 47701, [Parameter(Mandatory)][string]$TokenFile)
$ErrorActionPreference = 'Stop'
Remove-Item -LiteralPath $TokenFile -Force -ErrorAction SilentlyContinue   # a token from an earlier run would let tests start too soon
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$node = (Get-Command node -ErrorAction Stop).Source
$work = Join-Path ([IO.Path]::GetTempPath()) ('halyard-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
Copy-Item (Join-Path $root 'config') (Join-Path $work 'config') -Recurse
Remove-Item (Join-Path $work 'config\halyard.env') -Force -ErrorAction SilentlyContinue
$fake = Start-Process -FilePath $node -ArgumentList @(('"' + (Join-Path $root 'tests\fakes\fake-github.mjs') + '"'), $FakePort) -PassThru -WindowStyle Hidden
$h = $null
try {
    $deadline = (Get-Date).AddSeconds(15)
    $up = $false
    while (-not $up -and (Get-Date) -lt $deadline) { try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $FakePort); $c.Close(); $up = $true } catch { Start-Sleep -Milliseconds 200 } }
    if (-not $up) { throw 'fake GitHub did not start' }
    $vars = @{
        HALYARD_PROVIDER = 'github'; GITHUB_API_URL = "http://127.0.0.1:$FakePort"; GITHUB_TOKEN = 'fake'
        HALYARD_SANDBOX_REPO = 'example-org/backlog-sandbox'; HALYARD_SOURCE_REPO = ''
        HALYARD_WRITE_DELAY_MS = '0'; HALYARD_WRITE_MODE = 'sandbox'; HALYARD_AUTO_STOP = '0'
        HALYARD_FAIRLEAD_URL = "http://127.0.0.1:$FakePort/v1/chat/completions"
        HALYARD_TALKFORGE_PATH = (Join-Path $work 'no-talkforge')
    }
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k], 'Process') }
    Import-Module (Join-Path $root 'module\Halyard\Halyard.psd1') -Force
    Import-HalyardCore -Root $root
    # No Fairlead key: the crew answers from the scrum brief, so the suite never depends on a model.
    Add-Type -TypeDefinition 'public static class UiE2eKey { public static string Read(string target) { return null; } }'
    [Halyard.Core.Llm.CredentialReader]::Reader = [Delegate]::CreateDelegate([Func[string, string]], [UiE2eKey].GetMethod('Read'))
    $settings = [Halyard.Core.Facade]::SettingsFromEnvironment($work)
    $h = [Halyard.Core.Facade]::CreateHost($settings, (Join-Path $root 'web\dist'), $Port, $null, $false)
    $info = $h.Start()
    $hdr = @{ 'X-Halyard-Session' = $info.SessionToken }
    $base = 'http://127.0.0.1:' + $info.Port
    function Wait-Job($j) { for ($i = 0; $i -lt 240; $i++) { $s = Invoke-RestMethod "$base/api/jobs/$($j.id)" -Headers $hdr; if ($s.state -in 'succeeded', 'failed', 'cancelled') { return $s }; Start-Sleep -Milliseconds 250 } }
    $seed = Wait-Job (Invoke-RestMethod "$base/api/sandbox/seed" -Method Post -Headers $hdr -ContentType 'application/json' -Body '{"total":100}')
    if ($seed.state -ne 'succeeded') { throw ('seed ' + $seed.state + ': ' + $seed.error) }
    $sync = Wait-Job (Invoke-RestMethod "$base/api/sync" -Method Post -Headers $hdr -ContentType 'application/json' -Body '{"repository":"sandbox"}')
    if ($sync.state -ne 'succeeded') { throw ('sync ' + $sync.state + ': ' + $sync.error) }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TokenFile) | Out-Null
    [IO.File]::WriteAllText($TokenFile, $info.SessionToken)
    Write-Host ('UI host ready on ' + $base)
    while (-not $h.WaitForStop(500)) { }
} finally {
    if ($h) { $h.Dispose() }
    if ($fake -and -not $fake.HasExited) { Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
