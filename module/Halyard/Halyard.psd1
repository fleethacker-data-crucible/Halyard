@{
    RootModule        = 'Halyard.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '5f0d7c2e-5a63-4b8e-9a1d-2f8e4c7b9a10'
    Author            = 'Anthony'
    Description       = 'Halyard launcher and command-line wrappers. Loads Halyard.Core.dll, unlocks Lazarette (formerly ARC) once in the console when required, and starts the local browser host.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport = @(
        'Get-HalyardRoot',
        'Import-HalyardCore',
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
    AliasesToExport   = @('Connect-HalyardArc')   # former name, kept for existing scripts
    PrivateData = @{ PSData = @{ Tags = @('agile', 'gitlab', 'github', 'backlog') } }
}
