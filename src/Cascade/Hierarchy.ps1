function Build-CascadeTree {
    <#
    .SYNOPSIS
      Build parent/child tree from issues that have Cascade metadata (or inferred).
    .OUTPUTS
      Hashtable with Roots, Nodes (by id), Unparsed
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Issues
    )
    $nodes = [ordered]@{}
    $unparsed = @()

    foreach ($iss in $Issues) {
        $body = ''
        if ($iss.PSObject.Properties['description']) { $body = [string]$iss.description }
        elseif ($iss.PSObject.Properties['Description']) { $body = [string]$iss.Description }

        $meta = Get-CascadeMeta -Body $body
        $iid = $null
        if ($iss.PSObject.Properties['iid']) { $iid = [int]$iss.iid }
        elseif ($iss.PSObject.Properties['IID']) { $iid = [int]$iss.IID }

        $title = ''
        if ($iss.PSObject.Properties['title']) { $title = [string]$iss.title }
        elseif ($iss.PSObject.Properties['Title']) { $title = [string]$iss.Title }

        if (-not $meta) {
            $unparsed += [pscustomobject]@{
                Iid   = $iid
                Title = $title
                Issue = $iss
            }
            continue
        }

        $id = $meta['id']
        $nodes[$id] = [pscustomobject]@{
            Id       = $id
            Type     = "$($meta['type'])".ToUpper()
            Parent   = $meta['parent']
            State    = $(if ($meta['state']) { $meta['state'] } else { $meta['status'] })
            Meta     = $meta
            Iid      = $iid
            Title    = $title
            Issue    = $iss
            Children = [System.Collections.ArrayList]@()
        }
    }

    $roots = [System.Collections.ArrayList]@()
    foreach ($n in $nodes.Values) {
        if ($n.Parent -and $nodes.Contains($n.Parent)) {
            [void]$nodes[$n.Parent].Children.Add($n)
        }
        else {
            [void]$roots.Add($n)
        }
    }

    return [pscustomobject]@{
        Roots    = $roots
        Nodes    = $nodes
        Unparsed = $unparsed
    }
}

function Get-CascadeRollup {
    <#
    .SYNOPSIS
      0.0-1.0 completion. Leaf DONE/CLOSED = 1; parent = mean of children.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Node
    )
    if (-not $Node.Children -or $Node.Children.Count -eq 0) {
        $s = "$($Node.State)".ToUpper()
        if ($s -in @('DONE','CLOSED')) { return 1.0 }
        return 0.0
    }
    $sum = 0.0
    foreach ($c in $Node.Children) {
        $sum += Get-CascadeRollup -Node $c
    }
    return $sum / $Node.Children.Count
}

function Get-CascadeCloseSuggestions {
    [CmdletBinding()]
    param($Nodes)
    $out = @()
    foreach ($n in $Nodes.Values) {
        if (-not $n.Children -or $n.Children.Count -eq 0) { continue }
        $allDone = $true
        foreach ($c in $n.Children) {
            if ((Get-CascadeRollup -Node $c) -lt 1.0) { $allDone = $false; break }
        }
        $s = "$($n.State)".ToUpper()
        if ($allDone -and $s -notin @('DONE','CLOSED')) {
            $out += $n.Id
        }
    }
    return $out
}

function Get-CascadeMetrics {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Issues)

    $tree = Build-CascadeTree -Issues $Issues
    $features = @($tree.Nodes.Values | Where-Object { $_.Type -eq 'FEATURE' })
    $stories  = @($tree.Nodes.Values | Where-Object { $_.Type -eq 'STORY' })
    $done = @($stories | Where-Object { "$($_.State)".ToUpper() -in @('DONE','CLOSED') })

    $completion = [ordered]@{}
    foreach ($f in $features) {
        $completion[$f.Id] = [math]::Round((Get-CascadeRollup -Node $f) * 100)
    }

    return [pscustomobject]@{
        ParsedCount        = $tree.Nodes.Count
        UnparsedCount      = $tree.Unparsed.Count
        FeatureCount       = $features.Count
        StoryCount         = $stories.Count
        StoriesDone        = $done.Count
        FeatureCompletion  = $completion
        CloseSuggestions   = @(Get-CascadeCloseSuggestions -Nodes $tree.Nodes)
        Tree               = $tree
    }
}
