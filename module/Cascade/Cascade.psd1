@{
    RootModule        = 'Cascade.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '5f0d7c2e-5a63-4b8e-9a1d-2f8e4c7b9a10'
    Author            = 'Anthony'
    Description       = 'Cascade launcher and command-line wrappers. Loads Cascade.Core.dll, unlocks ARC once in the console when required, and starts the local browser host.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport = @(
        'Get-CascadeRoot',
        'Import-CascadeCore',
        'Initialize-CascadeEnvironment',
        'Connect-CascadeArc',
        'Start-Cascade',
        'Test-CascadeConnection',
        'New-CascadeSyntheticCatalog',
        'Invoke-CascadeSeed',
        'Invoke-CascadeTeardown',
        'Get-CascadeWorkItem'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData = @{ PSData = @{ Tags = @('agile', 'gitlab', 'github', 'backlog') } }
}
