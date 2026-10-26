@{
    RootModule        = 'Halyard.psm1'
    ModuleVersion     = '1.1.0'
    GUID              = '5f0d7c2e-5a63-4b8e-9a1d-2f8e4c7b9a10'
    Author            = 'Anthony'
    Description       = 'Halyard launcher and command-line wrappers. Compiles the Halyard engine from its C# source in memory (no DLL is loaded from disk), unlocks Lazarette once in the console when required, and starts the local browser host.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport = @(
        'Get-HalyardRoot',
        'Import-HalyardCore',
        'Get-HalyardCoreSource',
        'Get-HalyardCoreLoadInfo',
        'Initialize-HalyardEnvironment',
        'Connect-HalyardLazarette',
        'Start-Halyard',
        'Test-HalyardConnection',
        'New-HalyardSyntheticCatalog',
        'Invoke-HalyardSeed',
        'Invoke-HalyardTeardown',
        'Get-HalyardWorkItem'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData = @{ PSData = @{ Tags = @('agile', 'gitlab', 'github', 'backlog') } }
}
