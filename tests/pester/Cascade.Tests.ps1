#requires -Version 5.1
# Pester 5 tests for the PowerShell layer. Run: Invoke-Pester tests/pester
# Needs a built Cascade.Core.dll (dotnet build) or CASCADE_CORE_DLL pointing at one.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $script:Root 'module\Cascade\Cascade.psd1') -Force
    $script:Saved = @{}
    foreach ($n in @('CASCADE_PROVIDER', 'CASCADE_SOURCE_REPO', 'CASCADE_SANDBOX_REPO', 'GITHUB_TOKEN', 'GH_TOKEN', 'GITLAB_TOKEN', 'LLM_TOKEN', 'DATABRICKS_TOKEN', 'ARC_PATH', 'CASCADE_USE_ARC', 'CASCADE_WRITE_MODE', 'CASCADE_ALLOW_PRODUCTION', 'GITLAB_PROJECT_ID')) {
        $script:Saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process')
        [Environment]::SetEnvironmentVariable($n, $null, 'Process')
    }
}

AfterAll {
    foreach ($k in $script:Saved.Keys) { [Environment]::SetEnvironmentVariable($k, $script:Saved[$k], 'Process') }
    Remove-Module Cascade -ErrorAction SilentlyContinue
}

Describe 'Module surface' {
    It 'exports the documented commands' {
        $names = (Get-Command -Module Cascade).Name
        foreach ($n in @('Start-Cascade', 'Invoke-CascadeSeed', 'Invoke-CascadeTeardown', 'Connect-CascadeArc', 'New-CascadeSyntheticCatalog')) {
            $names | Should -Contain $n
        }
    }
    It 'resolves the repository root' {
        Get-CascadeRoot | Should -Be $script:Root
    }
    It 'loads Cascade.Core' {
        { Import-CascadeCore } | Should -Not -Throw
        ('Cascade.Core.Facade' -as [type]) | Should -Not -BeNullOrEmpty
    }
}

Describe 'Initialize-CascadeEnvironment' {
    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ('cascade-pester-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $script:Tmp 'config') | Out-Null
        foreach ($n in @('CASCADE_PROVIDER', 'CASCADE_SOURCE_REPO', 'CASCADE_SANDBOX_REPO', 'GITHUB_TOKEN', 'CASCADE_WRITE_MODE')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
    }
    AfterEach { Remove-Item $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue }

    It 'reads cascade.env without overriding variables already set' {
        Set-Content (Join-Path $script:Tmp 'config\cascade.env') "CASCADE_PROVIDER=github`nCASCADE_SANDBOX_REPO=owner/sandbox`nCASCADE_SOURCE_REPO=owner/source`nGITHUB_TOKEN=from-file"
        [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', 'from-env', 'Process')
        $r = Initialize-CascadeEnvironment -Root $script:Tmp -UseArc Never
        $r.Provider | Should -Be 'github'
        $r.Settings.Provider.SandboxRepository | Should -Be 'owner/sandbox'
        $r.Settings.Provider.SourceRepository | Should -Be 'owner/source'
        $r.Settings.Provider.Token | Should -Be 'from-env'
    }

    It 'builds a sandbox-only write policy that protects the source' {
        Set-Content (Join-Path $script:Tmp 'config\cascade.env') "CASCADE_PROVIDER=gitlab`nGITLAB_URL=https://gitlab.example.com`nCASCADE_SOURCE_REPO=1234`nCASCADE_SANDBOX_REPO=5678"
        $s = (Initialize-CascadeEnvironment -Root $script:Tmp -UseArc Never).Settings
        $policy = $s.BuildWritePolicy()
        $policy.Mode.ToString() | Should -Be 'Sandbox'
        $source = [Cascade.Core.Model.RepositoryRef]::new([Cascade.Core.Model.ProviderKind]::GitLab, '1234')
        $intent = [Cascade.Core.Safety.WriteIntent]::new('test', 'pester', $null)
        { $policy.Assert($source, $intent) } | Should -Throw '*WRITE BLOCKED*'
    }
}

Describe 'Connect-CascadeArc with a stand-in ARC' {
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
    AfterAll { Remove-Item $script:FakeArc -Recurse -Force -ErrorAction SilentlyContinue; Remove-Module CascadeArc -ErrorAction SilentlyContinue }

    It 'unlocks once and exports mapped items to the process environment' {
        foreach ($n in @('GITLAB_TOKEN', 'LLM_TOKEN', 'DATABRICKS_TOKEN', 'GITHUB_TOKEN')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
        $env:CASCADE_ARC_ITEM_GitLab = 'my-gitlab'; $env:CASCADE_ARC_ITEM_Llm = 'my-llm'; $env:CASCADE_ARC_ITEM_Databricks = 'my-databricks'
        $r = Connect-CascadeArc -ArcRoot $script:FakeArc
        $r.Unlocked | Should -BeTrue
        $r.Loaded | Should -Contain 'GITLAB_TOKEN'
        $r.Loaded | Should -Contain 'LLM_TOKEN'
        $r.Missing | Should -Contain 'my-databricks'
        $env:GITLAB_TOKEN | Should -Be 'glpat-fake-gitlab'
    }

    It 'does not reload ARC or prompt again once unlocked' {
        $again = Connect-CascadeArc -ArcRoot $script:FakeArc
        $again.Unlocked | Should -BeTrue
        $calls = & (Get-Module CascadeArc) { $script:UnlockCalls }
        $calls | Should -Be 1
    }

    It 'keeps the live ARC session instead of loading a second copy' {
        # Re-importing pm.ps1 would reset ARC's in-memory CAC session, so a later call reuses the loaded one.
        $r = Connect-CascadeArc -ArcRoot (Join-Path $script:FakeArc 'elsewhere')
        $r.Unlocked | Should -BeTrue
        @(Get-Module CascadeArc).Count | Should -Be 1
    }

    It 'finds ARC from ARC_PATH' {
        $env:ARC_PATH = $script:FakeArc
        try { InModuleScope Cascade { Find-CascadeArcRoot } | Should -Be ([IO.Path]::GetFullPath($script:FakeArc)) }
        finally { $env:ARC_PATH = $null }
    }
}

Describe 'Synthetic catalogue' {
    It 'writes the same catalogue as the committed fixture' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('cat-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            New-CascadeSyntheticCatalog -Path $tmp -Total 100 | Out-Null
            $a = Get-Content $tmp -Raw | ConvertFrom-Json
            $b = Get-Content (Join-Path $script:Root 'fixtures\synthetic-work-items.json') -Raw | ConvertFrom-Json
            $a.items.Count | Should -Be 100
            ($a.items | ForEach-Object id) -join ',' | Should -Be (($b.items | ForEach-Object id) -join ',')
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}

Describe 'Repository rules' {
    It 'PowerShell code never calls REST endpoints directly (all writes go through Cascade.Core)' {
        $files = Get-ChildItem (Join-Path $script:Root 'module'), (Join-Path $script:Root 'scripts') -Recurse -Include *.ps1, *.psm1
        $hits = $files | Select-String -Pattern 'Invoke-RestMethod|Invoke-WebRequest|Net\.WebClient|HttpClient' -List
        $hits | Should -BeNullOrEmpty
    }
    It 'only provider adapters issue mutating HTTP verbs in the core' {
        $core = Join-Path $script:Root 'src\Cascade.Core'
        $hits = Get-ChildItem $core -Recurse -Filter *.cs |
            Where-Object { $_.FullName -notmatch '[\\/](Providers|Llm|bin|obj)[\\/]' } |
            Select-String -Pattern '(HttpCall|SendJsonAsync|NewCall)\(\s*"(POST|PUT|PATCH|DELETE)"' -CaseSensitive
        $hits | Should -BeNullOrEmpty
    }
    It 'only the write gateway calls OpenWriter' {
        $core = Join-Path $script:Root 'src\Cascade.Core'
        $hits = Get-ChildItem $core -Recurse -Filter *.cs |
            Where-Object { $_.FullName -notmatch '[\\/](Providers|bin|obj)[\\/]' -and $_.Name -ne 'WriteGateway.cs' } |
            Select-String -Pattern '\.OpenWriter\('
        $hits | Should -BeNullOrEmpty
    }
    It 'providers never implement the writer interface themselves (no cast around the gateway)' {
        $hits = Get-ChildItem (Join-Path $script:Root 'src\Cascade.Core\Providers') -Filter '*Provider.cs' |
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

Describe 'Process helpers (Cascade-Process.ps1)' {
    BeforeAll { . (Join-Path $PSScriptRoot '..\..\scripts\Cascade-Process.ps1') }

    It 'finds no engines in an empty root and removes stale runtime files' {
        $root = Join-Path ([IO.Path]::GetTempPath()) ('cascade-proc-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $root 'logs') | Out-Null
        Set-Content -Path (Join-Path $root 'logs\cascade-server-999999.json') -Value '{"pid":999999,"port":1,"url":"http://127.0.0.1:1/"}'
        @(Get-CascadeProcesses -Root $root).Count | Should -Be 0
        Test-Path (Join-Path $root 'logs\cascade-server-999999.json') | Should -BeFalse
        Remove-Item $root -Recurse -Force
    }

    It 'sees this process as a holder of a file it keeps open (Restart Manager)' -Skip:(-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
        $file = Join-Path ([IO.Path]::GetTempPath()) ('cascade-hold-' + [guid]::NewGuid().ToString('N') + '.dll')
        Set-Content -Path $file -Value 'x'
        $stream = [IO.File]::Open($file, 'Open', 'Read', 'None')
        try {
            $holders = @(Get-FileHolders -Path $file)
            ($holders | ForEach-Object { $_.Pid }) | Should -Contain $PID
        } finally { $stream.Dispose(); Remove-Item $file -Force }
    }
}
