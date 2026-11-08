#requires -Version 5.1
# Lazarette Lite fallback for Halyard.
# Connect-HalyardLazarette against a Lite-shaped provider: lib/Lazarette.ps1
# forwarding to a fake backend, with Unlock-LazaretteVaultInteractive as a
# no-op success and NO Test-LazaretteVaultUnlocked command (Lite has no vault
# state to test). A vault-shaped provider (Test-LazaretteVaultUnlocked present)
# and a refused unlock are covered too. Fake only; no real Credential Manager,
# no network, no Lazarette install needed. ASCII only for Windows PowerShell 5.1.
Describe 'Halyard Lazarette Lite fallback' {
    BeforeAll {
        # Pester 5: set $script: state and helpers here, not at file top level â€” top-level
        # $script: assignments did not survive into BeforeAll on the runner
        # (v2: Test-Path -LiteralPath $null). This matches the pattern in
        # tests/pester/Halyard.Tests.ps1, which resolves paths in BeforeAll.
        $script:HalyardModuleFile = Join-Path $PSScriptRoot '..\module\Halyard\Lazarette.ps1'
        $script:LiteStub = {
            function Unlock-LazaretteVaultInteractive {
                param($DataDir)
                if ($DataDir) { throw 'Lite uses Windows Credential Manager and has no vault directory.' }
                return $true
            }
            function Get-LazaretteSecret {
                [CmdletBinding()]
                param([Parameter(Mandatory)][string]$Service, [string]$User = '', [string]$Field = '')
                if ($Field) { throw "Lazarette Lite item '$Service' has no field '$Field'." }
                switch ($Service) {
                    'gitlab' { return 'fake-gitlab-secret' }
                    'github' { return 'fake-github-secret' }
                    default { throw "Lazarette Lite has no item '$Service'." }
                }
            }
            function Get-LazaretteItemNames { return @() }
        }

        # Helper lives here too: a top-level function is not visible
        # inside It blocks in Pester 5 (v3: New-LiteFixture not recognized).
        function New-LiteFixture {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory)][string]$Root,
                [bool]$UnlockResult = $true,
                [switch]$WithTestCommand
            )
            $lib = Join-Path $Root 'lib'
            New-Item -ItemType Directory -Force -Path $lib | Out-Null
            $stub = $script:LiteStub.ToString()
            if (-not $UnlockResult) { $stub = $stub.Replace('return $true', 'return $false') }
            if ($WithTestCommand) { $stub += "`nfunction Test-LazaretteVaultUnlocked { return `$true }`n" }
            Set-Content -LiteralPath (Join-Path $lib 'Lazarette.ps1') -Value $stub -Encoding UTF8
        }
        if (-not (Test-Path -LiteralPath $script:HalyardModuleFile -PathType Leaf)) {
            throw "Halyard Lazarette bridge not found: $script:HalyardModuleFile"
        }
    }
    BeforeEach {
        . $script:HalyardModuleFile
        Remove-Module HalyardLazarette -Force -ErrorAction SilentlyContinue
        $env:HALYARD_LAZARETTE_ITEM_GitLab = 'gitlab'
        $env:HALYARD_LAZARETTE_ITEM_GitHub = 'github'
        [Environment]::SetEnvironmentVariable('GITLAB_TOKEN', $null, 'Process')
        [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', $null, 'Process')
    }
    AfterEach {
        Remove-Module HalyardLazarette -Force -ErrorAction SilentlyContinue
        Remove-Item Env:HALYARD_LAZARETTE_ITEM_GitLab -ErrorAction SilentlyContinue
        Remove-Item Env:HALYARD_LAZARETTE_ITEM_GitHub -ErrorAction SilentlyContinue
        Remove-Item Env:LAZARETTE_PATH -ErrorAction SilentlyContinue
        [Environment]::SetEnvironmentVariable('GITLAB_TOKEN', $null, 'Process')
        [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', $null, 'Process')
    }
    It 'loads GitLab and GitHub tokens from a Lite-shaped provider with no Test-LazaretteVaultUnlocked' {
        $liteRoot = Join-Path $TestDrive 'Lite'
        New-LiteFixture -Root $liteRoot
        $result = Connect-HalyardLazarette -LazaretteRoot $liteRoot -HalyardRoot $TestDrive
        $result.Unlocked | Should -Be $true
        $result.Error | Should -BeNullOrEmpty
        $env:GITLAB_TOKEN | Should -Be 'fake-gitlab-secret'
        $env:GITHUB_TOKEN | Should -Be 'fake-github-secret'
        $result.Loaded | Should -Contain 'GITLAB_TOKEN'
        $result.Loaded | Should -Contain 'GITHUB_TOKEN'
    }
    It 'still works against a vault-shaped provider that exposes Test-LazaretteVaultUnlocked' {
        $vaultRoot = Join-Path $TestDrive 'Vault'
        New-LiteFixture -Root $vaultRoot -WithTestCommand
        $result = Connect-HalyardLazarette -LazaretteRoot $vaultRoot -HalyardRoot $TestDrive
        $result.Unlocked | Should -Be $true
        $result.Error | Should -BeNullOrEmpty
        $env:GITLAB_TOKEN | Should -Be 'fake-gitlab-secret'
        $env:GITHUB_TOKEN | Should -Be 'fake-github-secret'
    }
    It 'reports LAZARETTE_AUTH_CANCELLED when the Lite unlock returns false' {
        $liteRoot = Join-Path $TestDrive 'LiteDenied'
        New-LiteFixture -Root $liteRoot -UnlockResult $false
        $result = Connect-HalyardLazarette -LazaretteRoot $liteRoot -HalyardRoot $TestDrive
        $result.Unlocked | Should -Be $false
        $result.Error | Should -Be 'LAZARETTE_AUTH_CANCELLED'
        $env:GITLAB_TOKEN | Should -BeNullOrEmpty
        $env:GITHUB_TOKEN | Should -BeNullOrEmpty
    }
}