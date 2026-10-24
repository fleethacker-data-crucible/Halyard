<#
.SYNOPSIS
  Cascade write policy: sandbox vs production.
  Dev default = sandbox (52999). CBR (16629) is never a sandbox write target.
#>

function Get-CascadeWriteMode {
    $m = 'sandbox'
    if ($env:CASCADE_WRITE_MODE) {
        $m = $env:CASCADE_WRITE_MODE.Trim().ToLowerInvariant()
    }
    if ($m -notin @('sandbox','production','prod')) { $m = 'sandbox' }
    if ($m -eq 'prod') { $m = 'production' }
    return $m
}

function Get-CascadeProjectIds {
    # Canonical defaults for this program
    $cbr = 16629
    $sandbox = 52999

    $t = 0
    if ($env:CASCADE_SANDBOX_PROJECT_ID -and [int]::TryParse($env:CASCADE_SANDBOX_PROJECT_ID.Trim(), [ref]$t) -and $t -gt 0) {
        $sandbox = $t
    }

    $t = 0
    if ($env:GITLAB_SOURCE_PROJECT_ID -and [int]::TryParse($env:GITLAB_SOURCE_PROJECT_ID.Trim(), [ref]$t) -and $t -gt 0) {
        # Never allow source/CBR to be the sandbox id
        if ($t -eq $sandbox) {
            Write-Host ("WRITE POLICY: GITLAB_SOURCE_PROJECT_ID={0} is sandbox - forcing CBR read to 16629" -f $t) -ForegroundColor Yellow
            $cbr = 16629
        } else {
            $cbr = $t
        }
    }

    $writeEnv = 0
    $t = 0
    if ($env:GITLAB_PROJECT_ID -and [int]::TryParse($env:GITLAB_PROJECT_ID.Trim(), [ref]$t) -and $t -gt 0) {
        $writeEnv = $t
    }

    return [pscustomobject]@{
        CbrProjectId      = $cbr
        SandboxProjectId  = $sandbox
        EnvWriteProjectId = $writeEnv
    }
}

function Get-CascadeWriteContext {
    param([switch]$AllowProduction)

    $mode = Get-CascadeWriteMode
    $ids = Get-CascadeProjectIds

    if ($mode -eq 'production') {
        $target = $ids.CbrProjectId
        if ($ids.EnvWriteProjectId -gt 0 -and $ids.EnvWriteProjectId -ne $ids.SandboxProjectId) {
            $target = $ids.EnvWriteProjectId
        }
        $requiresConfirm = -not ($AllowProduction -or ($env:CASCADE_ALLOW_PRODUCTION -eq '1'))
        return [pscustomobject]@{
            Mode             = 'production'
            WriteProjectId   = $target
            ReadProjectId    = $ids.CbrProjectId
            SandboxProjectId = $ids.SandboxProjectId
            Label            = 'production'
            RequiresConfirm  = $requiresConfirm
            AllowProduction  = [bool]($AllowProduction -or ($env:CASCADE_ALLOW_PRODUCTION -eq '1'))
        }
    }

    # sandbox mode: writes only to sandbox project
    return [pscustomobject]@{
        Mode             = 'sandbox'
        WriteProjectId   = $ids.SandboxProjectId
        ReadProjectId    = $ids.CbrProjectId
        SandboxProjectId = $ids.SandboxProjectId
        Label            = 'sandbox'
        RequiresConfirm  = $false
        AllowProduction  = $false
    }
}

function Assert-CascadeWriteTarget {
    param(
        [Parameter(Mandatory)][int]$ProjectId,
        [switch]$AllowProduction
    )
    $ctx = Get-CascadeWriteContext -AllowProduction:$AllowProduction
    $ids = Get-CascadeProjectIds
    if ($ctx.Mode -eq 'sandbox') {
        if ($ProjectId -ne $ids.SandboxProjectId) {
            throw ("WRITE BLOCKED: sandbox mode only permits project {0}; requested {1}." -f $ids.SandboxProjectId, $ProjectId)
        }
        return $ctx
    }
    if (-not $ctx.AllowProduction) {
        throw 'WRITE BLOCKED: production requires explicit -AllowProduction or CASCADE_ALLOW_PRODUCTION=1.'
    }
    if ($ProjectId -ne $ctx.WriteProjectId) {
        throw ("WRITE BLOCKED: requested project {0} does not match approved production project {1}." -f $ProjectId, $ctx.WriteProjectId)
    }
    return $ctx
}

function Write-CascadeWriteBanner {
    param([switch]$AllowProduction)
    $ctx = Get-CascadeWriteContext -AllowProduction:$AllowProduction
    $color = if ($ctx.Mode -eq 'production') { 'Red' } else { 'Cyan' }
    Write-Host ("Cascade WRITE mode={0}  write_project={1}  read_project={2}" -f $ctx.Mode, $ctx.WriteProjectId, $ctx.ReadProjectId) -ForegroundColor $color
    return $ctx
}
