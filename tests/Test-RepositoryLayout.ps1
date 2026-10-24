#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$required = @(
    'Cascade-GUI.bat', 'Cascade-Terminal.bat', 'scripts/Start-CascadeWeb.ps1',
    'ui/index.html', 'ui/app.js', 'ui/nexus.js',
    'src/Cascade/WritePolicy.ps1', 'src/Cascade/GitMilIntelligence.ps1', 'src/Cascade/WorkspaceScope.ps1', 'src/Cascade/TopologyAudit.ps1', 'src/Cascade/IntelligenceReview.ps1', 'src/Cascade/RelationshipReview.ps1',
    'src/Cascade/AgentRuntime.ps1', 'tests/Test-ArcBridgeIntegration.ps1', 'tests/Test-ArcStartupFailures.ps1', 'tests/Test-WorkspaceScope.ps1', 'tests/Test-GitMilLabelSearch.ps1', 'tests/Test-TopologyAudit.ps1', 'tests/Test-IntelligenceReview.ps1', 'tests/Test-RelationshipReview.ps1', 'tests/Test-Discovery.ps1',
    'tests/Test-Autonomy.ps1', 'docs/CLEANUP_PHASE1_3.md'
)
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $item))) {
        throw "Required Cascade source file missing: $item"
    }
}
$disallowed = @(
    'docs/ROADMAP.md',
    'scripts/Import-CascadeModule.ps1',
    'scripts/Invoke-TestGitLab.ps1',
    'scripts/Invoke-IntegrityAudit.ps1',
    'scripts/Invoke-SandboxRebuildFromCbr.ps1',
    'ui/nexus-standalone.html'
)
foreach ($item in $disallowed) {
    if (Test-Path -LiteralPath (Join-Path $root $item)) {
        throw "Retired or duplicate file still present: $item"
    }
}
$archives = @(Get-ChildItem -LiteralPath $root -Filter '*.zip' -File -ErrorAction SilentlyContinue)
if ($archives.Count -gt 0) { throw 'Release ZIP archives should not be committed into the source root.' }
$sessions = @(Get-ChildItem -LiteralPath $root -Filter 'cascade-session-*.json' -File -ErrorAction SilentlyContinue)
if ($sessions.Count -gt 0) { throw 'Local session recordings do not belong in tracked source.' }
$launchers = @(Get-ChildItem -LiteralPath $root -Filter '*.bat' -File)
$expectedLaunchers=@('Cascade-GUI.bat','Cascade-Terminal.bat','Launch-LocalDev.bat')
if($launchers.Count -ne $expectedLaunchers.Count -or @( $launchers.Name | Where-Object {$_ -notin $expectedLaunchers} ).Count -gt 0){throw "Unexpected launcher inventory: $($launchers.Name -join ', ')"}
$localDevBat=Get-Content -LiteralPath (Join-Path $root 'Launch-LocalDev.bat') -Raw
if($localDevBat -match '-AuthenticateArc' -or $localDevBat -notmatch 'Start-CascadeWeb.ps1'){throw 'Local development launcher must never automatically authenticate ARC.'}
foreach($name in @('Cascade-GUI.bat','Cascade-Terminal.bat')){
    if(-not ($launchers.Name -contains $name)){throw "Missing launcher $name"}
    $bat=Get-Content -LiteralPath (Join-Path $root $name) -Raw
    if($bat -notmatch '-STA' -or $bat -notmatch '-ExecutionPolicy Bypass' -or $bat -notmatch '-File'){
        throw "Launcher $name must match the known-working ARC PowerShell 5.1/STA startup."
    }
    if($bat -match 'Set-ExecutionPolicy'){throw "Launcher $name must never modify machine/user policy."}
}
if(-not (Test-Path -LiteralPath (Join-Path $root 'scripts/LoopbackTransport.ps1'))){
    throw 'Loopback transport is required for non-admin HTTP startup.'
}
$web=Get-Content -LiteralPath (Join-Path $root 'scripts/Start-CascadeWeb.ps1') -Raw
foreach($endpoint in @('/api/arc/open','/api/gitmil/status','/api/gitmil/projects','/api/gitmil/sync','/api/provider/test')){
    if(-not $web.Contains($endpoint)){throw "Missing real API endpoint: $endpoint"}
}
if($web -match 'New-Object System.Net.HttpListener' -or $web -notmatch 'Start-CascadeLoopbackTransport'){
    throw 'HttpListener depends on HTTP.sys URL reservations; use loopback transport.'
}
if($web -match 'Test-CascadeGitLabConnection -SampleIssues 0') {throw 'Never launch interactive connection wizard from browser request'}
$index=Get-Content -LiteralPath (Join-Path $root 'ui/index.html') -Raw
if(-not $index.Contains('nexus-logo.png')){throw 'Selected Nexus logo missing from GUI'}
$module = Get-Content -LiteralPath (Join-Path $root 'src/Cascade/Cascade.psm1') -Raw
foreach ($component in @('WritePolicy.ps1','GitMilIntelligence.ps1','Discovery.ps1','Autonomy.ps1')) {
    if ($module -notmatch [regex]::Escape($component)) { throw "Module import missing: $component" }
}
Write-Host 'PASS: repository layout, no legacy archives, two explicit launchers, guarded modules.' -ForegroundColor Green