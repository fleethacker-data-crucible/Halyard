#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$port=18000 + (Get-Random -Minimum 0 -Maximum 3000)
$script=Join-Path $root 'scripts/Start-CascadeWeb.ps1'
$p=$null
$pFallback=$null
$blocker=$null
$labelSnapshotDir=$null
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('cascade-arc-browser-test-'+[guid]::NewGuid().ToString('N'))
$oldArc=$env:ARC_PATH
$configPath=Join-Path $root 'config/browser-settings.json'
$priorConfig=if(Test-Path -LiteralPath $configPath){Get-Content -LiteralPath $configPath -Raw -Encoding UTF8}else{$null}
[void](New-Item -ItemType Directory -Path $fixture -Force)
@'
$script:PmState=@{VaultUnlocked=$false}
function Ensure-VaultUnlocked {
    $script:PmState.VaultUnlocked=$true
    return $true
}
function Get-PmStarkApiKey {
    param([string]$VaultName)
    if(-not $script:PmState.VaultUnlocked){throw 'Synthetic vault locked'}
    switch($VaultName){
        'gitmil' {return 'ci-gitlab-token'}
        'genapi' {return 'ci-genai-token'}
        'advana-api' {return 'ci-advana-token'}
        default {throw 'Unknown synthetic item'}
    }
}
'@ | Set-Content (Join-Path $fixture 'pm.ps1') -Encoding ASCII
'Start-Sleep -Seconds 5' | Set-Content (Join-Path $fixture 'Start.ps1') -Encoding ASCII
$env:ARC_PATH=$fixture
try {
    $p=Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @('-NoLogo','-NoProfile','-Sta','-ExecutionPolicy','Bypass','-File',('"'+$script+'"'),'-Port',$port,'-NoBrowser','-AuthenticateArc')
    $url="http://127.0.0.1:$port"
    $ready=$false
    for($i=0;$i -lt 30;$i++){
        Start-Sleep -Milliseconds 300
        try{
            $status=Invoke-RestMethod -Uri "$url/api/status" -TimeoutSec 2
            if($status.service -eq 'Cascade local'){$ready=$true;break}
        } catch{}
        if($p.HasExited){break}
    }
    if(-not $ready){throw 'Server failed startup or /api/status'}
    $page=Invoke-WebRequest -Uri "$url/" -UseBasicParsing -TimeoutSec 5
    if($page.StatusCode -ne 200 -or $page.Content -notmatch 'Cascade'){throw 'Loopback GET / did not serve the browser UI'}
    $integrations=Invoke-RestMethod -Uri "$url/api/integrations" -TimeoutSec 5
    if($null -eq $integrations.arcInstalled){throw 'Missing explicit ARC readiness status'}
    if(-not $integrations.arcLoaded -or -not $integrations.vaultUnlocked) {
        throw 'Startup ARC mock failed to unlock vault in the Cascade server process.'
    }
    if($integrations.arcErrorCode -or $integrations.authStage -ne 'ready'){
        throw ('Successful synthetic ARC must be ready without error; got '+$integrations.arcErrorCode+'/'+$integrations.authStage)
    }
    $config=Invoke-RestMethod -Uri "$url/api/config" -TimeoutSec 5
    if($null -eq $config.PSObject.Properties['issueLabel']){throw 'Exact issue label setting is absent.'}
    if([string]$config.issueLabel -ne ''){throw 'New installations must default to all issue labels.'}
    $denied=$false
    try {
        Invoke-RestMethod -Uri "$url/api/issue-label" -Method Post -ContentType 'application/json' -Body '{"issueLabel":"team::Data-Crucible"}' -TimeoutSec 5 | Out-Null
    } catch { try{$denied=([int]$_.Exception.Response.StatusCode -eq 403)}catch{} }
    if(-not $denied){throw 'Issue label change without operator origin/intent must be rejected.'}
    $headers=@{'Origin'=$url;'X-Cascade-Intent'='change-label'}
    $selected=Invoke-RestMethod -Uri "$url/api/issue-label" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"issueLabel":"team::Data-Crucible"}' -TimeoutSec 5
    if($selected.issueLabel -ne 'team::Data-Crucible' -or -not $selected.requiresSync){throw 'Issue label selection not persisted.'}
    if((Invoke-RestMethod -Uri "$url/api/config" -TimeoutSec 5).issueLabel -ne 'team::Data-Crucible'){throw 'Selected issue label unavailable through config.'}
    $invalid=$false
    try{
        $invalidValue=('bad'+[char]10+'label')
        $badBody=@{issueLabel=$invalidValue}|ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$url/api/issue-label" -Method Post -Headers $headers -ContentType 'application/json' -Body $badBody -TimeoutSec 5 | Out-Null
    }catch{try{$invalid=([int]$_.Exception.Response.StatusCode -eq 400)}catch{}}
    if(-not $invalid){throw 'Control characters in issue label were accepted.'}
    $clear=Invoke-RestMethod -Uri "$url/api/issue-label" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"issueLabel":""}' -TimeoutSec 5
    if($clear.issueLabel -ne ''){throw 'Clear label must restore all labels.'}
    $discoveryDenied=$false
    try{
        Invoke-RestMethod -Uri "$url/api/gitmil/labels" -Method Post -ContentType 'application/json' -Body '{"projectIds":[16629]}' -TimeoutSec 5 | Out-Null
    }catch{try{$discoveryDenied=([int]$_.Exception.Response.StatusCode -eq 403)}catch{}}
    if(-not $discoveryDenied){throw 'Project label discovery must require operator origin and intent.'}
    $badIds=$false
    try{
        Invoke-RestMethod -Uri "$url/api/gitmil/labels" -Method Post -Headers @{'Origin'=$url;'X-Cascade-Intent'='list-labels'} -ContentType 'application/json' -Body '{"projectIds":[]}' -TimeoutSec 5 | Out-Null
    }catch{try{$badIds=([int]$_.Exception.Response.StatusCode -eq 400)}catch{}}
    if(-not $badIds){throw 'Label discovery accepted an empty project selection.'}
    # Validate the actual HTTP re-filter path from an OFFLINE, saved Git.mil
    # snapshot: no token, CAC, or remote API call should be needed.
    $labelSnapshotDir=Join-Path (Join-Path $root 'output/autonomous') ('label-fixture-'+[guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $labelSnapshotDir -Force)
    $localSnapshot=[pscustomobject]@{
        SchemaVersion=2;Source='web.git.mil';GeneratedUtc='2026-10-22T00:00:00Z';SinceDays=30
        User=[pscustomobject]@{username='test-user'};ArcLoaded=$false;ReadOnly=$true
        TruncatedPotentially=$false;Projects=@([pscustomobject]@{id=16629;path_with_namespace='navy/shared-cbr'})
        Issues=@(
            [pscustomobject]@{id=1;iid=1;project_id=16629;title='Labeled issue';description='';state='opened';labels=@('team::Data-Crucible');web_url='https://web.git.mil/a'},
            [pscustomobject]@{id=2;iid=2;project_id=16629;title='Other issue';description='';state='opened';labels=@('team::Other');web_url='https://web.git.mil/b'}
        )
        Activity=@();MergeRequests=@();Pipelines=@();Errors=@()
    }
    $localSnapshot|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $labelSnapshotDir 'source-snapshot.json') -Encoding UTF8
    $filtered=Invoke-RestMethod -Uri "$url/api/issue-label" -Method Post -Headers $headers -Body '{"issueLabel":"team::Data-Crucible"}' -ContentType 'application/json' -TimeoutSec 8
    if($filtered.requiresSync -or @($filtered.plan.Issues).Count -ne 1 -or $filtered.plan.Issues[0].iid -ne 1){
        throw 'The browser did not instantly re-filter the local Git.mil source snapshot by exact issue label.'
    }
    if(@($filtered.plan.Scope.AvailableLabels).Count -ne 2){throw 'Imported label suggestions are missing.'}
    $fetched=Invoke-RestMethod -Uri "$url/api/latest" -TimeoutSec 8
    if(@($fetched.Issues).Count -ne 1){throw 'Latest imported source should respect currently selected label.'}
    $notAllowed=$false
    try {
        Invoke-RestMethod -Uri "$url/api/intelligence/preview" -Method Post -Body '{}' -ContentType 'application/json' -TimeoutSec 5 | Out-Null
    }catch{try{$notAllowed=([int]$_.Exception.Response.StatusCode -eq 403)}catch{}}
    if(-not $notAllowed){throw 'Read-only intelligence preview accepted a request without operator origin and intent.'}
    $intelligence=Invoke-RestMethod -Uri "$url/api/intelligence/preview" -Method Post -Body '{}' -Headers @{'Origin'=$url;'X-Cascade-Intent'='preview-intelligence'} -ContentType 'application/json' -TimeoutSec 20
    if($intelligence.Mode -ne 'READ_ONLY' -or $intelligence.Summary.LabelMatchingIssues -ne 1 -or @($intelligence.Items).Count -ne 1){
        throw 'Live local HTTP intelligence review did not apply the exact GitLab label.'
    }
    if($intelligence.Items[0].Title -ne 'Labeled issue' -or -not $intelligence.Items[0].DraftDescription){
        throw 'Normalization preview failed to use existing work-item intelligence engine.'
    }
    foreach($file in @('example-explorer.js','leadership-dashboard.js','intelligence-review-dialog.js','intelligence-workbench.js','stack-compatibility.js','stack-worker.js')){
        $asset=Invoke-WebRequest -Uri "$url/$file" -UseBasicParsing -TimeoutSec 6
        if($asset.StatusCode -ne 200 -or $asset.Headers['Content-Type'] -notmatch 'javascript'){
            throw ('Browser module not served: '+$file)
        }
    }

    $unfiltered=Invoke-RestMethod -Uri "$url/api/issue-label" -Method Post -Headers $headers -Body '{"issueLabel":""}' -ContentType 'application/json' -TimeoutSec 8
    if($unfiltered.requiresSync -or @($unfiltered.plan.Issues).Count -ne 2){
        throw 'Clearing label must restore the imported issues without another Git.mil sync.'
    }
    if(@($localSnapshot.Issues).Count -ne 2){throw 'Local label re-filter altered source objects.'}

    if($config.genaiUrl -notmatch '^https://api.genai.mil/' -or $config.advanaUrl -notmatch 'databricks.mil'){
        throw 'Separate GenAI and Advana endpoint settings not returned.'
    }
    # Never permit an ARC provider token to be sent to arbitrary user-supplied HTTPS URLs.
    $invalid=[ordered]@{
        gitLabUrl='https://web.git.mil';arcPath=''
        gitLabArcItem='gitmil';geminiArcItem='genapi';grokArcItem='advana-api'
        genaiUrl='https://example.org/v1/chat/completions';genaiModel='gemini-3.8-flash'
        advanaUrl='https://advana-jupiter.cloud.databricks.mil/serving-endpoints/test/invocations'
        advanaModel='system.ai.grok-4-6';sinceDays=30;localRoots=@();selectedProjectIds=@()
    }
    $rejected=$false
    try {
        $body=($invalid|ConvertTo-Json -Depth 5 -Compress)
        Invoke-RestMethod -Uri "$url/api/config" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 5 | Out-Null
    }catch {
        try {$rejected=([int]$_.Exception.Response.StatusCode -eq 400)}catch{}
    }
    if(-not $rejected){throw 'Unapproved GenAI endpoint should be rejected before any token can be sent.'}

    $logo=Invoke-WebRequest -Uri "$url/nexus-logo.png" -UseBasicParsing -TimeoutSec 5
    if($logo.StatusCode -ne 200 -or $logo.Headers['Content-Type'] -notmatch 'image/png') {throw 'Selected Nexus PNG not served'}
    # Using a fresh process without ARC/credentials, the provider test must
    # fail quickly. It must never block on a terminal selection or CAC prompt.
    $sw=[Diagnostics.Stopwatch]::StartNew()
    $failed=$false
    try{
        Invoke-RestMethod -Uri "$url/api/provider/test" -Method Post -Body '{"profile":"GenAi"}' -ContentType 'application/json' -TimeoutSec 8 | Out-Null
    }catch{ $failed=$true }
    $sw.Stop()
    if(-not $failed -or $sw.Elapsed.TotalSeconds -gt 7){throw 'Missing credential must fail fast, noninteractively'}
    # Opening the modern ARC window starts a SEPARATE process and must reply
    # immediately, not block the only local API request handler.
    $arcStart=[Diagnostics.Stopwatch]::StartNew()
    $modern=Invoke-RestMethod -Uri "$url/api/arc/open" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 4
    $arcStart.Stop()
    if(-not $modern.launched -or $arcStart.Elapsed.TotalSeconds -gt 3){
        throw 'Modern ARC launch blocked the single-threaded web request loop.'
    }
    $check=Invoke-RestMethod -Uri "$url/api/status" -TimeoutSec 3
    if($check.service -ne 'Cascade local'){throw 'API unresponsive after ARC browser launch'}
    # Attempted cross-origin stop without Origin and intent must be forbidden.
    $stopRejected=$false
    try{
        Invoke-RestMethod -Uri "$url/api/session/stop" -Method Post -Body '{}' -ContentType 'application/json' -TimeoutSec 3 | Out-Null
    }catch{
        try{$stopRejected=([int]$_.Exception.Response.StatusCode -eq 403)}catch{}
    }
    if(-not $stopRejected){throw 'Untrusted shutdown request must be rejected'}
    $stop=Invoke-RestMethod -Uri "$url/api/session/stop" -Method Post -Body '{}' -ContentType 'application/json' -Headers @{'Origin'=$url;'X-Cascade-Intent'='end-session'} -TimeoutSec 3
    if(-not $stop.ok){throw 'Operator stop endpoint failed'}
    if(-not $p.WaitForExit(3500)){throw 'Cascade process did not stop after browser shutdown'}

    # A second Cascade instance must never shut down another project's port
    # owner. Verify the occupied preferred port falls back to a real free port.
    $occupiedPort=$port+1
    $blocker=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$occupiedPort)
    $blocker.Start()
    $pFallback=Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @('-NoLogo','-NoProfile','-Sta','-ExecutionPolicy','Bypass','-File',('"'+$script+'"'),'-Port',$occupiedPort,'-NoBrowser')
    $marker=Join-Path (Join-Path $root 'logs') ('cascade-server-'+$pFallback.Id+'.json')
    $chosenPort=0
    for($i=0;$i -lt 40;$i++){
        Start-Sleep -Milliseconds 300
        if(Test-Path -LiteralPath $marker){
            try{$chosenPort=[int]((Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).port);break}catch{}
        }
        if($pFallback.HasExited){break}
    }
    if($chosenPort -lt 1024 -or $chosenPort -eq $occupiedPort){throw 'Port fallback did not select a different valid local port.'}
    $fallbackUrl="http://127.0.0.1:$chosenPort"
    $fallbackStatus=Invoke-RestMethod -Uri "$fallbackUrl/api/status" -TimeoutSec 3
    if($fallbackStatus.service -ne 'Cascade local'){throw 'Auto-selected port is not serving API.'}
    if(-not $blocker.Server.IsBound){throw 'Another service listener was disrupted by Cascade startup.'}
    Write-Host 'PASS: TCP server, startup ARC, modern ARC nonblocking launch, origin-guarded shutdown, auto-port fallback' -ForegroundColor Green
 } finally {
    if($null -ne $p -and -not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}
    if($null -ne $pFallback -and -not $pFallback.HasExited){Stop-Process -Id $pFallback.Id -Force -ErrorAction SilentlyContinue}
    if($null -ne $blocker){$blocker.Stop()}
    if($null -ne $labelSnapshotDir){Remove-Item -LiteralPath $labelSnapshotDir -Force -Recurse -ErrorAction SilentlyContinue}
    if($null -eq $oldArc){Remove-Item Env:ARC_PATH -ErrorAction SilentlyContinue}else{$env:ARC_PATH=$oldArc}
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
    # Never leave the test's workspace selection in an operator's real settings.
    if($null -eq $priorConfig){
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    }else{
        [IO.File]::WriteAllText($configPath,$priorConfig,(New-Object Text.UTF8Encoding($false)))
    }
}