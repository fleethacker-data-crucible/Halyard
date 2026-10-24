param(
    [Parameter(Mandatory)][string]$PlanPath,
    [Parameter(Mandatory)][string[]]$ApprovedKeys,
    [Parameter(Mandatory)][int]$ProjectId,
    [switch]$Execute
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'src\Cascade\Cascade.psd1') -Force
Invoke-CascadeApprovedSandboxPlan -PlanPath $PlanPath -ApprovedKeys $ApprovedKeys -ProjectId $ProjectId -Execute:$Execute
