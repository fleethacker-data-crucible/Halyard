#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Menu','Offline','All','Schema','Integrity','Agent','Discovery','Autonomy','GitLab','Llm','Online')][string]$Suite='Menu')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
function Invoke-CascadeTestScript {
    param([string]$Relative)
    $path=Join-Path $root $Relative
    if(-not(Test-Path -LiteralPath $path)){throw "Missing test: $Relative"}
    Write-Host "TEST: $Relative" -ForegroundColor Cyan
    # Isolated process ensures scripts calling exit do not terminate the suite and
    # prevents one module session from contaminating the next test.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $path
    if($LASTEXITCODE -ne 0){throw "Test failed: $Relative (exit $LASTEXITCODE)"}
}
function Invoke-CascadeOfflineTests {
    $passed=0
    foreach($test in @('tests\Test-Schema.ps1','tests\Test-Integrity.ps1','tests\Test-AgentRuntime.ps1','tests\Test-Discovery.ps1','tests\Test-Autonomy.ps1','tests\Test-RepositoryLayout.ps1','tests\Test-ArcBridgeIntegration.ps1','tests\Test-ArcStartupFailures.ps1','tests\Test-WorkspaceScope.ps1','tests\Test-GitMilLabelSearch.ps1','tests\Test-TopologyAudit.ps1','tests\Test-IntelligenceReview.ps1','tests\Test-RelationshipReview.ps1','tests\Test-WebApi.ps1')) {
        Invoke-CascadeTestScript -Relative $test
        $passed++
    }
    Write-Host ("PASS: {0} offline PowerShell tests" -f $passed) -ForegroundColor Green
}
function Invoke-CascadeOnlineTests {
    # Explicitly requested; may require operator authentication.
    Invoke-CascadeTestScript -Relative 'tests\Test-GitLab.ps1'
    Invoke-CascadeTestScript -Relative 'scripts\Invoke-TestLlm.ps1'
}
try {
    switch($Suite) {
        'Menu' {
            while($true){
                Write-Host '[1] Offline suite (safe)  [2] GitLab connection  [3] LLM connection  [4] Offline+Online  [B] Back' -ForegroundColor Cyan
                $choice=Read-Host 'Test'
                switch($choice){
                    '1' {Invoke-CascadeOfflineTests}
                    '2' {Invoke-CascadeTestScript -Relative 'tests\Test-GitLab.ps1'}
                    '3' {Invoke-CascadeTestScript -Relative 'scripts\Invoke-TestLlm.ps1'}
                    '4' {Invoke-CascadeOfflineTests; Invoke-CascadeOnlineTests}
                    'B' {return}
                    'b' {return}
                    default {Write-Host 'Unknown selection'}
                }
            }
        }
        'Offline' {Invoke-CascadeOfflineTests}
        'All' {Invoke-CascadeOfflineTests}
        'Online' {Invoke-CascadeOnlineTests}
        'Schema' {Invoke-CascadeTestScript -Relative 'tests\Test-Schema.ps1'}
        'Integrity' {Invoke-CascadeTestScript -Relative 'tests\Test-Integrity.ps1'}
        'Agent' {Invoke-CascadeTestScript -Relative 'tests\Test-AgentRuntime.ps1'}
        'Discovery' {Invoke-CascadeTestScript -Relative 'tests\Test-Discovery.ps1'}
        'Autonomy' {Invoke-CascadeTestScript -Relative 'tests\Test-Autonomy.ps1','tests\Test-RepositoryLayout.ps1','tests\Test-WebApi.ps1'}
        'GitLab' {Invoke-CascadeTestScript -Relative 'tests\Test-GitLab.ps1'}
        'Llm' {Invoke-CascadeTestScript -Relative 'scripts\Invoke-TestLlm.ps1'}
    }
} catch {
    Write-Error $_.Exception.Message
    exit 1
}