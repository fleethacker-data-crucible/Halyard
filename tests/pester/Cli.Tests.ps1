#requires -Version 5.1
# Pester 5 tests for the command line surface: Get-HalyardCommand and scripts\Halyard.ps1 (Fleet CLI parity).

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $script:Root 'module\Halyard\Halyard.psd1') -Force
    $script:Cli = Join-Path $script:Root 'scripts\Halyard.ps1'
}

AfterAll {
    Remove-Module Halyard -ErrorAction SilentlyContinue
}

Describe 'halyard commands' {
    It 'Get-HalyardCommand -Json returns the versioned document' {
        $doc = Get-HalyardCommand -Json | ConvertFrom-Json
        $doc.app | Should -Be 'halyard'
        $doc.schemaVersion | Should -Be 1
        @($doc.commands).Count | Should -BeGreaterThan 50
        $doc.commands[0].PSObject.Properties.Name | Should -Be @('name', 'method', 'route', 'summary', 'writes', 'async')
    }
    It 'names every command halyard <noun> <verb>' {
        foreach ($c in (Get-HalyardCommand)) { $c.Name | Should -Match '^halyard [a-z-]+ [a-z-]+$' }
    }
    It 'prints the document with --json and writes UTF-8 without a BOM with --out' {
        $text = (& $script:Cli commands --json) -join "`n"
        ($text | ConvertFrom-Json).app | Should -Be 'halyard'
        $out = Join-Path ([IO.Path]::GetTempPath()) ('halyard-cmds-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            & $script:Cli commands --json --out $out
            $bytes = [IO.File]::ReadAllBytes($out)
            $bytes[0] | Should -Not -Be 0xEF
            $bytes[0] | Should -Not -Be 0xFF
            ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json).schemaVersion | Should -Be 1
        }
        finally { Remove-Item $out -ErrorAction SilentlyContinue }
    }
    It 'agrees with the engine route list in the source' {
        $registry = Get-Content (Join-Path $script:Root 'src\Halyard.Core\Host\CommandRegistry.cs') -Raw
        foreach ($c in (Get-HalyardCommand)) { $registry | Should -Match ([regex]::Escape($c.Route)) }
    }
}
