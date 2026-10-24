from pathlib import Path
root=Path(__file__).resolve().parents[1]
server=(root/'scripts/Start-CascadeWeb.ps1').read_text(encoding='utf-8')
bridge=(root/'src/Cascade/ArcBridge.ps1').read_text(encoding='utf-8')
mil=(root/'src/Cascade/GitMilIntelligence.ps1').read_text(encoding='utf-8')
app=(root/'ui/app.js').read_text(encoding='utf-8')
nexus=(root/'ui/nexus.js').read_text(encoding='utf-8')
assert "'nexus.js' {'application/javascript" in server
assert "'/nexus.js'" in server
assert 'MaxProjects=20' in mil and '-MaxProjects 20' in mil
section=bridge.split('function Test-CascadeArcVaultUnlocked {',1)[1].split('function Convert-CascadeArcRows',1)[0]
assert 'Ensure-VaultUnlocked)' not in section and 'Get-LiveVault)' not in section, 'Noninteractive health check must never unlock or enumerate vault'
assert "window.CascadeNexusBridge=Object.freeze" in app
assert "window.addEventListener('cascade:plan-changed'" in nexus
assert 'bridge.getPlan()' in nexus and 'const oldRender=render' not in nexus
assert "'/api/arc/open'" in server and "'/api/provider/test'" in server
assert "'/nexus-logo.png'" in server
assert "Test-CascadeGitLabConnection -SampleIssues 0" not in server
assert "Assert-CascadeNoninteractiveService GitLab" in server
assert "Initialize-CascadeArcAtStartup" in server and "Ensure-VaultUnlocked" in server
assert "Open-CascadeModernArcBrowser" in server and "Start-PmGui" not in server.split("} elseif ($path -eq '/api/arc/open'",1)[1].split("} elseif ($path -eq '/api/provider/test'",1)[0]
assert "while (-not $script:StopRequested)" in server
assert "Start-CascadeLoopbackTransport -Port $Port" in server
assert "'/api/session/stop'" in server
print('PASS: static integration invariants (routes, 20-project ceiling, startup-only ARC unlock, nonblocking modern browser, explicit UI bridge)')