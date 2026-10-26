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
    It 'accepts the former -UseArc / -NoArc / -Arc parameter names for one release' {
        (Get-Command Initialize-HalyardEnvironment).Parameters['UseLazarette'].Aliases | Should -Contain 'UseArc'
        (Get-Command (Join-Path $script:Root 'scripts\Start-Halyard.ps1')).Parameters['NoLazarette'].Aliases | Should -Contain 'NoArc'
        (Get-Command (Join-Path $script:Root 'scripts\Start-Halyard.ps1')).Parameters['Lazarette'].Aliases | Should -Contain 'Arc'
        (Get-Command (Join-Path $script:Root 'scripts\Invoke-SandboxTeardown.ps1')).Parameters['NoLazarette'].Aliases | Should -Contain 'NoArc'
    }
    It 'no longer exports the former Connect-HalyardArc name' {
        Get-Command Connect-HalyardArc -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
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
        $r = Initialize-HalyardEnvironment -Root $script:Tmp -UseLazarette Never
        $r.Provider | Should -Be 'github'
        $r.Settings.Provider.SandboxRepository | Should -Be 'owner/sandbox'
        $r.Settings.Provider.SourceRepository | Should -Be 'owner/source'
        $r.Settings.Provider.Token | Should -Be 'from-env'
    }

    It 'copies a former config\cascade.env forward to config\halyard.env once and leaves the original' {
        $legacy = Join-Path $script:Tmp 'config\cascade.env'
        Set-Content $legacy "CASCADE_PROVIDER=github`nCASCADE_SANDBOX_REPO=sun-of-anton/cascade-sandbox`nCASCADE_SOURCE_REPO=owner/source"
        $before = Get-Content $legacy -Raw
        $r = Initialize-HalyardEnvironment -Root $script:Tmp -UseLazarette Never
        $r.EnvFile | Should -Be (Join-Path $script:Tmp 'config\halyard.env')
        Test-Path (Join-Path $script:Tmp 'config\halyard.env') | Should -BeTrue
        (Get-Content $legacy -Raw) | Should -Be $before
        (Get-Content (Join-Path $script:Tmp 'config\halyard.env')) | Should -Contain 'HALYARD_SANDBOX_REPO=fleethacker-data-crucible/Halyard-Ballast'
        $r.Settings.Provider.SandboxRepository | Should -Be 'fleethacker-data-crucible/Halyard-Ballast'
        $r.Settings.Provider.SourceRepository | Should -Be 'owner/source'
    }

    It 'ignores config\cascade.env once config\halyard.env exists, and still reads CASCADE_* variables for one release' {
        Set-Content (Join-Path $script:Tmp 'config\cascade.env') "CASCADE_SANDBOX_REPO=owner/old"
        Set-Content (Join-Path $script:Tmp 'config\halyard.env') "HALYARD_SANDBOX_REPO=owner/new`nCASCADE_SOURCE_REPO=owner/source"
        $r = Initialize-HalyardEnvironment -Root $script:Tmp -UseLazarette Never
        $r.EnvFile | Should -Be (Join-Path $script:Tmp 'config\halyard.env')
        $r.Settings.Provider.SandboxRepository | Should -Be 'owner/new'
        $r.Settings.Provider.SourceRepository | Should -Be 'owner/source'
    }

    It 'builds a sandbox-only write policy that protects the source' {
        Set-Content (Join-Path $script:Tmp 'config\halyard.env') "HALYARD_PROVIDER=gitlab`nGITLAB_URL=https://gitlab.example.com`nHALYARD_SOURCE_REPO=1234`nHALYARD_SANDBOX_REPO=5678"
        $s = (Initialize-HalyardEnvironment -Root $script:Tmp -UseLazarette Never).Settings
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
    # Runs in a child PowerShell so its global Arc-named stand-ins never meet the Lazarette-named stand-in below
    # (a real process only ever loads one Lazarette).
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
        $script:Child = @'
param([string]$Manifest, [string]$LazRoot)
$ErrorActionPreference = 'Stop'
foreach ($n in @('GITLAB_TOKEN', 'LLM_TOKEN', 'DATABRICKS_TOKEN', 'GITHUB_TOKEN')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
$env:CASCADE_ARC_ITEM_GitLab = 'lib-gitlab'; $env:CASCADE_ARC_ITEM_Llm = 'lib-missing'
Import-Module $Manifest -Force
$r = Connect-HalyardLazarette -LazaretteRoot $LazRoot 6>$null
$calls = & (Get-Module HalyardLazarette) { $script:UnlockCalls }
[pscustomobject]@{ Unlocked = $r.Unlocked; Error = $r.Error; Loaded = @($r.Loaded); Missing = @($r.Missing); Token = $env:GITLAB_TOKEN; Calls = $calls } | ConvertTo-Json -Compress
'@
        $script:ChildFile = Join-Path ([IO.Path]::GetTempPath()) ('halyard-lazarette-child-' + [guid]::NewGuid().ToString('N') + '.ps1')
        Set-Content -Path $script:ChildFile -Value $script:Child
    }
    AfterAll { Remove-Item $script:FakeLaz -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item $script:ChildFile -Force -ErrorAction SilentlyContinue }

    It 'unlocks through the ARC 5 API and reads items named by the former CASCADE_ARC_ITEM_* variables' {
        $exe = (Get-Process -Id $PID).Path
        $out = & $exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script:ChildFile -Manifest (Join-Path $script:Root 'module\Halyard\Halyard.psd1') -LazRoot $script:FakeLaz
        $json = @($out) | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
        $json | Should -Not -BeNullOrEmpty -Because (@($out) -join "`n")
        $r = $json | ConvertFrom-Json
        $r.Error | Should -BeNullOrEmpty
        $r.Unlocked | Should -BeTrue
        $r.Loaded | Should -Contain 'GITLAB_TOKEN'
        $r.Missing | Should -Contain 'lib-missing'
        $r.Token | Should -Be 'glpat-lib'
        $r.Calls | Should -Be 1
    }
}

Describe 'Connect-HalyardLazarette with a stand-in Lazarette (lib\Lazarette.ps1, Lazarette-named API)' {
    BeforeAll {
        $script:FakeLaz2 = Join-Path ([IO.Path]::GetTempPath()) ('fake-lazarette2-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $script:FakeLaz2 'lib') | Out-Null
        # Lazarette-named functions next to Arc-named ones: the Lazarette names must win.
        Set-Content (Join-Path $script:FakeLaz2 'lib\Lazarette.ps1') @'
$script:Open = $false
$script:UnlockCalls = 0
function Unlock-LazaretteVaultInteractive { $script:UnlockCalls++; $script:Open = $true; return $true }
function Test-LazaretteVaultUnlocked { return $script:Open }
function Get-LazaretteSecret { param([Parameter(Mandatory)][string]$Service) switch ($Service) { 'my-gitlab' { 'glpat-fake-gitlab' } 'my-llm' { 'sk-fake-llm' } default { throw "no item $Service" } } }
function Unlock-ArcVaultInteractive { throw 'the Arc name must not be used when the Lazarette name exists' }
function Get-ArcSecret { param([string]$Service) throw 'the Arc name must not be used when the Lazarette name exists' }
'@
    }
    AfterAll { Remove-Item $script:FakeLaz2 -Recurse -Force -ErrorAction SilentlyContinue; Remove-Module HalyardLazarette -ErrorAction SilentlyContinue }

    It 'unlocks once and exports mapped items to the process environment' {
        foreach ($n in @('GITLAB_TOKEN', 'LLM_TOKEN', 'DATABRICKS_TOKEN', 'GITHUB_TOKEN')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
        $env:HALYARD_LAZARETTE_ITEM_GitLab = 'my-gitlab'; $env:HALYARD_LAZARETTE_ITEM_Llm = 'my-llm'; $env:HALYARD_LAZARETTE_ITEM_Databricks = 'my-databricks'
        $r = Connect-HalyardLazarette -LazaretteRoot $script:FakeLaz2
        $r.Unlocked | Should -BeTrue -Because ('result: ' + ($r | ConvertTo-Json -Compress))
        $r.Loaded | Should -Contain 'GITLAB_TOKEN'
        $r.Loaded | Should -Contain 'LLM_TOKEN'
        $r.Missing | Should -Contain 'my-databricks'
        $env:GITLAB_TOKEN | Should -Be 'glpat-fake-gitlab'
    }

    It 'does not reload Lazarette or prompt again once unlocked' {
        $again = Connect-HalyardLazarette -LazaretteRoot $script:FakeLaz2
        $again.Unlocked | Should -BeTrue
        $calls = & (Get-Module HalyardLazarette) { $script:UnlockCalls }
        $calls | Should -Be 1
    }

    It 'keeps the live session instead of loading a second copy' {
        $r = Connect-HalyardLazarette -LazaretteRoot (Join-Path $script:FakeLaz2 'elsewhere')
        $r.Unlocked | Should -BeTrue
        @(Get-Module HalyardLazarette).Count | Should -Be 1
    }

    It 'reports ARC_NOT_FOUND (Lazarette result code) when no install is found' {
        $saved = @{}; foreach ($n in @('LAZARETTE_PATH', 'LAZARETTE_ROOT', 'LAZARETTE_HOME', 'ARC_PATH', 'ARC_ROOT', 'ARC_HOME', 'USERPROFILE', 'LOCALAPPDATA')) { $saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process'); [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
        try {
            $r = InModuleScope Halyard -Parameters @{ R = (Join-Path ([IO.Path]::GetTempPath()) ('nowhere-' + [guid]::NewGuid().ToString('N') + '\Halyard')) } { param($R) Find-HalyardLazaretteRoot -HalyardRoot $R }
            $r | Should -BeNullOrEmpty
        } finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') } }
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

    It 'sees this process as a holder of a file it keeps open (FileProcessIdsUsingFileInformation)' -Skip:(-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
        $file = Join-Path ([IO.Path]::GetTempPath()) ('halyard-hold-' + [guid]::NewGuid().ToString('N') + '.dll')
        Set-Content -Path $file -Value 'x'
        $stream = [IO.File]::Open($file, 'Open', 'Read', 'None')
        try {
            $holders = @(Get-FileHolders -Path $file)
            ($holders | ForEach-Object { $_.Pid }) | Should -Contain $PID
        } finally { $stream.Dispose(); Remove-Item $file -Force }
    }
}
