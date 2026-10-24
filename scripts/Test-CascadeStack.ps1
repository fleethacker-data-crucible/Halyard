#requires -Version 5.1
<#
.SYNOPSIS
  Safe, non-installing Cascade host stack compatibility audit on Windows.
.DESCRIPTION
  Reports what is present or blocked. Does not change execution policy,
  install a package, run native Rust binaries, launch additional servers,
  inspect CAC PINs, or store secrets. Run on the approved Nautilus workstation.
#>
[CmdletBinding()]
param([string]$OutputPath='')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(-not $OutputPath){
    $out=Join-Path $repo 'output/stack-check'
    [void](New-Item -ItemType Directory -Force -Path $out)
    $OutputPath=Join-Path $out ('cascade-host-stack-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
}
$rows=New-Object System.Collections.ArrayList
function Add-StackResult {
    param([string]$Component,[string]$Status,[string]$Details)
    [void]$rows.Add([pscustomobject]@{Component=$Component;Status=$Status;Details=$Details})
}
Add-StackResult 'Windows PowerShell' 'PASS' ('Version '+$PSVersionTable.PSVersion.ToString()+'; language mode '+$ExecutionContext.SessionState.LanguageMode)
Add-StackResult 'Execution policy' 'OBSERVED' ('Effective execution policy: '+(Get-ExecutionPolicy)+'. No policy changes made.')
$expected=@('ui/index.html','ui/app.js','ui/example-work-items.js','ui/example-explorer.js','ui/stack-compatibility.js','ui/stack-worker.js')
$missing=@($expected|Where-Object {-not (Test-Path -LiteralPath (Join-Path $repo $_) -PathType Leaf)})
Add-StackResult 'Prebuilt browser assets' $(if($missing.Count){'MISSING'}else{'PASS'}) $(if($missing.Count){'Missing '+($missing -join ', ')}else{'Static HTML, JavaScript and CSS are present; Node.js is not needed at runtime.'})
try{
    $probeType='Probe'+[guid]::NewGuid().ToString('N')
    $cs='namespace CascadeStackProbe { public static class '+$probeType+' { public static int Add(int a,int b) { return a+b; } } }'
    $types=Add-Type -TypeDefinition $cs -PassThru -ErrorAction Stop
    $type=@($types|Where-Object {$_.Name -eq $probeType}|Select-Object -First 1)
    if($type.Count -ne 1){throw 'No in-memory C# type returned'}
    $val=$type[0].GetMethod('Add').Invoke($null,@([int]19,[int]23))
    if([int]$val -ne 42){throw 'C# smoke calculation incorrect'}
    Add-StackResult 'C# in-memory compile' 'PASS' 'Approved PowerShell/.NET Add-Type compiler executed a minimal arithmetic class.'
}catch{
    Add-StackResult 'C# in-memory compile' 'BLOCKED' ('Cannot compile in this environment: '+$_.Exception.GetType().Name)
}
try{
    $txt='CASCADE-STACK-PROBE-ONLY'
    $plain=[Text.Encoding]::UTF8.GetBytes($txt)
    $cipher=[Security.Cryptography.ProtectedData]::Protect($plain,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $decoded=[Security.Cryptography.ProtectedData]::Unprotect($cipher,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    if([Text.Encoding]::UTF8.GetString($decoded) -ne $txt){throw 'Roundtrip did not match'}
    Add-StackResult 'Windows DPAPI' 'PASS' 'CurrentUser ephemeral text encrypted/decrypted; no keys or ciphertext persisted.'
}catch{
    Add-StackResult 'Windows DPAPI' 'BLOCKED' ('Unavailable: '+$_.Exception.GetType().Name)
}
try{
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $selectedPort=([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    Add-StackResult 'Local TCP loopback' 'PASS' ('Temporary 127.0.0.1 port '+$selectedPort+' bound and released; no firewall or port policy changes.')
}catch{
    try{if($listener){$listener.Stop()}}catch{}
    Add-StackResult 'Local TCP loopback' 'BLOCKED' ('Could not bind localhost: '+$_.Exception.GetType().Name)
}
$sqliteProviders=New-Object System.Collections.ArrayList
foreach($assembly in @('System.Data.SQLite','Microsoft.Data.Sqlite')){
    try{
        Add-Type -AssemblyName $assembly -ErrorAction Stop
        [void]$sqliteProviders.Add($assembly)
    }catch{}
}
if($sqliteProviders.Count -gt 0){
    Add-StackResult 'SQLite managed drivers' 'DETECTED' (('Present: '+($sqliteProviders -join ', '))+'. A full CRUD/locking validation is still required before selecting backend SQLite.')
}else{
    Add-StackResult 'SQLite managed drivers' 'NOT AVAILABLE' 'No tested SQLite managed assembly found; maintain JSON snapshots until a provider is approved. Do not install one on Nautilus.'
}
$dotnet=Get-Command dotnet.exe -ErrorAction SilentlyContinue
if($null -ne $dotnet){
    try{
        $runtimeList=@(& $dotnet.Source --list-runtimes 2>$null)
        if($LASTEXITCODE -ne 0){throw 'dotnet reported nonzero exit'}
        Add-StackResult '.NET runtime launcher' 'DETECTED' ('dotnet executable ran; installed runtimes: '+$(if($runtimeList.Count){($runtimeList -join ' | ')}else{'none reported'})+'. Native hosting still requires authorization.')
    }catch{
        Add-StackResult '.NET runtime launcher' 'BLOCKED' ('Installed executable could not run: '+$_.Exception.GetType().Name)
    }
}else{
    Add-StackResult '.NET runtime launcher' 'NOT AVAILABLE' 'dotnet.exe not found on PATH; do not depend on it for the Cascade Nautilus host.'
}
$rust=Get-Command rustc.exe -ErrorAction SilentlyContinue
Add-StackResult 'Rust native toolchain' $(if($rust){'DETECTED ONLY'}else{'NOT AVAILABLE'}) $(if($rust){'rustc.exe is discoverable, but was deliberately not executed; native Rust execution requires separate IT authorization.'}else{'rustc.exe not present on PATH. Compile Rust/WASM in approved CI and ship reviewed WASM static assets.'})
Add-StackResult 'WebAssembly browser execution' 'BROWSER TEST' 'Open Stack compatibility in Cascade and run its bundled WASM module to verify browser execution separately.'
Add-StackResult 'Svelte/TypeScript frontend' 'NOT DEPLOYED' 'Target frontend is not yet migrated. Bundle static outputs in CI and test on this workstation before replacing legacy app.js.'
$report=[pscustomobject]@{
 schema='cascade.stack.host.v1'
 observedUtc=[datetime]::UtcNow.ToString('o')
 mode='SAFE_CAPABILITY_PROBE'
 entries=@($rows.ToArray())
 note='Do not use these observations to bypass Nautilus execution/security policy. No CAC PINs, tokens or repository issue bodies are collected.'
}
$dir=Split-Path -Parent $OutputPath
if($dir){[void](New-Item -ItemType Directory -Force -Path $dir)}
$report|ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$report.entries|Format-Table Component,Status,Details -AutoSize -Wrap
Write-Host ('Host stack test report: '+$OutputPath) -ForegroundColor Cyan
