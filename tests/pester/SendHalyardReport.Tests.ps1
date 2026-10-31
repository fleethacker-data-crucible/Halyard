#requires -Version 5.1
# Pester 5 tests for report delivery: Send-HalyardReportMessage (Outlook COM / SMTP / file) and scripts\Send-HalyardReport.ps1.
# Nothing here reaches Outlook or a mail server: New-Object and Send-MailMessage are mocked inside the module, and the
# script tests only use paths that fail before any delivery (or deliver to a file).

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $script:Root 'module\Halyard\Halyard.psd1') -Force
    $script:Script = Join-Path $script:Root 'scripts\Send-HalyardReport.ps1'
}

AfterAll {
    Remove-Module Halyard -ErrorAction SilentlyContinue
}

Describe 'Send-HalyardReportMessage' {
    BeforeEach {
        $script:mail = [pscustomobject]@{ To = $null; Subject = $null; HTMLBody = $null; Sent = 0 }
        $script:mail | Add-Member -MemberType ScriptMethod -Name Send -Value { $this.Sent++ }
        $script:outlook = [pscustomobject]@{ ItemType = $null }
        $script:outlook | Add-Member -MemberType ScriptMethod -Name CreateItem -Value { param($type) $this.ItemType = $type; return $script:mail }
        Mock New-Object -ModuleName Halyard -ParameterFilter { $ComObject -eq 'Outlook.Application' } -MockWith { $script:outlook }
        Mock Send-MailMessage -ModuleName Halyard -MockWith { }
    }

    It 'is exported by the module' {
        (Get-Command -Module Halyard).Name | Should -Contain 'Send-HalyardReportMessage'
    }

    Context 'Outlook' {
        It 'creates a mail item (type 0), joins recipients with semicolons, sets the HTML body and sends once' {
            $r = Send-HalyardReportMessage -Via Outlook -Html '<p>report</p>' -Subject 'Weekly' -To 'a@example.org', 'b@example.org'
            $script:outlook.ItemType | Should -Be 0
            $script:mail.To | Should -Be 'a@example.org;b@example.org'
            $script:mail.Subject | Should -Be 'Weekly'
            $script:mail.HTMLBody | Should -Be '<p>report</p>'
            $script:mail.Sent | Should -Be 1
            $r.Via | Should -Be 'Outlook'
            $r.Sent | Should -BeTrue
            $r.To | Should -Be @('a@example.org', 'b@example.org')
            Should -Invoke Send-MailMessage -ModuleName Halyard -Times 0 -Exactly
        }
        It 'refuses to send without recipients, and does not even start Outlook' {
            { Send-HalyardReportMessage -Via Outlook -Html 'x' -Subject 's' } | Should -Throw -ExpectedMessage '*Specify -To*Outlook*'
            { Send-HalyardReportMessage -Via Outlook -Html 'x' -Subject 's' -To '', '  ' } | Should -Throw -ExpectedMessage '*Specify -To*'
            Should -Invoke New-Object -ModuleName Halyard -Times 0 -Exactly
            $script:mail.Sent | Should -Be 0
        }
        It 'says so, and suggests SMTP or a file, when Outlook COM automation fails' {
            Mock New-Object -ModuleName Halyard -ParameterFilter { $ComObject -eq 'Outlook.Application' } -MockWith { throw 'Class not registered' }
            { Send-HalyardReportMessage -Via Outlook -Html 'x' -Subject 's' -To 'a@example.org' } | Should -Throw -ExpectedMessage '*Outlook is not available*-Via Smtp or -Via File*'
            $script:mail.Sent | Should -Be 0
        }
        It 'does not swallow a failure of the send itself' {
            $script:mail | Add-Member -MemberType ScriptMethod -Name Send -Value { throw 'Outbox is full' } -Force
            { Send-HalyardReportMessage -Via Outlook -Html 'x' -Subject 's' -To 'a@example.org' } | Should -Throw -ExpectedMessage '*Outbox is full*'
        }
    }

    Context 'SMTP' {
        It 'sends the HTML through the given server with UTF-8 encoding' {
            $r = Send-HalyardReportMessage -Via Smtp -Html '<b>hi</b>' -Subject 'Weekly' -To 'a@example.org', 'b@example.org' -SmtpServer 'smtp.example.org' -From 'halyard@example.org'
            Should -Invoke Send-MailMessage -ModuleName Halyard -Times 1 -Exactly -ParameterFilter {
                $SmtpServer -eq 'smtp.example.org' -and $From -eq 'halyard@example.org' -and $Subject -eq 'Weekly' -and $Body -eq '<b>hi</b>' -and $BodyAsHtml -and
                $To.Count -eq 2 -and $To[1] -eq 'b@example.org' -and $Encoding.WebName -eq 'utf-8'
            }
            $r.Via | Should -Be 'Smtp'
            $r.Sent | Should -BeTrue
            $r.Server | Should -Be 'smtp.example.org'
            Should -Invoke New-Object -ModuleName Halyard -Times 0 -Exactly
        }
        It 'needs recipients, a server and a sender before anything is sent' {
            { Send-HalyardReportMessage -Via Smtp -Html 'x' -Subject 's' -SmtpServer 's' -From 'f@example.org' } | Should -Throw -ExpectedMessage '*Specify -To*SMTP*'
            { Send-HalyardReportMessage -Via Smtp -Html 'x' -Subject 's' -To 'a@example.org' -From 'f@example.org' } | Should -Throw -ExpectedMessage '*-SmtpServer and -From*'
            { Send-HalyardReportMessage -Via Smtp -Html 'x' -Subject 's' -To 'a@example.org' -SmtpServer 's' } | Should -Throw -ExpectedMessage '*-SmtpServer and -From*'
            Should -Invoke Send-MailMessage -ModuleName Halyard -Times 0 -Exactly
        }
        It 'does not swallow a failure of the mail server' {
            Mock Send-MailMessage -ModuleName Halyard -MockWith { throw 'Unable to connect to the remote server' }
            { Send-HalyardReportMessage -Via Smtp -Html 'x' -Subject 's' -To 'a@example.org' -SmtpServer 's' -From 'f@example.org' } | Should -Throw -ExpectedMessage '*Unable to connect*'
        }
    }

    Context 'File' {
        It 'sends nothing and says so' {
            $r = Send-HalyardReportMessage -Via File -Html 'x' -Subject 's' -To 'a@example.org'
            $r.Via | Should -Be 'File'
            $r.Sent | Should -BeFalse
            Should -Invoke New-Object -ModuleName Halyard -Times 0 -Exactly
            Should -Invoke Send-MailMessage -ModuleName Halyard -Times 0 -Exactly
        }
    }

    It 'rejects a delivery mode it does not know' {
        { Send-HalyardReportMessage -Via Carrier-Pigeon -Html 'x' -Subject 's' } | Should -Throw
    }
}

Describe 'Send-HalyardReport.ps1' {
    It 'accepts only Outlook, Smtp or File and keeps the -NoArc alias for one release' {
        $cmd = Get-Command $script:Script
        $set = $cmd.Parameters['Via'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $set.ValidValues | Should -Be @('Outlook', 'Smtp', 'File')
        $cmd.Parameters['NoLazarette'].Aliases | Should -Contain 'NoArc'
        $cmd.Parameters['To'].ParameterType.Name | Should -Be 'String[]'
    }
    It 'defaults to saving a file, never to sending' {
        $text = Get-Content -LiteralPath $script:Script -Raw
        $text | Should -Match "\[string\]\`$Via = 'File'"
    }
    It 'hands delivery to the module rather than talking to Outlook or SMTP itself' {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:Script, [ref]$null, [ref]$null)
        $called = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
        $called | Should -Contain 'Send-HalyardReportMessage'
        $called | Should -Not -Contain 'Send-MailMessage'
        $comObjects = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandParameterAst] -and $n.ParameterName -eq 'ComObject' }, $true))
        $comObjects | Should -BeNullOrEmpty
    }

    Context 'against a throw-away copy of the repository layout' {
        BeforeAll {
            $script:Temp = Join-Path ([IO.Path]::GetTempPath()) ('halyard-report-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force (Join-Path $script:Temp 'config') | Out-Null
            Import-HalyardCore
            $repo = New-Object Halyard.Core.Model.RepositoryRef ([Halyard.Core.Model.ProviderKind]::GitHub), 'example-org/synthetic-backlog'
            $items = New-Object 'System.Collections.Generic.List[Halyard.Core.Model.WorkItem]'
            foreach ($n in 1..3) {
                $w = New-Object Halyard.Core.Model.WorkItem $repo, ([long]$n)
                $w.Title = "FEATURE :: Synthetic feature $n"
                $w.Labels.Add('type::feature')
                $items.Add($w)
            }
            $store = New-Object Halyard.Core.Snapshots.SnapshotStore (Join-Path $script:Temp 'output\snapshots')
            [void]$store.Save($repo, $items, $null)
            $script:SavedRoot = $env:HALYARD_ROOT
            $env:HALYARD_ROOT = $script:Temp
        }
        AfterAll {
            $env:HALYARD_ROOT = $script:SavedRoot
            Remove-Item $script:Temp -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'renders the leadership report from the current snapshot to the requested file when Via is File' {
            $out = Join-Path $script:Temp 'weekly.html'
            $result = & $script:Script -Via File -OutFile $out -Title 'Synthetic weekly' -NoLazarette 6>$null
            $result | Should -Be $out
            Test-Path -LiteralPath $out | Should -BeTrue
            (Get-Content -LiteralPath $out -Raw) | Should -Match 'Synthetic weekly'
        }
        It 'saves under output\reports by default' {
            $result = & $script:Script -Via File -NoLazarette 6>$null
            $result | Should -BeLike (Join-Path $script:Temp 'output\reports\leadership-*.html')
            Test-Path -LiteralPath $result | Should -BeTrue
        }
        It 'stops before sending when Outlook or SMTP delivery lacks its settings' {
            { & $script:Script -Via Outlook -OutFile (Join-Path $script:Temp 'a.html') -NoLazarette 6>$null } | Should -Throw -ExpectedMessage '*Specify -To*'
            { & $script:Script -Via Smtp -To 'a@example.org' -OutFile (Join-Path $script:Temp 'b.html') -NoLazarette 6>$null } | Should -Throw -ExpectedMessage '*-SmtpServer and -From*'
        }
        It 'fails clearly when there is no snapshot to report on' {
            $empty = Join-Path ([IO.Path]::GetTempPath()) ('halyard-report-empty-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force (Join-Path $empty 'config') | Out-Null
            $env:HALYARD_ROOT = $empty
            try { { & $script:Script -Via File -NoLazarette 6>$null } | Should -Throw -ExpectedMessage '*No snapshot*' }
            finally { $env:HALYARD_ROOT = $script:Temp; Remove-Item $empty -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
