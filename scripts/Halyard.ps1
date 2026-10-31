#requires -Version 5.1
<#
.SYNOPSIS
  Halyard command line (Fleet CLI parity). Today: `commands`, which lists every Halyard command.
.EXAMPLE
  .\scripts\Halyard.ps1 commands --json
  .\scripts\Halyard.ps1 commands --json --out commands.json
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'commands',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\module\Halyard\Halyard.psd1') -Force
$rest = @($Rest)
switch ($Command) {
    'commands' {
        if ($rest -contains '--json') {
            $text = Get-HalyardCommand -Json
            $i = [array]::IndexOf($rest, '--out')
            if ($i -ge 0 -and $i + 1 -lt $rest.Count) {
                # UTF-8 without a BOM: `>` in Windows PowerShell 5.1 writes UTF-16 and breaks JSON readers.
                [IO.File]::WriteAllText($rest[$i + 1], $text, (New-Object System.Text.UTF8Encoding($false)))
            }
            else { $text }
        }
        else { Get-HalyardCommand | Format-Table Name, Method, Route -AutoSize }
    }
    default {
        [Console]::Error.WriteLine('Unknown command "' + $Command + '". Try: commands [--json] [--out <file>]. The other commands are reached through the loopback API (see docs/CLI.md).')
        exit 2
    }
}
