function Test-CascadeHierarchyIntegrity {
    <#
    .SYNOPSIS
      Offline, read-only validation of Cascade work-item relationships.
    .DESCRIPTION
      Detects duplicate IDs, missing parents, self-parenting, cycles, and invalid
      parent/child type combinations before an AI-generated plan is applied.
      Does not contact GitLab or modify any work item.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Issues)

    $items = @{}
    $findings = (New-Object System.Collections.ArrayList)
    # Theme -> Epic -> Feature -> Story (tasks under Story)
    # PfW/DWI are Feature work-definition artifacts (optional parent Feature)
    $allowedParents = @{
        'THEME' = @()
        'EPIC' = @('THEME')
        'FEATURE' = @('EPIC')
        'STORY' = @('FEATURE')
        'TASK' = @('STORY')
        'PFW' = @('FEATURE')
        'DWI' = @('FEATURE')
        'SOW' = @()
        'PROPOSAL' = @('FEATURE')
    }
    $Issues = @($Issues | Where-Object { $null -ne $_ })
    foreach ($issue in $Issues) {
        if ($issue -is [System.Array]) { continue }
        $body = $null
        try {
            if ($issue.PSObject.Properties['description']) { $body = $issue.description }
            elseif ($issue.PSObject.Properties['Description']) { $body = $issue.Description }
        } catch { $body = $null }
        if ($body -is [System.Array]) { $body = ($body | ForEach-Object { "$_" }) -join "`n" }
        $body = [string]$body
        $meta = Get-CascadeMeta -Body $body
        if (-not $meta) { continue }
        $id = [string]$meta['id']; $type = ([string]$meta['type']).ToUpperInvariant()
        $parent = [string]$meta['parent']
        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($type)) {
            [void]$findings.Add([pscustomobject]@{ Code='INVALID_META'; Id=$id; Detail='Missing id or type' }); continue
        }
        if ($items.ContainsKey($id)) {
            [void]$findings.Add([pscustomobject]@{ Code='DUPLICATE_ID'; Id=$id; Detail='Multiple work items share this ID' }); continue
        }
        $items[$id] = [pscustomobject]@{ Id=$id; Type=$type; Parent=$parent }
    }
    foreach ($item in $items.Values) {
        if (-not $item.Parent) { continue }
        if ($item.Parent -eq $item.Id) {
            [void]$findings.Add([pscustomobject]@{ Code='SELF_PARENT'; Id=$item.Id; Detail='Item references itself' }); continue
        }
        if (-not $items.ContainsKey($item.Parent)) {
            [void]$findings.Add([pscustomobject]@{ Code='MISSING_PARENT'; Id=$item.Id; Detail="Parent $($item.Parent) is absent from input" }); continue
        }
        if ($allowedParents.ContainsKey($item.Type) -and $items[$item.Parent].Type -notin $allowedParents[$item.Type]) {
            [void]$findings.Add([pscustomobject]@{ Code='INVALID_PARENT_TYPE'; Id=$item.Id; Detail="A $($item.Type) cannot be parented by $($items[$item.Parent].Type)" })
        }
    }
    # Iterative walk prevents infinite recursion on cyclic graphs.
    $reportedCycles = @{}
    foreach ($item in $items.Values) {
        $seen = @{}; $cursor = $item
        while ($cursor -and $cursor.Parent -and $items.ContainsKey($cursor.Parent)) {
            if ($seen.ContainsKey($cursor.Id)) {
                $members = @($seen.Keys | Sort-Object)
                $key = $members -join '|'
                if (-not $reportedCycles.ContainsKey($key)) {
                    $reportedCycles[$key] = $true
                    [void]$findings.Add([pscustomobject]@{ Code='CYCLE'; Id=$cursor.Id; Detail="Cycle found during walk from $($item.Id)" })
                }
                break
            }
            $seen[$cursor.Id] = $true
            $cursor = $items[$cursor.Parent]
        }
    }
    return [pscustomobject]@{
        IsValid = ($findings.Count -eq 0)
        ItemCount = $items.Count
        Findings = [object[]]@($findings.ToArray())
    }
}
