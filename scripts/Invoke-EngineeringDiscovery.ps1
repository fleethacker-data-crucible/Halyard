param(
    [Parameter(Mandatory)][string[]]$Roots,
    [int]$SinceDays = 30,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$base = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $base 'src\Cascade\Cascade.psd1') -Force
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $base 'output' }
Export-CascadeDiscovery -Roots $Roots -SinceDays $SinceDays -OutputDirectory $OutputDirectory | Format-List
