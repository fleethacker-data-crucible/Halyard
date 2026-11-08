#requires -Version 5.1
# Pester 5 tests for Halyard on the desktop (module\Halyard\Desktop.ps1): app window, second-launch signal, window title,
# desktop shortcut and the launcher. Nothing opens a browser, a tray icon or a message box.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $script:Root 'module\Halyard\Halyard.psd1') -Force
}

AfterAll {
    Remove-Module Halyard -ErrorAction SilentlyContinue
}

Describe 'App window' {
    It 'opens Edge or Chrome with --app, or a tab when HALYARD_APP_WINDOW=0' {
        InModuleScope Halyard {
            Mock Get-HalyardAppBrowser { 'C:\Edge\msedge.exe' }
            Mock Start-Process { }
            $old = $env:HALYARD_APP_WINDOW
            try {
                $env:HALYARD_APP_WINDOW = $null
                Open-HalyardWindow -Url 'http://127.0.0.1:8806/?session=x' | Should -Be 'app'
                Should -Invoke Start-Process -Times 1 -ParameterFilter { $FilePath -eq 'C:\Edge\msedge.exe' -and $ArgumentList -contains '--app=http://127.0.0.1:8806/?session=x' }
                $env:HALYARD_APP_WINDOW = '0'
                Open-HalyardWindow -Url 'http://127.0.0.1:8806/' | Should -Be 'tab'
            } finally { $env:HALYARD_APP_WINDOW = $old }
        }
    }
    It 'falls back to a tab when no Edge or Chrome is installed' {
        InModuleScope Halyard {
            Mock Get-HalyardAppBrowser { $null }
            Mock Start-Process { }
            Open-HalyardWindow -Url 'http://127.0.0.1:8806/' | Should -Be 'tab'
        }
    }
    It 'recognises the titles the page sets and nothing else' {
        InModuleScope Halyard {
            $dot = [string][char]0xB7
            Test-HalyardWindowTitle 'Halyard' | Should -BeTrue
            Test-HalyardWindowTitle ('Proposals ' + $dot + ' Halyard') | Should -BeTrue
            Test-HalyardWindowTitle ('Overview ' + $dot + ' Halyard - Microsoft Edge') | Should -BeTrue
            Test-HalyardWindowTitle 'Halyard-Ballast issues - GitHub' | Should -BeFalse
            Test-HalyardWindowTitle 'Lazarette' | Should -BeFalse
        }
    }
}

Describe 'Second launch' {
    It 'a running engine hears the open signal; with none running it is not taken' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('halyard-sig-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            Send-HalyardOpenSignal -Root $tmp | Should -BeFalse
            $h = InModuleScope Halyard -Parameters @{ R = $tmp } { param($R) New-HalyardOpenSignal -Root $R }
            try {
                Send-HalyardOpenSignal -Root $tmp | Should -BeTrue
                $h.WaitOne(0) | Should -BeTrue
                $h.WaitOne(0) | Should -BeFalse   # auto-reset: one launch, one open
            } finally { $h.Dispose() }
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }
    It 'two install folders get different signals' {
        InModuleScope Halyard {
            $a = Get-HalyardOpenSignalName -Root $env:TEMP
            $b = Get-HalyardOpenSignalName -Root $env:SystemRoot
            $a | Should -Match '^Local\\Halyard-Open-[0-9a-f]{16}$'
            $a | Should -Not -Be $b
        }
    }
}

Describe 'Desktop shortcut' {
    It 'points at Launch-Halyard.bat with the icon, and is made only once' {
        InModuleScope Halyard -Parameters @{ Root = $script:Root } {
            param($Root)
            $tmp = Join-Path ([IO.Path]::GetTempPath()) ('halyard-lnk-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $tmp | Out-Null
            Mock Get-HalyardShortcutPath { Join-Path $tmp 'Halyard.lnk' }
            Mock Get-HalyardStateDir { Join-Path $tmp 'state' }
            try {
                $r = Initialize-HalyardShortcut -Root $Root
                $r.Ok | Should -BeTrue
                $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $tmp 'Halyard.lnk'))
                $lnk.TargetPath | Should -Be (Join-Path $Root 'Launch-Halyard.bat')
                $lnk.IconLocation | Should -BeLike '*favicon.ico,0'
                Remove-Item -LiteralPath (Join-Path $tmp 'Halyard.lnk')
                Initialize-HalyardShortcut -Root $Root | Should -BeNullOrEmpty   # deleted stays deleted
                Test-Path -LiteralPath (Join-Path $tmp 'Halyard.lnk') | Should -BeFalse
            } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
        }
    }
}

Describe 'Launcher' {
    It 'starts hidden unless --console is given' {
        $bat = Get-Content -LiteralPath (Join-Path $script:Root 'Launch-Halyard.bat') -Raw
        $bat | Should -Match 'start "" powershell\.exe [^\r\n]*-WindowStyle Hidden[^\r\n]*Start-Halyard\.ps1'
        $bat | Should -Match 'find /i "--console"'
    }
    It 'never prompts in a console it may not show' {
        $start = Get-Content -LiteralPath (Join-Path $script:Root 'scripts\Start-Halyard.ps1') -Raw
        $start | Should -Not -Match 'Read-Host'
        $start | Should -Match 'Send-HalyardOpenSignal'
    }
}
