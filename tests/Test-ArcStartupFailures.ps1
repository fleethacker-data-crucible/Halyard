#requires -Version 5.1
# Offline Windows regression: missing ARC libraries and a cancelled CAC unlock
# must NOT accidentally authenticate, hide exceptions or block localhost APIs.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$server=Join-Path $root 'scripts/Start-CascadeWeb.ps1'
$base=Join-Path ([IO.Path]::GetTempPath()) ('CascadeArcNegative-'+[guid]::NewGuid().ToString('N'))
$oldArc=$env:ARC_PATH
[void](New-Item -ItemType Directory -Path $base -Force)
function Invoke-NegativeStartupCase {
    param([string]$Name,[string]$Contents,[string]$ExpectedCode)
    $arc=Join-Path $base $Name
    [void](New-Item -ItemType Directory -Path $arc -Force)
    [IO.File]::WriteAllText((Join-Path $arc 'pm.ps1'),$Contents)
    $env:ARC_PATH=$arc
    $port=20000+(Get-Random -Minimum 0 -Maximum 5000)
    $p=$null
    try{
        $p=Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile','-Sta','-ExecutionPolicy','Bypass','-File',('"'+$server+'"'),'-Port',$port,'-NoBrowser','-AuthenticateArc')
        $url="http://127.0.0.1:$port"
        $ready=$null
        for($i=0;$i -lt 35;$i++){
            Start-Sleep -Milliseconds 250
            try{$ready=Invoke-RestMethod -Uri "$url/api/integrations" -TimeoutSec 2;break}catch{}
            if($p.HasExited){break}
        }
        if($null -eq $ready){throw ('Server did not respond during ARC '+$Name+' failure.')}
        if($ready.arcErrorCode -ne $ExpectedCode){
            throw ('Expected '+$ExpectedCode+' for '+$Name+', received '+$ready.arcErrorCode+'.')
        }
        if($ready.vaultUnlocked){throw ('Incorrectly marked '+$Name+' vault as unlocked.')}
        if($Name -eq 'missing-lib' -and $ready.arcLoaded){throw 'Incomplete ARC accepted as loaded.'}
        $check=Invoke-RestMethod -Uri "$url/api/status" -TimeoutSec 2
        if($check.service -ne 'Cascade local'){throw 'Offline Cascade API stopped responding after ARC failure.'}
        $stop=Invoke-RestMethod -Uri "$url/api/session/stop" -Method Post -ContentType 'application/json' -Body '{}' -Headers @{'Origin'=$url;'X-Cascade-Intent'='end-session'} -TimeoutSec 3
        if(-not $stop.ok){throw 'Could not cleanly shut down offline ARC failure test.'}
        if(-not $p.WaitForExit(4500)){throw 'Local server remained active after shutdown.'}
    }finally{
        if($null -ne $p -and -not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}
    }
}
try{
    Invoke-NegativeStartupCase -Name 'missing-lib' -ExpectedCode 'ARC_MISSING_MODULES' -Contents @'
$libOrder=@('00-missing-vault.ps1')
$script:PmState=@{VaultUnlocked=$false}
function Ensure-VaultUnlocked {$script:PmState.VaultUnlocked=$true;return $true}
function Get-PmStarkApiKey {param([string]$VaultName);return 'not-an-actual-token'}
'@
    Invoke-NegativeStartupCase -Name 'cancelled-unlock' -ExpectedCode 'ARC_AUTH_CANCELLED' -Contents @'
$script:PmState=@{VaultUnlocked=$false}
function Ensure-VaultUnlocked {return $false}
function Get-PmStarkApiKey {param([string]$VaultName);throw 'Vault not unlocked'}
'@
    Write-Host 'PASS: incomplete ARC library and declined CAC both fail safely with precise codes and responsive loopback API.' -ForegroundColor Green
}finally{
    if($null -eq $oldArc){Remove-Item Env:ARC_PATH -ErrorAction SilentlyContinue}else{$env:ARC_PATH=$oldArc}
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
