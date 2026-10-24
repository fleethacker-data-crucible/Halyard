[CmdletBinding()]
param([int]$Port=8773,[switch]$NoBrowser,[switch]$AuthenticateArc,[switch]$HideConsole)
$ErrorActionPreference='Stop'
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port must be between 1024 and 65535.' }
$repo=Split-Path $PSScriptRoot -Parent
$ui=Join-Path $repo 'ui'
$workspace=Join-Path $repo 'output\autonomous'
$logDir=Join-Path $repo 'logs'
[void](New-Item -ItemType Directory -Path $logDir -Force)
$logFile=Join-Path $logDir ('cascade-'+(Get-Date -Format 'yyyyMMdd')+'.jsonl')
function Write-CascadeDiagnostic {
 param([string]$Level,[string]$Event,[string]$Route='', [string]$Message='', [string]$RequestId='')
 # Never log request bodies, tokens, authorization headers, or repository data.
 $safe=([string]$Message -replace '(?i)(private-token|authorization|bearer|token|password|secret|api[_-]?key)\s*[:=]\s*[^\s,;]+','$1=[REDACTED]')
 $safe=($safe -replace 'https?://[^\s]+','[URL REDACTED]')
 if($safe.Length -gt 350){$safe=$safe.Substring(0,350)}
 $line=([pscustomobject]@{time=[datetime]::UtcNow.ToString('o');level=$Level;event=$Event;route=$Route;requestId=$RequestId;message=$safe}|ConvertTo-Json -Compress)
 Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
}
Import-Module (Join-Path $repo 'src\Cascade\Cascade.psd1') -Force
# ARC stays in this PowerShell process so its in-memory CAC/vault session survives.
# No login or network calls occur automatically on application startup.
# CAC/TOTP authentication is an explicit, STARTUP-ONLY operation. Never prompt
# from an HTTP request: this server handles requests serially.
. (Join-Path $PSScriptRoot 'LoopbackTransport.ps1')
$script:StopRequested=$false
$script:CascadeArcAuthenticated=$false
$script:CascadeArcAuthStage='not-started'
$script:CascadeArcAuthError=''
function Send-Json($response,$value,[int]$status=200) {
    $response.StatusCode=$status
    $response.ContentType='application/json; charset=utf-8'
    $json=ConvertTo-Json -InputObject $value -Depth 30 -Compress
    $bytes=[Text.Encoding]::UTF8.GetBytes($json)
    $response.ContentLength64=$bytes.Length
    $response.OutputStream.Write($bytes,0,$bytes.Length)
}
# Browser-managed nonsecret configuration. Tokens stay in ARC; never write credentials to disk.
$configDir=Join-Path $repo 'config'
$configPath=Join-Path $configDir 'browser-settings.json'
function Read-CascadeBrowserSettings {
    $defaults=[pscustomobject]@{gitLabUrl='https://web.git.mil';arcPath='';gitLabArcItem='gitmil';genaiUrl='https://api.genai.mil/v1/chat/completions';genaiModel='gemini-3.8-flash';advanaUrl='https://advana-jupiter.cloud.databricks.mil/serving-endpoints/databricks-grok-4-6/invocations';advanaModel='system.ai.grok-4-6';geminiArcItem='genapi';grokArcItem='advana-api';sinceDays=30;issueLabel='';localRoots=@();selectedProjectIds=@()}
    if(-not (Test-Path -LiteralPath $configPath)){return $defaults}
    $saved=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($name in @('gitLabUrl','arcPath','genaiUrl','genaiModel','advanaUrl','advanaModel','gitLabArcItem','geminiArcItem','grokArcItem','sinceDays','issueLabel','localRoots','selectedProjectIds')) {
        if($null -ne $saved.PSObject.Properties[$name]) { $defaults.$name=$saved.$name }
    }
    $defaults.issueLabel=Assert-CascadeIssueLabelFilter -Label ([string]$defaults.issueLabel)
    return $defaults
}
function Set-CascadeBrowserSettings($inputSettings) {
    $settings=Read-CascadeBrowserSettings
    $oldGitItem=[string]$settings.gitLabArcItem
    $oldGenAiItem=[string]$settings.geminiArcItem
    $oldAdvanaItem=[string]$settings.grokArcItem
    $url=[string]$inputSettings.gitLabUrl
    if($url -ne 'https://web.git.mil'){throw 'This build only permits https://web.git.mil for GitLab.'}
    $arcPath=[string]$inputSettings.arcPath
    if($arcPath.Length -gt 512){throw 'ARC path is too long'}
    if(-not [string]::IsNullOrWhiteSpace($arcPath)){
        $arcPath=$arcPath.Trim().Trim('"')
        if(-not [IO.Path]::IsPathRooted($arcPath)){throw 'ARC installation path must be absolute'}
        $arcPath=[IO.Path]::GetFullPath($arcPath)
        if(-not (Test-Path -LiteralPath (Join-Path $arcPath 'pm.ps1') -PathType Leaf)){throw 'The ARC folder must contain pm.ps1'}
        $settings.arcPath=$arcPath
    }else{$settings.arcPath=''}
    foreach($field in @('genaiModel','advanaModel')){
        $model=[string]$inputSettings.$field
        if($model -notmatch '^[a-zA-Z0-9._:/-]{1,128}$'){throw ('Invalid model name for '+$field)}
        $settings.$field=$model
    }
    foreach($field in @('genaiUrl','advanaUrl')){
        $value=[string]$inputSettings.$field
        if($value.Length -gt 380){throw ('Endpoint too long: '+$field)}
        $endpoint=$null
        if(-not [Uri]::TryCreate($value,[UriKind]::Absolute,[ref]$endpoint) -or $endpoint.Scheme -ne 'https' -or
            -not [string]::IsNullOrEmpty($endpoint.UserInfo) -or -not [string]::IsNullOrEmpty($endpoint.Query) -or
            -not [string]::IsNullOrEmpty($endpoint.Fragment)) {throw ('Invalid HTTPS endpoint for '+$field)}
        if($field -eq 'genaiUrl' -and $endpoint.Host -ne 'api.genai.mil'){throw 'GenAI endpoint must be api.genai.mil'}
        if($field -eq 'advanaUrl' -and $endpoint.Host -notmatch '(^|\.)databricks\.mil$'){throw 'Advana endpoint must be on an approved databricks.mil host'}
        $settings.$field=$endpoint.AbsoluteUri.TrimEnd('/')
    }
    foreach($field in @('gitLabArcItem','geminiArcItem','grokArcItem')) {
        $value=[string]$inputSettings.$field
        if($value -notmatch '^[a-zA-Z0-9_.-]{1,100}$'){throw "Invalid ARC item name: $field"}
        $settings.$field=$value
    }
    $days=[int]$inputSettings.sinceDays
    if($days -lt 1 -or $days -gt 365){throw 'Lookback days must be 1..365'}
    $settings.sinceDays=$days
    if($null -ne $inputSettings.PSObject.Properties['issueLabel']){
        $settings.issueLabel=Assert-CascadeIssueLabelFilter -Label ([string]$inputSettings.issueLabel)
    }
    $roots=@($inputSettings.localRoots | Where-Object {$_ -is [string] -and $_.Length -lt 512})
    if($roots.Count -gt 15){throw 'Maximum 15 local roots'}
    $settings.localRoots=$roots
    $ids=@($inputSettings.selectedProjectIds | ForEach-Object {[int]$_} | Where-Object {$_ -gt 0} | Select-Object -Unique)
    if($ids.Count -gt 20){throw 'Maximum 20 projects'}
    $settings.selectedProjectIds=$ids
    # ARC mapping changes invalidate the corresponding process-cached token, so
    # the next explicit test retrieves the NEW service entry from the vault.
    if($oldGitItem -cne [string]$settings.gitLabArcItem){Remove-Item Env:GITLAB_TOKEN -ErrorAction SilentlyContinue}
    if($oldGenAiItem -cne [string]$settings.geminiArcItem){Remove-Item Env:GENAI_TOKEN -ErrorAction SilentlyContinue}
    if($oldAdvanaItem -cne [string]$settings.grokArcItem){Remove-Item Env:DATABRICKS_TOKEN -ErrorAction SilentlyContinue}
    [void](New-Item -ItemType Directory -Path $configDir -Force)
    $tmp=$configPath+'.tmp'
    $settings|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $configPath -Force
    return $settings
}
function Set-CascadeBrowserIssueLabel([string]$Label) {
    $value=Assert-CascadeIssueLabelFilter -Label $Label
    $settings=Read-CascadeBrowserSettings
    $settings.issueLabel=$value
    [void](New-Item -ItemType Directory -Path $configDir -Force)
    $temporary=$configPath+'.tmp'
    $settings|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $configPath -Force
    return $settings
}
function Apply-CascadeBrowserSettings {
    $cfg=Read-CascadeBrowserSettings
    $env:GITLAB_URL=$cfg.gitLabUrl
    if(-not [string]::IsNullOrWhiteSpace([string]$cfg.arcPath)){$env:ARC_PATH=[string]$cfg.arcPath}
    $env:GENAI_URL=$cfg.genaiUrl
    $env:GENAI_MODEL=$cfg.genaiModel
    $env:DATABRICKS_URL=$cfg.advanaUrl
    $env:DATABRICKS_MODEL=$cfg.advanaModel
    $env:CASCADE_ARC_ITEM_GitLab=$cfg.gitLabArcItem
    $env:CASCADE_ARC_ITEM_GenAi=$cfg.geminiArcItem
    $env:CASCADE_ARC_ITEM_Grok=$cfg.grokArcItem
    return $cfg
}
function Read-CascadeRequestJson($request) {
    if($request.ContentLength64 -gt 16384 -or $request.ContentLength64 -lt 0){throw 'Invalid request size'}
    $reader=New-Object IO.StreamReader($request.InputStream,[Text.Encoding]::UTF8)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
}
function Assert-CascadeSameOrigin($request) {
    $origin=$request.Headers['Origin']
    if($origin -and $origin -notin @("http://localhost:$Port","http://127.0.0.1:$Port")){throw 'Origin not allowed'}
}
function Get-CascadeConnectionFault($Failure,[string]$Service) {
    # Stable, nonsecret error codes for support bundles and user troubleshooting.
    # Never return token values, full URLs, request payloads or exception bodies.
    $msg=[string]$Failure.Exception.Message
    $status=0
    try{$status=[int]$Failure.Exception.Response.StatusCode}catch{}
    if(-not $status){
        if($msg -match '(?i)\b401\b|unauthorized'){$status=401}
        elseif($msg -match '(?i)\b403\b|forbidden'){$status=403}
        elseif($msg -match '(?i)\b404\b|not found'){$status=404}
    }
    $code='REQUEST_FAILED'
    $hint='The request failed. Review the local connection diagnostics and approved network configuration.'
    if($msg -match '(?i)ARC.*locked|vault.*locked|open ARC|unlock ARC') {
        $code='ARC_LOCKED';$hint='ARC is locked. Use Open ARC, finish CAC/2FA authentication, and retry.'
    } elseif($msg -match '(?i)credential unavailable|no credential|token unavailable|token empty') {
        $code='CREDENTIAL_MISSING';$hint='Credential not available. Verify this service mapping in ARC and authenticate.'
    } elseif($status -eq 401) {
        $code='AUTH_REJECTED';$hint='The provider rejected authentication (401). Check that the correct ARC item contains a current token.'
    } elseif($status -eq 403) {
        $code='ACCESS_DENIED';$hint='The provider denied access (403). Check account membership, token scopes, and model permissions.'
    } elseif($status -eq 404) {
        $code='ENDPOINT_OR_MODEL_NOT_FOUND';$hint='The endpoint or model was not found (404). Check the approved endpoint and model configuration.'
    } elseif($msg -match '(?i)timed?\s*out|resolve|name or service|SSL|TLS|certificate|proxy|network') {
        $code='NETWORK_OR_TLS';$hint='Network, proxy, certificate, or TLS connection failed. Verify the approved host and workstation network.'
    } elseif($msg -match '(?i)endpoint|model|configuration|unknown profile') {
        $code='CONFIGURATION_ERROR';$hint='Provider configuration rejected. Check the endpoint and model in Data & settings.'
    }
    return [pscustomobject]@{service=$Service;code=$code;error=$hint}
}
function Get-CascadeArcReadiness {
    $null=Apply-CascadeBrowserSettings
    $root=Find-CascadeArcRoot
    $loaded=[bool](Test-CascadeArcHelpersAvailable)
    $unlocked=if($loaded){[bool](Test-CascadeArcVaultUnlocked)}else{$false}
    $code=[string]$script:CascadeArcAuthError
    if(-not $code -and -not $loaded){$code=[string](Get-CascadeArcLoadDiagnostics).ErrorCode}
    $phase=[string]$script:CascadeArcAuthStage
    return [pscustomobject]@{
        installed=[bool]$root
        loaded=$loaded
        unlocked=$unlocked
        authStage=$phase
        errorCode=$code
        pathHint=if($root){'ARC found'}else{'Set ARC_PATH or place arc beside cascade'}
    }
}
function Assert-CascadeNoninteractiveService([string]$Profile) {
    # Never inspect a locked vault, because ARC secret getters can launch CAC/TOTP UI.
    $arcReady=Test-CascadeArcVaultUnlocked
    if ($Profile -eq 'GitLab') {
        if ([string]::IsNullOrWhiteSpace($env:GITLAB_TOKEN) -and -not $arcReady) {
            throw 'ARC is locked. Restart Cascade-GUI.bat to authenticate at startup before connecting to Git.mil.'
        }
        $token=Get-CascadeGitLabToken
    } else {
        $envName=if($Profile -eq 'GenAi'){'GENAI_TOKEN'}else{'DATABRICKS_TOKEN'}
        if (-not [Environment]::GetEnvironmentVariable($envName) -and -not $arcReady) {
            throw ('ARC is locked. Restart Cascade-GUI.bat to authenticate at startup before testing '+$Profile+'.')
        }
        $token=Get-CascadeLlmToken -ForProfile $Profile
    }
    if ([string]::IsNullOrWhiteSpace($token)){throw ('No credential available for '+$Profile+'. Check its ARC service mapping.')}
    # Never log/return this token; the provider function resolves its own token.
    $token=$null
}
function Get-CascadeProviderProbe([string]$Profile) {
    if($Profile -notin @('GenAi','Grok')){throw 'Unrecognized AI provider'}
    $null=Apply-CascadeBrowserSettings
    Assert-CascadeNoninteractiveService $Profile
    $cfg=Get-CascadeLlmConfig -Profile $Profile
    # Never send an ARC credential to an unrelated endpoint.
    $hostName=([Uri]$cfg.Url).Host
    if($Profile -eq 'GenAi' -and $hostName -ne 'api.genai.mil'){throw 'GenAI requires api.genai.mil. Check GENAI_URL.'}
    if($Profile -eq 'Grok' -and $hostName -notmatch '(^|\.)databricks\.mil$'){throw 'Advana requires an approved databricks.mil host.'}
    # A real, explicitly requested request: no mock result and no console prompt.
    $answer=Invoke-CascadeChat -Profile $Profile -Model $cfg.Model -MaxTokens 32 -TimeoutSec 18 -Quiet -NonInteractive `
      -Messages @(@{role='user';content='Reply with OK.'})
    return [pscustomobject]@{connected=($null -ne $answer.Raw);profile=$Profile;model=$cfg.Model;responseReceived=([bool]$answer.Content)}
}
function Initialize-CascadeArcAtStartup {
    if(-not $AuthenticateArc){return}
    $script:CascadeArcAuthStage='discover'
    $script:CascadeArcAuthError=''
    $null=Apply-CascadeBrowserSettings
    $root=Find-CascadeArcRoot
    if(-not $root){
        $script:CascadeArcAuthError='ARC_NOT_FOUND'
        Write-Warning 'ARC not found. Set ARC_PATH to the directory containing pm.ps1 or update Data & settings, then restart Cascade.'
        Write-CascadeDiagnostic -Level WARN -Event ARC_STARTUP_SKIPPED -Message $script:CascadeArcAuthError
        return
    }
    # Like Keystone, load once and authenticate BEFORE opening an HTTP port.
    # Never present a CAC prompt from a synchronous browser API handler.
    $script:CascadeArcAuthStage='import'
    if(-not (Import-CascadeArcSession -ArcRoot $root -Quiet)){
        $arcFailure=Get-CascadeArcLoadDiagnostics
        $code=[string]$arcFailure.ErrorCode
        if([string]::IsNullOrWhiteSpace($code)){$code='ARC_IMPORT_FAILED'}
        $script:CascadeArcAuthError=$code
        Write-Warning ('Cascade could not initialize ARC ('+$code+'). Use ARC directly and inspect its local logs; no vault contents were changed.')
        Write-CascadeDiagnostic -Level ERROR -Event ARC_STARTUP_FAILED -Message ('stage=import code='+$code)
        return
    }
    $script:CascadeArcAuthStage='unlock'
    try{
        if(-not (Test-CascadeArcVaultUnlocked)){
            Write-Host ''
            Write-Host 'Cascade: complete ARC CAC + TOTP authentication in this PowerShell window.' -ForegroundColor Cyan
            $result=[bool](Ensure-VaultUnlocked)
            if(-not $result){
                $script:CascadeArcAuthError='ARC_AUTH_CANCELLED'
                Write-Warning 'ARC authentication was cancelled or declined. The GUI will open with no ARC vault credentials.'
                Write-CascadeDiagnostic -Level WARN -Event ARC_STARTUP_LOCKED -Message $script:CascadeArcAuthError
                return
            }
        }
        $script:CascadeArcAuthenticated=[bool](Test-CascadeArcVaultUnlocked)
        if(-not $script:CascadeArcAuthenticated){
            $script:CascadeArcAuthError='ARC_STATE_NOT_UNLOCKED'
            Write-Warning 'ARC completed authentication but did not report an unlocked vault. Check ARC local diagnostics.'
            Write-CascadeDiagnostic -Level ERROR -Event ARC_STARTUP_FAILED -Message ('stage=state code='+$script:CascadeArcAuthError)
            return
        }
    }catch{
        $script:CascadeArcAuthError='ARC_AUTH_EXCEPTION'
        Write-Warning ('ARC unlock failed ('+$_.Exception.GetType().Name+'). Open ARC directly to review its logs.')
        Write-CascadeDiagnostic -Level ERROR -Event ARC_STARTUP_FAILED -Message ('stage=unlock code='+$script:CascadeArcAuthError+' exception='+$_.Exception.GetType().Name)
        return
    }
    $script:CascadeArcAuthStage='credentials'
    $script:CascadeArcAuthError=''
    Write-Host '[+] ARC vault unlocked in Cascade host process.' -ForegroundColor Green
    Write-CascadeDiagnostic -Level INFO -Event ARC_STARTUP_UNLOCKED

    # Credential cache is *optional* after a successful vault unlock.
    # Missing/malformed browser-settings must not undo a valid CAC session.
    try{
        $settings=Read-CascadeBrowserSettings
        foreach($entry in @(
            @{EnvName='GITLAB_TOKEN';VaultName=[string]$settings.gitLabArcItem;Role='GitMil'},
            @{EnvName='GENAI_TOKEN';VaultName=[string]$settings.geminiArcItem;Role='GenAi'},
            @{EnvName='DATABRICKS_TOKEN';VaultName=[string]$settings.grokArcItem;Role='Advana'}
        )){
            if([string]::IsNullOrWhiteSpace($entry.VaultName)){continue}
            if(-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($entry.EnvName,'Process'))){
                Write-CascadeDiagnostic -Level INFO -Event ARC_CREDENTIAL_READY -Message ($entry.Role+' (existing approved session)')
                continue
            }
            $value=$null
            try{$value=Get-CascadeArcSecretByName -ServiceName $entry.VaultName}catch{}
            if(-not [string]::IsNullOrWhiteSpace([string]$value)){
                [Environment]::SetEnvironmentVariable($entry.EnvName,[string]$value,'Process')
                Write-CascadeDiagnostic -Level INFO -Event ARC_CREDENTIAL_READY -Message $entry.Role
            }else{
                Write-CascadeDiagnostic -Level WARN -Event ARC_CREDENTIAL_MISSING -Message $entry.Role
            }
            $value=$null
        }
    }catch{
        # Fail only credential prefetch; don't reset a successfully unlocked ARC.
        Write-CascadeDiagnostic -Level WARN -Event ARC_PREFETCH_FAILED -Message $_.Exception.GetType().Name
    }
    $script:CascadeArcAuthStage='ready'
}
function Open-CascadeModernArcBrowser {
    $root=Find-CascadeArcRoot
    if(-not $root){throw 'ARC_NOT_FOUND'}
    $start=Join-Path $root 'Start.ps1'
    if(-not (Test-Path -LiteralPath $start -PathType Leaf)){throw 'ARC_BROWSER_NOT_INSTALLED'}
    # The modern ARC browser owns a SEPARATE process/session. Unlike Cascade,
    # it does not inherit the API tokens cached after startup authentication.
    $powershell=(Get-Command powershell.exe -ErrorAction Stop).Source
    $psi=New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName=$powershell
    $psi.Arguments='-STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "'+$start+'"'
    $psi.UseShellExecute=$false
    foreach($name in @('GITLAB_TOKEN','GENAI_TOKEN','DATABRICKS_TOKEN','LLM_TOKEN',
                        'GITHUB_TOKEN','CASCADE_WEB_NONINTERACTIVE')){
        if($psi.EnvironmentVariables.ContainsKey($name)){$psi.EnvironmentVariables.Remove($name)}
    }
    $proc=[System.Diagnostics.Process]::Start($psi)
    if($null -eq $proc){throw 'ARC_BROWSER_LAUNCH_FAILED'}
    return [pscustomobject]@{launched=$true;pid=[int]$proc.Id;vaultUnlocked=[bool](Test-CascadeArcVaultUnlocked);
        hint='Modern ARC browser is opening in a separate process. Its authentication does not unlock Cascade. To unlock Cascade, restart Cascade-GUI.bat and complete its startup CAC/TOTP prompts.'}
}
function Hide-CascadeConsoleAfterLaunch {
    if(-not $HideConsole){return}
    if($AuthenticateArc -and -not $script:CascadeArcAuthenticated){
        Write-Host 'Console retained because ARC authentication did not complete. Review the diagnostic output.' -ForegroundColor Yellow
        return
    }
    try {
        if(-not ('CascadeBrowser.ConsoleWindow' -as [type])){
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace CascadeBrowser {
 public static class ConsoleWindow {
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle,int command);
 }
}
'@ -ErrorAction Stop
        }
        $window=[CascadeBrowser.ConsoleWindow]::GetConsoleWindow()
        if($window -ne [IntPtr]::Zero){
            [void][CascadeBrowser.ConsoleWindow]::ShowWindow($window,0)
            Write-CascadeDiagnostic -Level INFO -Event CONSOLE_HIDDEN
        }
    }catch {
        Write-Warning 'Cannot hide console; it is safe to leave open for diagnostics.'
        Write-CascadeDiagnostic -Level WARN -Event CONSOLE_HIDE_UNAVAILABLE -Message $_.Exception.GetType().Name
    }
}
function Get-CascadeLatestSourceSnapshot {
    # Imported data has already been approved for local use. Label changes
    # re-filter it IN MEMORY; no network request, new token or production write.
    if(-not (Test-Path -LiteralPath $workspace)){return $null}
    $source=@(Get-ChildItem -LiteralPath $workspace -Filter 'source-snapshot.json' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    if($source.Count -eq 0){return $null}
    return (Get-Content -LiteralPath $source[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Latest-Plan {
    if (-not (Test-Path -LiteralPath $workspace)) { return $null }
    $paths=@(Get-ChildItem -LiteralPath $workspace -Filter 'plan.json' -File -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)
    if ($paths.Count -eq 0) { return $null }
    return $paths[0].FullName
}
# Authenticate before creating the TCP listener. ARC's CAC/TOTP dialogs may
# need interactive desktop access and must never block the HTTP request loop.
Initialize-CascadeArcAtStartup
# Browser HTTP API is strictly noninteractive even if a vault lease expires.
# Terminal workflows continue to use ARC's normal interactive behavior.
$env:CASCADE_WEB_NONINTERACTIVE='1'
# Each project prefers a unique local port. If occupied, atomically bind an
# OS-assigned loopback port; do not stop any other running application.
$preferredPort=$Port
try {
    $listener=Start-CascadeLoopbackTransport -Port $Port
} catch {
    if($_.Exception.Message -notmatch 'already in use'){throw}
    $listener=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
    $listener.Start()
    $Port=([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    Write-Host ('Preferred port '+$preferredPort+' occupied; Cascade selected '+$Port) -ForegroundColor Yellow
}
Write-CascadeDiagnostic -Level INFO -Event SERVER_START -Message ('Port '+$Port)
# Nonsecret runtime marker makes auto-selected ports inspectable and allows
# the operator to identify and stop a hidden Cascade instance.
$runtimeFile=Join-Path $logDir ('cascade-server-'+$PID+'.json')
([pscustomobject]@{pid=$PID;port=$Port;url="http://127.0.0.1:$Port/";started=(Get-Date).ToUniversalTime().ToString('o');arcUnlocked=$script:CascadeArcAuthenticated} | ConvertTo-Json -Compress) |
    Set-Content -LiteralPath $runtimeFile -Encoding UTF8
Write-Host "Cascade GUI: http://127.0.0.1:$Port/" -ForegroundColor Cyan
Write-Host 'Stop the background server using the End session control in Cascade.'
if(-not $NoBrowser){Start-Process "http://127.0.0.1:$Port/"}
Hide-CascadeConsoleAfterLaunch
try {
    while (-not $script:StopRequested) {
        try { $ctx=Read-CascadeLoopbackContext -Listener $listener -Port $Port }
        catch {
            Write-CascadeDiagnostic -Level WARN -Event BAD_LOCAL_REQUEST -Message $_.Exception.GetType().Name
            continue
        }
        $res=$ctx.Response
        $requestId=[guid]::NewGuid().ToString('N').Substring(0,12)
        $sw=[Diagnostics.Stopwatch]::StartNew()
        try {
            $path=$ctx.Request.Url.AbsolutePath
            $res.Headers.Add('X-Content-Type-Options','nosniff')
            $res.Headers.Add('Cache-Control','no-store')
            $res.Headers.Add('X-Frame-Options','DENY')
            $res.Headers.Add('Referrer-Policy','no-referrer')
            Write-CascadeDiagnostic -Level INFO -Event REQUEST_START -Route $path -RequestId $requestId
            $method=$ctx.Request.HttpMethod
            # Strictly loopback and same-origin. No CORS headers, cookies, or network binds.
            if (-not [Net.IPAddress]::IsLoopback($ctx.Request.RemoteEndPoint.Address) -or $ctx.Request.Url.Host -notin @('localhost','127.0.0.1')) {
                Send-Json $res @{error='Loopback access only'} 403
                continue
            }
            if ($path -eq '/api/session/stop' -and $method -eq 'POST') {
                # Require an explicit same-origin browser action and a custom
                # header. Cross-origin web pages cannot use this without CORS.
                $origin=[string]$ctx.Request.Headers['Origin']
                $intent=[string]$ctx.Request.Headers['X-Cascade-Intent']
                if($origin -notin @("http://127.0.0.1:$Port","http://localhost:$Port") -or $intent -cne 'end-session'){
                    Send-Json $res @{error='Operator origin and intent required'} 403
                }else{
                    Write-CascadeDiagnostic -Level INFO -Event SESSION_STOP_REQUESTED -Route $path -RequestId $requestId
                    $script:StopRequested=$true
                    Send-Json $res @{ok=$true;message='Cascade background engine shutting down.'}
                }
            } elseif ($path -eq '/api/config' -and $method -eq 'GET') {
                Send-Json $res (Read-CascadeBrowserSettings)
            } elseif ($path -eq '/api/config' -and $method -eq 'POST') {
                try {
                    Assert-CascadeSameOrigin $ctx.Request
                    $payload=Read-CascadeRequestJson $ctx.Request
                    $cfg=Set-CascadeBrowserSettings $payload
                    $null=Apply-CascadeBrowserSettings
                    Write-CascadeDiagnostic -Level INFO -Event CONFIG_UPDATED -Route $path -RequestId $requestId
                    Send-Json $res $cfg
                } catch {Send-Json $res @{error=$_.Exception.Message} 400}
            } elseif ($path -eq '/api/issue-label' -and $method -eq 'POST') {
                try {
                    # Changing label only alters a local, nonsecret view setting.
                    $origin=[string]$ctx.Request.Headers['Origin']
                    $intent=[string]$ctx.Request.Headers['X-Cascade-Intent']
                    if($origin -notin @("http://127.0.0.1:$Port","http://localhost:$Port") -or
                        $intent -cne 'change-label'){
                        Send-Json $res @{error='Operator origin and intent required'} 403
                    }else{
                        $payload=Read-CascadeRequestJson $ctx.Request
                        $cfg=Set-CascadeBrowserIssueLabel -Label ([string]$payload.issueLabel)
                        $raw=Get-CascadeLatestSourceSnapshot
                        $plan=$null
                        if($null -ne $raw){$plan=ConvertTo-CascadeGitMilPlan -Snapshot $raw -IssueLabel $cfg.issueLabel}
                        Write-CascadeDiagnostic -Level INFO -Event ISSUE_LABEL_FILTER_CHANGED -Route $path -RequestId $requestId -Message 'Exact label changed; local source re-filtered if available'
                        Send-Json $res @{issueLabel=$cfg.issueLabel;requiresSync=($null -eq $plan);plan=$plan}
                    }
                }catch {Send-Json $res @{error=$_.Exception.Message} 400}
            } elseif ($path -eq '/api/intelligence/preview' -and $method -eq 'POST') {
                # Explicit, operator-triggered dry-run only. Never perform
                # a GitLab write, LLM request, modal CAC prompt or migration.
                $origin=[string]$ctx.Request.Headers['Origin']
                $intent=[string]$ctx.Request.Headers['X-Cascade-Intent']
                if($origin -notin @("http://127.0.0.1:$Port","http://localhost:$Port") -or $intent -cne 'preview-intelligence'){
                    Send-Json $res @{error='Explicit same-origin operator action required.'} 403
                }else{
                    $raw=Get-CascadeLatestSourceSnapshot
                    if($null -eq $raw){
                        Send-Json $res @{error='No Git.mil snapshot yet. Sync the selected projects first.';code='GITMIL_SYNC_REQUIRED'} 404
                    }else{
                        try{
                            $label=[string](Read-CascadeBrowserSettings).issueLabel
                            $review=Get-CascadeIntelligencePreview -Snapshot $raw -IssueLabel $label -MaxItems 200
                            Write-CascadeDiagnostic -Level INFO -Event INTELLIGENCE_PREVIEW_FINISHED -Route $path -RequestId $requestId -Message ('Read-only analyzed issues '+[int]$review.Summary.AnalyzedIssues)
                            Send-Json $res $review
                        }catch{
                            Write-CascadeDiagnostic -Level ERROR -Event INTELLIGENCE_PREVIEW_FAILED -Route $path -RequestId $requestId -Message $_.Exception.GetType().Name
                            Send-Json $res @{error='Read-only analysis failed. Check local diagnostics.';code='INTELLIGENCE_PREVIEW_FAILED';requestId=$requestId} 500
                        }
                    }
                }
            } elseif ($path -eq '/api/integrations' -and $method -eq 'GET') {
                # Reads state only; never imports ARC, prompts, or makes external API calls.
                $cfg=Read-CascadeBrowserSettings
                $ready=Get-CascadeArcReadiness
                $checks=@(
                    [pscustomobject]@{role='GitLab';item=$cfg.gitLabArcItem;available=$null},
                    [pscustomobject]@{role='Gemini';item=$cfg.geminiArcItem;available=$null},
                    [pscustomobject]@{role='Grok';item=$cfg.grokArcItem;available=$null}
                )
                Send-Json $res @{arcInstalled=$ready.installed;arcLoaded=$ready.loaded;vaultUnlocked=$ready.unlocked;authStage=$ready.authStage;arcErrorCode=$ready.errorCode;hint=$ready.pathHint;integrations=$checks}
            } elseif ($path -eq '/api/arc/open' -and $method -eq 'POST') {
                # Never show a modal window from a synchronous HTTP request.
                try {
                    Assert-CascadeSameOrigin $ctx.Request
                    $data=Open-CascadeModernArcBrowser
                    Write-CascadeDiagnostic -Level INFO -Event ARC_BROWSER_LAUNCHED -Route $path -RequestId $requestId
                    Send-Json $res $data
                } catch {
                    $code=if($_.Exception.Message -eq 'ARC_NOT_FOUND'){'ARC_NOT_FOUND'}
                          elseif($_.Exception.Message -eq 'ARC_BROWSER_NOT_INSTALLED'){'ARC_BROWSER_NOT_INSTALLED'}
                          else {'ARC_BROWSER_LAUNCH_FAILED'}
                    $hint=switch($code){
                        'ARC_NOT_FOUND' {'Configure ARC installation folder in Data & settings, then save.'}
                        'ARC_BROWSER_NOT_INSTALLED' {'The configured ARC installation lacks Start.ps1. Update ARC to its modern browser release.'}
                        default {'Could not launch the modern ARC browser. Check execution policy and ARC logs.'}
                    }
                    Write-CascadeDiagnostic -Level ERROR -Event ARC_BROWSER_FAILED -Route $path -RequestId $requestId -Message $code
                    Send-Json $res @{code=$code;error=$hint;requestId=$requestId} 503
                }
            } elseif ($path -eq '/api/provider/test' -and $method -eq 'POST') {
                try {
                    Assert-CascadeSameOrigin $ctx.Request
                    $payload=Read-CascadeRequestJson $ctx.Request
                    $profile=[string]$payload.profile
                    if($profile -cnotin @('GenAi','Grok')){Send-Json $res @{error='Profile must be GenAi or Grok'} 400}
                    else { Send-Json $res (Get-CascadeProviderProbe -Profile $profile) }
                } catch {
                    $fault=Get-CascadeConnectionFault -Failure $_ -Service 'AI'
                    Write-CascadeDiagnostic -Level WARN -Event PROVIDER_TEST_FAILED -Route $path -RequestId $requestId -Message $fault.code
                    Send-Json $res @{connected=$false;code=$fault.code;error=$fault.error;requestId=$requestId} 503
                }
            } elseif ($path -eq '/api/gitmil/status' -and $method -eq 'GET') {
                try {
                    $null=Apply-CascadeBrowserSettings
                    Assert-CascadeNoninteractiveService GitLab
                    $root=Get-CascadeGitLabApiRoot
                    $headers=Get-CascadeGitLabHeaders
                    # Authenticated /user is independent of GITLAB_PROJECT_ID.
                    # Unlike Test-CascadeGitLabConnection, it cannot ask for a
                    # project selection or enter a CLI menu within a web request.
                    $users=Invoke-CascadeGitLabGetJson -Uri ($root+'/user') -Headers $headers -TimeoutSec 12
                    $user=@($users | Where-Object {$null -ne $_} | Select-Object -First 1)
                    if($user.Count -eq 0 -or -not $user[0].username){throw 'Git.mil returned no authenticated user.'}
                    Send-Json $res @{connected=$true;host='web.git.mil';arcLoaded=[bool](Get-Module CascadeArc);user=[string]$user[0].username}
                } catch { $fault=Get-CascadeConnectionFault -Failure $_ -Service 'GitMil'; if($fault.code -eq 'AUTH_REJECTED'){Remove-Item Env:GITLAB_TOKEN -ErrorAction SilentlyContinue}; Write-CascadeDiagnostic -Level WARN -Event GITMIL_AUTH_FAILED -Route $path -RequestId $requestId -Message $fault.code; Send-Json $res @{connected=$false;code=$fault.code;error=$fault.error;requestId=$requestId} 503 }
            } elseif ($path -eq '/api/gitmil/projects' -and $method -eq 'GET') {
                try {
                    $null=Apply-CascadeBrowserSettings
                    $null=Initialize-CascadeGitMilSession
                    $projects=@(Invoke-CascadeGitMilPaged -Path '/projects?membership=true&simple=true&order_by=last_activity_at&sort=desc' -MaxPages 2)
                    $flat=@($projects | ForEach-Object {$_} | Where-Object { $null -ne $_ -and $null -ne $_.id -and [string]$_.id -match '^[0-9]+$' })
                    Send-Json $res @{projects=$flat;count=$flat.Count}
                } catch { $fault=Get-CascadeConnectionFault -Failure $_ -Service 'GitMil'; Write-CascadeDiagnostic -Level WARN -Event GITMIL_DISCOVERY_FAILED -Route $path -RequestId $requestId -Message $fault.code; Send-Json $res @{code=$fault.code;error=$fault.error;requestId=$requestId} 503 }
            } elseif ($path -eq '/api/gitmil/labels' -and $method -eq 'POST') {
                try {
                    $origin=[string]$ctx.Request.Headers['Origin']
                    $intent=[string]$ctx.Request.Headers['X-Cascade-Intent']
                    if($origin -notin @("http://127.0.0.1:$Port","http://localhost:$Port") -or
                        $intent -cne 'list-labels'){
                        Send-Json $res @{error='Operator origin and intent required'} 403
                    }else{
                        $payload=Read-CascadeRequestJson $ctx.Request
                        $ids=@($payload.projectIds | ForEach-Object {[int]$_} | Where-Object {$_ -gt 0} | Select-Object -Unique)
                        if($ids.Count -lt 1 -or $ids.Count -gt 20){
                            Send-Json $res @{error='Select 1–20 source projects.'} 400
                        }else{
                            $null=Apply-CascadeBrowserSettings
                            $labels=Get-CascadeGitMilAvailableLabels -ProjectIds $ids
                            Send-Json $res $labels
                        }
                    }
                }catch {
                    $fault=Get-CascadeConnectionFault -Failure $_ -Service 'GitMil'
                    Write-CascadeDiagnostic -Level WARN -Event LABEL_DISCOVERY_FAILED -Route $path -RequestId $requestId -Message $fault.code
                    Send-Json $res @{error=$fault.error;code=$fault.code;requestId=$requestId} 503
                }
            } elseif ($path -eq '/api/gitmil/sync' -and $method -eq 'POST') {
                $origin=$ctx.Request.Headers['Origin']
                if ($origin -and $origin -notin @("http://localhost:$Port","http://127.0.0.1:$Port")) { Send-Json $res @{error='Origin not allowed'} 403; continue }
                if ($ctx.Request.ContentLength64 -gt 16384 -or $ctx.Request.ContentLength64 -lt 0) { Send-Json $res @{error='Invalid request size'} 413; continue }
                $reader=New-Object IO.StreamReader($ctx.Request.InputStream,[Text.Encoding]::UTF8)
                try { $payload=$reader.ReadToEnd()|ConvertFrom-Json } finally { $reader.Dispose() }
                $ids=@($payload.projectIds|ForEach-Object{[int]$_}|Where-Object{$_ -gt 0}|Select-Object -Unique)
                $days=[int]$payload.sinceDays
                if($ids.Count -lt 1 -or $ids.Count -gt 20 -or $days -lt 1 -or $days -gt 365) { Send-Json $res @{error='Select 1–20 projects and 1–365 days'} 400; continue }
                try {
                    $null=Apply-CascadeBrowserSettings
                    $cfg=Read-CascadeBrowserSettings
                    $run=Export-CascadeGitMilPlan -ProjectIds $ids -SinceDays $days -Workspace $workspace -IssueLabel ([string]$cfg.issueLabel)
                    # Persist the exact source-project selection for the next run.
                    $cfg.selectedProjectIds=$ids
                    [void](New-Item -ItemType Directory -Path $configDir -Force)
                    $temp=$configPath+'.tmp'
                    $cfg|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $temp -Encoding UTF8
                    Move-Item -LiteralPath $temp -Destination $configPath -Force
                    Send-Json $res @{runId=$run.RunId;plan=$run.Plan}
                } catch { Send-Json $res @{error=$_.Exception.Message} 503 }
            } elseif ($path -eq '/api/diagnostics' -and $method -eq 'GET') {
                $recent=@(Get-Content -LiteralPath $logFile -Tail 200 -ErrorAction SilentlyContinue | ForEach-Object {try{$_|ConvertFrom-Json}catch{}})
                Send-Json $res @{entries=$recent;requestId=$requestId}
            } elseif ($path -eq '/api/status' -and $method -eq 'GET') {
                Send-Json $res @{service='Cascade local';mode='READ_ONLY';issueLabel=(Read-CascadeBrowserSettings).issueLabel;latestPlan=([bool](Latest-Plan))}
            } elseif ($path -eq '/api/latest' -and $method -eq 'GET') {
                $raw=Get-CascadeLatestSourceSnapshot
                if($null -eq $raw){
                    Send-Json $res @{error='No imported Git.mil source snapshot is available. Synchronize selected projects first.';code='GITMIL_SYNC_REQUIRED'} 404
                }else{
                    $label=(Read-CascadeBrowserSettings).issueLabel
                    $filtered=ConvertTo-CascadeGitMilPlan -Snapshot $raw -IssueLabel ([string]$label)
                    Send-Json $res $filtered
                }
            } elseif ($path -eq '/api/scan' -and $method -eq 'POST') {
                $origin=$ctx.Request.Headers['Origin']
                if ($origin -and $origin -notin @("http://localhost:$Port","http://127.0.0.1:$Port")) {
                    Send-Json $res @{error='Origin not allowed'} 403
                    continue
                }
                if ($ctx.Request.ContentLength64 -gt 16384 -or $ctx.Request.ContentLength64 -lt 0) {
                    Send-Json $res @{error='Request body too large or invalid'} 413
                    continue
                }
                $reader=New-Object IO.StreamReader($ctx.Request.InputStream,[Text.Encoding]::UTF8)
                try { $payload=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                $roots=@($payload.roots | Where-Object { $_ -is [string] -and $_.Trim() })
                $days=[int]$payload.sinceDays
                if ($roots.Count -lt 1 -or $roots.Count -gt 15 -or $days -lt 1 -or $days -gt 365) {
                    Send-Json $res @{error='Supply 1–15 directories and a 1–365 day window.'} 400
                    continue
                }
                foreach ($root in $roots) {
                    if (-not [IO.Path]::IsPathRooted($root) -or -not (Test-Path -LiteralPath $root -PathType Container)) {
                        throw "Directory must exist and be an absolute path: $root"
                    }
                }
                $result=Invoke-CascadeAutonomousCycle -Roots $roots -SinceDays $days -Workspace $workspace
                $plan=Get-Content -LiteralPath $result.Plan -Raw -Encoding UTF8 | ConvertFrom-Json
                Send-Json $res @{runId=$result.RunId;plan=$plan}
            } elseif ($method -eq 'GET' -and $path -in @('/','/index.html','/app.css','/app.js','/example-work-items.js','/example-explorer.js','/leadership-dashboard.js','/intelligence-review-dialog.js','/intelligence-workbench.js','/stack-compatibility.js','/stack-worker.js','/nexus.js','/nexus-logo.png')) {
                $name=if($path -eq '/'){'index.html'}else{$path.TrimStart('/')}
                $file=Join-Path $ui $name
                $type=switch -Exact ($name) {
                    'index.html' {'text/html; charset=utf-8'}
                    'app.css' {'text/css; charset=utf-8'}
                    'app.js' {'application/javascript; charset=utf-8'}
                    'example-work-items.js' {'application/javascript; charset=utf-8'}
                    'example-explorer.js' {'application/javascript; charset=utf-8'}
                    'leadership-dashboard.js' {'application/javascript; charset=utf-8'}
                    'intelligence-workbench.js' {'application/javascript; charset=utf-8'}
                    'intelligence-review-dialog.js' {'application/javascript; charset=utf-8'}
                    'stack-compatibility.js' {'application/javascript; charset=utf-8'}
                    'stack-worker.js' {'application/javascript; charset=utf-8'}
                    'nexus.js' {'application/javascript; charset=utf-8'}
                    'nexus-logo.png' {'image/png'}
                }
                $bytes=[IO.File]::ReadAllBytes($file)
                $res.ContentType=$type
                $res.Headers.Add('Content-Security-Policy',"default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'")
                $res.ContentLength64=$bytes.Length
                $res.OutputStream.Write($bytes,0,$bytes.Length)
            } else { Send-Json $res @{error='Not found'} 404 }
        } catch {
            Write-CascadeDiagnostic -Level ERROR -Event REQUEST_FAILED -Route $ctx.Request.Url.AbsolutePath -RequestId $requestId -Message $_.Exception.GetType().Name
            Write-Warning ('Request failed. Diagnostic ID: '+$requestId)
            try { Send-Json $res @{error='Request failed; see diagnostics';requestId=$requestId} 500 } catch {}
        } finally {
            $sw.Stop()
            Write-CascadeDiagnostic -Level INFO -Event REQUEST_END -Route $ctx.Request.Url.AbsolutePath -RequestId $requestId -Message ('HTTP '+$res.StatusCode+' '+$sw.ElapsedMilliseconds+'ms')
            try{$res.Close()}catch{}
        }
    }
} finally { $listener.Stop(); if($runtimeFile -and (Test-Path -LiteralPath $runtimeFile)){Remove-Item -LiteralPath $runtimeFile -Force -ErrorAction SilentlyContinue} }