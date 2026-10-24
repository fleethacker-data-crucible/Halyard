function Export-CascadeTreeReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Metrics,
        [string]$Path = (Join-Path (Get-Location) 'output\tree_report.txt')
    )
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

    $lines = @()
    $lines += "CASCADE TREE REPORT  $(Get-Date -Format o)"
    $lines += "Parsed: $($Metrics.ParsedCount)  Unparsed: $($Metrics.UnparsedCount)"
    $lines += "Features: $($Metrics.FeatureCount)  Stories: $($Metrics.StoryCount)  Done: $($Metrics.StoriesDone)"
    $lines += ""
    $lines += "=== Feature completion ==="
    foreach ($k in $Metrics.FeatureCompletion.Keys) {
        $lines += "  $k : $($Metrics.FeatureCompletion[$k])%"
    }
    $lines += ""
    $lines += "=== Unparsed issues (need metadata) ==="
    foreach ($u in $Metrics.Tree.Unparsed) {
        $lines += "  !$($u.Iid)  $($u.Title)"
    }
    $lines | Set-Content -Path $Path -Encoding UTF8
    Write-Host "Wrote $Path"
    return $Path
}

function Export-CascadeMondayHtml {
    <#
    .SYNOPSIS
      Minimal playbook-style HTML snapshot for email body or browser.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Metrics,
        [string]$Path = (Join-Path (Get-Location) 'output\monday_dashboard.html'),
        [string]$Title = 'SMPG Cascade - Weekly Snapshot'
    )
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

    $rows = foreach ($k in $Metrics.FeatureCompletion.Keys) {
        $pct = $Metrics.FeatureCompletion[$k]
        "<tr><td>$k</td><td>$pct%</td></tr>"
    }
    $unparsed = $Metrics.UnparsedCount
    $html = @"
<!DOCTYPE html>
<html><head><meta charset="utf-8"/><title>$Title</title>
<style>
body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}
h1{color:#1F4E79} table{border-collapse:collapse} td,th{border:1px solid #ccc;padding:6px 10px}
th{background:#1F4E79;color:#fff} .muted{color:#666}
</style></head><body>
<h1>$Title</h1>
<p class="muted">Generated $(Get-Date -Format u)</p>
<p>Parsed work items: <b>$($Metrics.ParsedCount)</b> &nbsp;|&nbsp;
Stories: <b>$($Metrics.StoryCount)</b> (done $($Metrics.StoriesDone)) &nbsp;|&nbsp;
Unparsed (need Cascade metadata): <b>$unparsed</b></p>
<h2>Feature completion</h2>
<table><tr><th>Feature</th><th>%</th></tr>
$($rows -join "`n")
</table>
<h2>Notes</h2>
<ul>
<li>Items without a Cascade metadata block appear under Unparsed until migrated.</li>
<li>This file is safe to paste into email or open locally - no special account required.</li>
</ul>
</body></html>
"@
    $html | Set-Content -Path $Path -Encoding UTF8
    Write-Host "Wrote $Path"
    return $Path
}
