<#
.SYNOPSIS
  Cascade error / diagnostic logging.
  Always write the latest failure to output/cascade_last_error.txt (paste this).
  Also append to output/cascade_errors.log (history).
#>

function Get-CascadeOutputDir {
    $root = $null
    try {
        if ($PSScriptRoot) {
            # src/Cascade -> repo root
            $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
            if (-not (Test-Path (Join-Path $root 'scripts'))) {
                $root = Split-Path $PSScriptRoot -Parent
            }
        }
    } catch { }
    if (-not $root -or -not (Test-Path $root)) {
        $root = (Get-Location).Path
    }
    # Prefer repo with scripts/ and src/
    if (-not (Test-Path (Join-Path $root 'scripts'))) {
        $cand = Join-Path $root 'cascade'
        if (Test-Path (Join-Path $cand 'scripts')) { $root = $cand }
    }
    $out = Join-Path $root 'output'
    if (-not (Test-Path $out)) {
        New-Item -ItemType Directory -Path $out -Force | Out-Null
    }
    return $out
}

function Write-CascadeErrorLog {
    <#
    .SYNOPSIS
      Write a structured error block for support. Returns path to cascade_last_error.txt
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string]$Message,
        [string]$Detail = '',
        [string]$Exception = '',
        [hashtable]$Context = $null
    )

    $out = Get-CascadeOutputDir
    $lastPath = Join-Path $out 'cascade_last_error.txt'
    $histPath = Join-Path $out 'cascade_errors.log'
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

    $lines = @(
        '=== CASCADE ERROR LOG (paste this file or block) ==='
        "timestamp : $ts"
        "operation : $Operation"
        "message   : $Message"
    )
    if ($Exception) {
        $lines += "exception : $Exception"
    }
    if ($Context) {
        $lines += 'context   :'
        foreach ($k in ($Context.Keys | Sort-Object)) {
            $lines += ("  {0} = {1}" -f $k, $Context[$k])
        }
    }
    if ($Detail) {
        $lines += '--- detail ---'
        $lines += $Detail
    }
    $lines += '=== END CASCADE ERROR LOG ==='
    $lines += ''

    $block = $lines -join "`r`n"
    $block | Set-Content -Path $lastPath -Encoding UTF8
    try {
        Add-Content -Path $histPath -Value $block -Encoding UTF8
    } catch { }

    Write-Host ("Error log -> {0}" -f $lastPath) -ForegroundColor Yellow
    Write-Host '  Paste output/cascade_last_error.txt when reporting issues.' -ForegroundColor DarkGray
    return $lastPath
}

function Write-CascadeInfoLog {
    param(
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string]$Message
    )
    $out = Get-CascadeOutputDir
    $path = Join-Path $out 'cascade_info.log'
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = "[{0}] {1} | {2}" -f $ts, $Operation, $Message
    try { Add-Content -Path $path -Value $line -Encoding UTF8 } catch { }
}
