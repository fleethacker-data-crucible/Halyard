#requires -Version 5.1
# Pester 5 tests for the PowerShell layer. Run: Invoke-Pester tests/pester
# Needs a built Halyard.Core.dll (dotnet build) or HALYARD_CORE_DLL pointing at one.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $script:Root 'module\Halyard\Halyard.psd1') -Force
    $script:Saved = @{}
    foreach ($n in @('HALYARD_PROVIDER', 'HALYARD_SOURCE_REPO', 'HALYARD_SANDBOX_REPO', 'GITHUB_TOKEN', 'GH_TOKEN', 'GITLAB_TOKEN', 'LLM_TOKEN', 'DATABRICKS_TOKEN', 'ARC_PATH', 'ARC_ROOT', 'ARC_HOME', 'LAZARETTE_PATH', 'LAZARETTE_ROOT', 'LAZARETTE_HOME', 'HALYARD_USE_LAZARETTE', 'CASCADE_USE_ARC', 'HALYARD_WRITE_MODE', 'HALYARD_ALLOW_PRODUCTION', 'GITLAB_PROJECT_ID',
               'CASCADE_PROVIDER', 'CASCADE_SOURCE_REPO', 'CASCADE_SANDBOX_REPO', 'CASCADE_WRITE_MODE', 'CASCADE_ARC_ITEM_GitLab', 'CASCADE_ARC_ITEM_Llm', 'CASCADE_ARC_ITEM_Databricks', 'CASCADE_ARC_ITEM_GitHub',
               'HALYARD_LAZARETTE_ITEM_GitLab', 'HALYARD_LAZARETTE_ITEM_Llm', 'HALYARD_LAZARETTE_ITEM_Databricks', 'HALYARD_LAZARETTE_ITEM_GitHub')) {
        $script:Saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process')
        [Environment]::SetEnvironmentVariable($n, $null, 'Process')
    }
}

AfterAll {
    foreach ($k in $script:Saved.Keys) { [Environment]::SetEnvironmentVariable($k, $script:Saved[$k], 'Process') }
    Remove-Module Halyard -ErrorAction SilentlyContinue
}

Describe 'Module surface' {
    It 'exports the documented commands' {
        $names = (Get-Command -Module Halyard).Name
        foreach ($n in @('Start-Halyard', 'Invoke-HalyardSeed', 'Invoke-HalyardTeardown', 'Connect-HalyardLazarette', 'New-HalyardSyntheticCatalog')) {
            $names | Should -Contain $n
        }
    }
    It 'keeps the former Connect-HalyardArc name as an alias' {
        (Get-Command Connect-HalyardArc).ResolvedCommand.Name | Should -Be 'Connect-HalyardLazarette'
    }
    It 'resolves the repository root' {
        Get-HalyardRoot | Should -Be $script:Root
    }
    It 'loads Halyard.Core' {
        { Import-HalyardCore } | Should -Not -Throw
        ('Halyard.Core.Facade' -as [type]) | Should -Not -BeNullOrEmpty
    }
}

Describe 'Initialize-HalyardEnvironment' {
    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ('halyard-pester-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $script:Tmp 'config') | Out-Null
        foreach ($n in @('HALYARD_PROVIDER', 'HALYARD_SOURCE_REPO', 'HALYARD_SANDBOX_REPO', 'GITHUB_TOKEN', 'HALYARD_WRITE_MODE', 'CASCADE_PROVIDER', 'CASCADE_SOURCE_REPO', 'CASCADE_SANDBOX_REPO', 'CASCADE_WRITE_MODE')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
    }
    AfterEach { Remove-Item $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue }

    It 'reads halyard.env without overriding variables already set' {
        Set-Content (Join-Path $script:Tmp 'config\halyard.env') "HALYARD_PROVIDER=github`nHALYARD_SANDBOX_REPO=owner/sandbox`nHALYARD_SOURCE_REPO=owner/source`nGITHUB_TOKEN=from-file"
        [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', 'from-env', 'Process')
        $r = Initialize-HalyardEnvironment -Root $script:Tmp -UseArc Never
        $r.Provider | Should -Be 'github'
        $r.Settings.Provider.SandboxRepository | Should -Be 'owner/sandbox'
        $r.Settings.Provider.SourceRepository | Should -Be 'owner/source'
        $r.Settings.Provider.Token | Should -Be 'from-env'
    }

    It 'still reads a former config\cascade.env with CASCADE_* names when there is no halyard.env' {
        Set-Content (Join-Path $script:Tmp 'config\cascade.env') "CASCADE_PROVIDER=github`nCASCADE_SANDBOX_REPO=sun-of-anton/cascade-sandbox`nCASCADE_SOURCE_REPO=owner/source"
        $r = Initialize-HalyardEnvironment -Root $script:Tmp -UseArc Never
        $r.EnvFile | Should -Be (Join-Path $script:Tmp 'config\cascade.env')
        $r.Settings.Provider.SandboxRepository | Should -Be 'sun-of-anton/Halyard-Ballast'
        $r.Settings.Provider.SourceRepository | Should -Be 'owner/source'
    }

    It 'prefers config\halyard.env and HALYARD_* over the former names' {
        Set-Content (Join-Path $script:Tmp 'config\cascade.env') "CASCADE_SANDBOX_REPO=owner/old"
        Set-Content (Join-Path $script:Tmp 'config\halyard.env') "HALYARD_SANDBOX_REPO=owner/new`nCASCADE_SOURCE_REPO=owner/source"
        $r = Initialize-HalyardEnvironment -Root $script:Tmp -UseArc Never
        $r.EnvFile | Should -Be (Join-Path $script:Tmp 'config\halyard.env')
        $r.Settings.Provider.SandboxRepository | Should -Be 'owner/new'
        $r.Settings.Provider.SourceRepository | Should -Be 'owner/source'
    }

    It 'builds a sandbox-only write policy that protects the source' {
        Set-Content (Join-Path $script:Tmp 'config\halyard.env') "HALYARD_PROVIDER=gitlab`nGITLAB_URL=https://gitlab.example.com`nHALYARD_SOURCE_REPO=1234`nHALYARD_SANDBOX_REPO=5678"
        $s = (Initialize-HalyardEnvironment -Root $script:Tmp -UseArc Never).Settings
        $policy = $s.BuildWritePolicy()
        $policy.Mode.ToString() | Should -Be 'Sandbox'
        $source = [Halyard.Core.Model.RepositoryRef]::new([Halyard.Core.Model.ProviderKind]::GitLab, '1234')
        $intent = [Halyard.Core.Safety.WriteIntent]::new('test', 'pester', $null)
        { $policy.Assert($source, $intent) } | Should -Throw '*WRITE BLOCKED*'
    }
}

Describe 'Finding Lazarette' {
    BeforeAll {
        $script:Base = Join-Path ([IO.Path]::GetTempPath()) ('find-lazarette-' + [guid]::NewGuid().ToString('N'))
        foreach ($d in @('new\lib', 'old\lib')) { New-Item -ItemType Directory -Force (Join-Path $script:Base $d) | Out-Null }
        Set-Content (Join-Path $script:Base 'new\lib\Lazarette.ps1') '# stand-in'
        Set-Content (Join-Path $script:Base 'old\lib\Arc.ps1') '# stand-in'
    }
    AfterAll { Remove-Item $script:Base -Recurse -Force -ErrorAction SilentlyContinue }
    AfterEach { foreach ($n in @('LAZARETTE_PATH', 'ARC_PATH')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') } }

    It 'prefers LAZARETTE_PATH over ARC_PATH' {
        $env:LAZARETTE_PATH = Join-Path $script:Base 'new'; $env:ARC_PATH = Join-Path $script:Base 'old'
        InModuleScope Halyard { Find-HalyardLazaretteRoot } | Should -Be ([IO.Path]::GetFullPath((Join-Path $script:Base 'new')))
    }
    It 'falls back to ARC_PATH and accepts lib\Arc.ps1' {
        $env:ARC_PATH = Join-Path $script:Base 'old'
        InModuleScope Halyard { Find-HalyardLazaretteRoot } | Should -Be ([IO.Path]::GetFullPath((Join-Path $script:Base 'old')))
    }
    It 'skips a configured folder that holds no Lazarette loader' {
        $env:LAZARETTE_PATH = Join-Path $script:Base 'missing'; $env:ARC_PATH = Join-Path $script:Base 'old'
        InModuleScope Halyard { Find-HalyardLazaretteRoot } | Should -Be ([IO.Path]::GetFullPath((Join-Path $script:Base 'old')))
    }
    It 'probes a sibling Lazarette folder before a sibling arc folder' {
        $sib = Join-Path $script:Base 'sib'
        foreach ($d in @('Lazarette\lib', 'arc\lib', 'Halyard')) { New-Item -ItemType Directory -Force (Join-Path $sib $d) | Out-Null }
        Set-Content (Join-Path $sib 'Lazarette\lib\Lazarette.ps1') '# stand-in'
        Set-Content (Join-Path $sib 'arc\lib\Arc.ps1') '# stand-in'
        $found = InModuleScope Halyard -Parameters @{ R = (Join-Path $sib 'Halyard') } { param($R) Find-HalyardLazaretteRoot -HalyardRoot $R }
        $found | Should -Be ([IO.Path]::GetFullPath((Join-Path $sib 'Lazarette')))
    }
}

Describe 'Connect-HalyardLazarette with a stand-in Lazarette (lib\Arc.ps1)' {
    BeforeAll {
        $script:FakeLaz = Join-Path ([IO.Path]::GetTempPath()) ('fake-lazarette-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $script:FakeLaz 'lib') | Out-Null
        # Minimal stand-in for the ARC 5 integration API (docs/INTEGRATION.md): unlock, test, Get-ArcSecret -Service.
        Set-Content (Join-Path $script:FakeLaz 'lib\Arc.ps1') @'
$script:Open = $false
$script:UnlockCalls = 0
function Unlock-ArcVaultInteractive { $script:UnlockCalls++; $script:Open = $true; return $true }
function Test-ArcVaultUnlocked { return $script:Open }
function Get-ArcSecret { param([Parameter(Mandatory)][string]$Service, [string]$User = '', [string]$Field = '') switch ($Service) { 'lib-gitlab' { 'glpat-lib' } default { throw "no item $Service" } } }
'@
    }
    AfterAll {
        Remove-Module HalyardLazarette -ErrorAction SilentlyContinue
        InModuleScope Halyard { $script:HalyardLazaretteModule = $null; $script:HalyardLazaretteRoot = $null; $script:HalyardLazaretteKind = $null }
        Remove-Item $script:FakeLaz -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'unlocks through the ARC 5 API and reads items named by the former CASCADE_ARC_ITEM_* variables' {
        foreach ($n in @('GITLAB_TOKEN', 'LLM_TOKEN')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
        $env:CASCADE_ARC_ITEM_GitLab = 'lib-gitlab'; $env:CASCADE_ARC_ITEM_Llm = 'lib-missing'
        try {
            $r = Connect-HalyardLazarette -LazaretteRoot $script:FakeLaz
            $r.Unlocked | Should -BeTrue
            $r.Loaded | Should -Contain 'GITLAB_TOKEN'
            $r.Missing | Should -Contain 'lib-missing'
            $env:GITLAB_TOKEN | Should -Be 'glpat-lib'
            (& (Get-Module HalyardLazarette) { $script:UnlockCalls }) | Should -Be 1
        } finally {
            foreach ($n in @('CASCADE_ARC_ITEM_GitLab', 'CASCADE_ARC_ITEM_Llm', 'GITLAB_TOKEN')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
        }
    }
}

Describe 'Connect-HalyardLazarette with a stand-in ARC 3/4 (pm.ps1)' {
    BeforeAll {
        $script:FakeArc = Join-Path ([IO.Path]::GetTempPath()) ('fake-arc-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $script:FakeArc | Out-Null
        # Minimal stand-in for ARC's pm.ps1: same function names, in-memory vault, no CAC.
        Set-Content (Join-Path $script:FakeArc 'pm.ps1') @'
$script:PmState = @{ VaultUnlocked = $false }
$script:UnlockCalls = 0
function Ensure-VaultUnlocked { $script:UnlockCalls++; $script:PmState.VaultUnlocked = $true; return $true }
function Get-PmStarkApiKey { param([string]$VaultName) switch ($VaultName) { 'my-gitlab' { 'glpat-fake-gitlab' } 'my-llm' { 'sk-fake-llm' } default { $null } } }
function Unprotect-SecretText { param($Protected) $Protected }
function Find-VaultRows { param($Name) @() }
'@
    }
    AfterAll { Remove-Item $script:FakeArc -Recurse -Force -ErrorAction SilentlyContinue; Remove-Module HalyardLazarette -ErrorAction SilentlyContinue }

    It 'unlocks once and exports mapped items to the process environment' {
        foreach ($n in @('GITLAB_TOKEN', 'LLM_TOKEN', 'DATABRICKS_TOKEN', 'GITHUB_TOKEN')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
        $env:HALYARD_LAZARETTE_ITEM_GitLab = 'my-gitlab'; $env:HALYARD_LAZARETTE_ITEM_Llm = 'my-llm'; $env:HALYARD_LAZARETTE_ITEM_Databricks = 'my-databricks'
        $r = Connect-HalyardArc -ArcRoot $script:FakeArc   # former command and parameter names still work
        $r.Unlocked | Should -BeTrue
        $r.Loaded | Should -Contain 'GITLAB_TOKEN'
        $r.Loaded | Should -Contain 'LLM_TOKEN'
        $r.Missing | Should -Contain 'my-databricks'
        $env:GITLAB_TOKEN | Should -Be 'glpat-fake-gitlab'
    }

    It 'does not reload Lazarette or prompt again once unlocked' {
        $again = Connect-HalyardLazarette -LazaretteRoot $script:FakeArc
        $again.Unlocked | Should -BeTrue
        $calls = & (Get-Module HalyardLazarette) { $script:UnlockCalls }
        $calls | Should -Be 1
    }

    It 'keeps the live session instead of loading a second copy' {
        # Re-importing pm.ps1 would reset ARC 3/4's in-memory CAC session, so a later call reuses the loaded one.
        $r = Connect-HalyardLazarette -LazaretteRoot (Join-Path $script:FakeArc 'elsewhere')
        $r.Unlocked | Should -BeTrue
        @(Get-Module HalyardLazarette).Count | Should -Be 1
    }

    It 'finds an ARC 3/4 install from ARC_PATH' {
        $env:ARC_PATH = $script:FakeArc
        try { InModuleScope Halyard { Find-HalyardLazaretteRoot } | Should -Be ([IO.Path]::GetFullPath($script:FakeArc)) }
        finally { $env:ARC_PATH = $null }
    }
}

Describe 'Synthetic catalogue' {
    It 'writes the same catalogue as the committed fixture' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('cat-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            New-HalyardSyntheticCatalog -Path $tmp -Total 100 | Out-Null
            $a = Get-Content $tmp -Raw | ConvertFrom-Json
            $b = Get-Content (Join-Path $script:Root 'fixtures\synthetic-work-items.json') -Raw | ConvertFrom-Json
            $a.items.Count | Should -Be 100
            ($a.items | ForEach-Object id) -join ',' | Should -Be (($b.items | ForEach-Object id) -join ',')
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}

Describe 'Repository rules' {
    It 'PowerShell code never calls REST endpoints directly (all writes go through Halyard.Core)' {
        $files = Get-ChildItem (Join-Path $script:Root 'module'), (Join-Path $script:Root 'scripts') -Recurse -Include *.ps1, *.psm1
        $hits = $files | Select-String -Pattern 'Invoke-RestMethod|Invoke-WebRequest|Net\.WebClient|HttpClient' -List
        $hits | Should -BeNullOrEmpty
    }
    It 'only provider adapters issue mutating HTTP verbs in the core' {
        $core = Join-Path $script:Root 'src\Halyard.Core'
        $hits = Get-ChildItem $core -Recurse -Filter *.cs |
            Where-Object { $_.FullName -notmatch '[\\/](Providers|Llm|bin|obj)[\\/]' } |
            Select-String -Pattern '(HttpCall|SendJsonAsync|NewCall)\(\s*"(POST|PUT|PATCH|DELETE)"' -CaseSensitive
        $hits | Should -BeNullOrEmpty
    }
    It 'only the write gateway calls OpenWriter' {
        $core = Join-Path $script:Root 'src\Halyard.Core'
        $hits = Get-ChildItem $core -Recurse -Filter *.cs |
            Where-Object { $_.FullName -notmatch '[\\/](Providers|bin|obj)[\\/]' -and $_.Name -ne 'WriteGateway.cs' } |
            Select-String -Pattern '\.OpenWriter\('
        $hits | Should -BeNullOrEmpty
    }
    It 'providers never implement the writer interface themselves (no cast around the gateway)' {
        $hits = Get-ChildItem (Join-Path $script:Root 'src\Halyard.Core\Providers') -Filter '*Provider.cs' |
            Select-String -Pattern 'class\s+\w+Provider\s*:[^{]*IWorkItemWriter'
        $hits | Should -BeNullOrEmpty
    }
    It 'scripts parse under the current PowerShell' {
        $files = Get-ChildItem (Join-Path $script:Root 'module'), (Join-Path $script:Root 'scripts'), (Join-Path $script:Root 'build') -Recurse -Include *.ps1, *.psm1, *.psd1
        foreach ($f in $files) {
            $tokens = $null; $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
            $errors | Should -BeNullOrEmpty -Because $f.Name
        }
    }
    It 'scripts avoid syntax that Windows PowerShell 5.1 cannot run' {
        $files = Get-ChildItem (Join-Path $script:Root 'module'), (Join-Path $script:Root 'scripts'), (Join-Path $script:Root 'build') -Recurse -Include *.ps1, *.psm1
        foreach ($f in $files) {
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$null)
            $pipelineChain = $ast.FindAll({ param($n) $n.GetType().Name -in @('PipelineChainAst', 'TernaryExpressionAst') }, $true)
            $pipelineChain | Should -BeNullOrEmpty -Because ($f.Name + ' uses && || or ?: which need PowerShell 7')
            $nullOps = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.BinaryExpressionAst] -and $n.Operator.ToString() -eq 'QuestionQuestion' }, $true)
            $nullOps | Should -BeNullOrEmpty -Because ($f.Name + ' uses ?? which needs PowerShell 7')
        }
    }
}

Describe 'Process helpers (Halyard-Process.ps1)' {
    BeforeAll { . (Join-Path $PSScriptRoot '..\..\scripts\Halyard-Process.ps1') }

    It 'finds no engines in an empty root and removes stale runtime files' {
        $root = Join-Path ([IO.Path]::GetTempPath()) ('halyard-proc-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $root 'logs') | Out-Null
        Set-Content -Path (Join-Path $root 'logs\halyard-server-999999.json') -Value '{"pid":999999,"port":1,"url":"http://127.0.0.1:1/"}'
        @(Get-HalyardProcesses -Root $root).Count | Should -Be 0
        Test-Path (Join-Path $root 'logs\halyard-server-999999.json') | Should -BeFalse
        Remove-Item $root -Recurse -Force
    }

    It 'sees this process as a holder of a file it keeps open (Restart Manager)' -Skip:(-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
        $file = Join-Path ([IO.Path]::GetTempPath()) ('halyard-hold-' + [guid]::NewGuid().ToString('N') + '.dll')
        Set-Content -Path $file -Value 'x'
        $stream = [IO.File]::Open($file, 'Open', 'Read', 'None')
        try {
            $holders = @(Get-FileHolders -Path $file)
            ($holders | ForEach-Object { $_.Pid }) | Should -Contain $PID
        } finally { $stream.Dispose(); Remove-Item $file -Force }
    }
}
