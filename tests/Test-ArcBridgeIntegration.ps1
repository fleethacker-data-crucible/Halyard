#requires -Version 5.1
# Offline regression: Cascade's exported ARC bridge, exact credential mapping, no prompt paths.
# Uses a synthetic temporary pm.ps1; NEVER reads the user's real ARC vault or calls an API.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path ([IO.Path]::GetTempPath()) ('CascadeFakeArc-' + [guid]::NewGuid().ToString('N'))
$envNames = @('ARC_PATH','CASCADE_ARC_ITEM_GitLab','CASCADE_ARC_ITEM_GenAi','CASCADE_ARC_ITEM_Grok',
              'GITLAB_TOKEN','GENAI_TOKEN','DATABRICKS_TOKEN','LLM_URL','LLM_MODEL',
              'GENAI_URL','GENAI_MODEL','DATABRICKS_URL','DATABRICKS_MODEL','ADVANA_URL','ADVANA_MODEL')
$prior = @{}
foreach($name in $envNames) { $prior[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
try {
    foreach($name in $envNames) { [Environment]::SetEnvironmentVariable($name,$null,'Process') }
    [void](New-Item -Path $root -ItemType Directory -Force)
    $stub = @'
$script:PmState = @{ VaultUnlocked = $false }
$libOrder = @('00-test-arc.ps1')
function Ensure-VaultUnlocked {
    if($script:PmState.VaultUnlocked){return $true}
    $script:PmState.VaultUnlocked = $true
    return $true
}
function Get-PmStarkApiKey {
    param([string]$VaultName)
    if (-not $script:PmState.VaultUnlocked) { throw 'Synthetic vault is locked.' }
    switch ($VaultName) {
        'gitmil' { return 'test-gitmil-token' }
        'genapi' { return 'test-genai-token' }
        'advana-api' { return 'test-advana-token' }
        default { throw 'Unknown synthetic item' }
    }
}
function Find-VaultRows {
    param([string]$Name)
    if($Name -eq 'nested-fallback'){
        $row=[pscustomobject]@{Service='nested-fallback';Secret='TEST-PROTECTED:expected-fallback'}
        $inner=New-Object System.Collections.ArrayList
        [void]$inner.Add($row)
        $outer=New-Object System.Collections.ArrayList
        [void]$outer.Add($inner)
        return ,$outer
    }
    if($Name -eq 'cipher-probe'){
        return [pscustomobject]@{Service='cipher-probe';Secret='TEST-INVALID-CIPHERTEXT'}
    }
    return @()
}
function Unprotect-SecretText {
    param([string]$Protected)
    if($Protected -eq 'TEST-PROTECTED:expected-fallback'){return 'expected-fallback'}
    throw 'Not an ARC encrypted secret'
}
function New-EmptyVaultList { return @() }
function Get-LiveVault { return @() }
function Start-PmGui { throw 'Legacy WPF flow is not expected for Cascade authentication.' }
'@
    [IO.File]::WriteAllText((Join-Path $root 'pm.ps1'), $stub)
    $env:ARC_PATH=$root
    $env:CASCADE_ARC_ITEM_GitLab='gitmil'
    $env:CASCADE_ARC_ITEM_GenAi='genapi'
    $env:CASCADE_ARC_ITEM_Grok='advana-api'
    Import-Module (Join-Path $repo 'src\Cascade\Cascade.psd1') -Force
    if(-not (Get-Command Test-CascadeArcVaultUnlocked -ErrorAction SilentlyContinue)) {
        throw 'Cascade.psd1 forgot to export Test-CascadeArcVaultUnlocked; browser cannot query ARC vault state.'
    }
    if (Test-CascadeArcVaultUnlocked) { throw 'Unloaded synthetic ARC must not be marked unlocked.' }
    # A partially copied ARC installation must FAIL before any loader state
    # is imported. The previous Cascade loader accepted partial ARC helpers.
    $missing=@(Get-CascadeArcMissingFiles -ArcRoot $root)
    if(@($missing).Count -ne 1 -or $missing[0] -ne 'lib/00-test-arc.ps1') {
        throw 'Missing ARC library not detected before import.'
    }
    if(Import-CascadeArcSession -ArcRoot $root -Quiet) {
        throw 'Cascade accepted incomplete ARC installation.'
    }
    if((Test-CascadeArcVaultUnlocked) -or (Get-Module CascadeArc)) {
        throw 'Partial ARC import left a false unlocked session.'
    }
    if((Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue)) {
        throw 'Partial ARC import published vault helpers.'
    }
    [void](New-Item -Path (Join-Path $root 'lib') -ItemType Directory -Force)
    [IO.File]::WriteAllText((Join-Path $root 'lib/00-test-arc.ps1'),'# Synthetic ARC library')
    if(@(Get-CascadeArcMissingFiles -ArcRoot $root).Count -gt 0) {
        throw 'Required test ARC library still reported missing.'
    }
    if(-not (Import-CascadeArcSession -ArcRoot $root -Quiet)) {
        throw 'Unable to import COMPLETE synthetic ARC module.'
    }
    if (Test-CascadeArcVaultUnlocked) { throw 'Locked synthetic ARC reported unlocked.' }
    if(-not (Get-Command Ensure-VaultUnlocked -ErrorAction SilentlyContinue)) {
        throw 'Real ARC CAC/TOTP helper unavailable after import.'
    }
    if(-not (Ensure-VaultUnlocked)){ throw 'Synthetic ARC authentication returned false.' }
    if(-not (Test-CascadeArcVaultUnlocked)) { throw 'Unlocked ARC state not detected by Cascade.' }
    if(-not (Import-CascadeArcSession -ArcRoot $root -Quiet)) {
        throw 'Already unlocked ARC session import was not idempotent.'
    }
    if(-not (Test-CascadeArcVaultUnlocked)) {
        throw 'Re-import check reset the unlocked ARC session.'
    }
    # Older ARC row APIs can wrap ArrayLists multiple times. The Keystone-
    # compatible fallback must unroll them without returning ciphertext.
    $nested=Get-CascadeArcSecretByName -ServiceName 'nested-fallback'
    if($nested -ne 'expected-fallback'){
        $rows=@(& (Get-Module Cascade) { Convert-CascadeArcRows -InputObject (Find-VaultRows -Name 'nested-fallback') })
        $firstType=if($rows.Count){$rows[0].GetType().Name}else{'NONE'}
        $rowService=if($rows.Count){[string]$rows[0].Service}else{'NONE'}
        $decryptOk=if($rows.Count){[bool]((& (Get-Module Cascade) { param($row) Convert-CascadeArcSecretRow -Row $row } $rows[0]) -eq 'expected-fallback')}else{$false}
        throw ('Nested ARC row fallback failed: count='+$rows.Count+' firstType='+$firstType+' serviceMatched='+($rowService -eq 'nested-fallback')+' decryptOk='+$decryptOk)
    }
    if(Get-CascadeArcSecretByName -ServiceName 'cipher-probe'){
        throw 'Cascade returned invalid ciphertext as a provider token.'
    }
    $git = Get-CascadeGitLabHeaders
    if($git['PRIVATE-TOKEN'] -ne 'test-gitmil-token'){throw 'GitLab did not retrieve synthetic gitmil item.'}
    if ((Get-CascadeLlmToken -ForProfile GenAi) -ne 'test-genai-token') {throw 'Wrong synthetic GenAi ARC item.'}
    if ((Get-CascadeLlmToken -ForProfile Grok) -ne 'test-advana-token') {throw 'Wrong synthetic Advana ARC item.'}
    $env:LLM_URL='https://api.genai.mil/v1/chat/completions'
    $env:LLM_MODEL='incompatible-generic-model'
    $grok = Get-CascadeLlmConfig -Profile Grok
    if ($grok.Url -notmatch 'databricks.mil' -or $grok.Model -eq 'incompatible-generic-model') {
        throw 'Grok inherited GenAi generic URL or model.'
    }
    $env:LLM_URL='https://advana-jupiter.cloud.databricks.mil/serving-endpoints/test/invocations'
    $genai = Get-CascadeLlmConfig -Profile GenAi
    if ($genai.Url -notmatch '^https://api.genai.mil/' -or $genai.Model -eq 'incompatible-generic-model') {
        throw 'GenAi inherited Advana generic URL or model.'
    }
    $env:DATABRICKS_URL='https://advana-jupiter.cloud.databricks.mil/serving-endpoints/team/invocations'
    $env:DATABRICKS_MODEL='approved-team-model'
    $override = Get-CascadeLlmConfig -Profile Grok
    if ($override.Model -ne 'approved-team-model' -or $override.Url -notmatch '/team/invocations') {
        throw 'Profile-specific model / endpoint override ignored.'
    }
    Write-Host 'PASS: missing-library rejection, one-time ARC module import, CAC unlock state and isolated provider tokens.' -ForegroundColor Green
} finally {
    foreach($name in $envNames) { [Environment]::SetEnvironmentVariable($name,$prior[$name],'Process') }
    Remove-Module CascadeArc,Cascade -Force -ErrorAction SilentlyContinue
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}