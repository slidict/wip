<#
.SYNOPSIS
    Restores the solution in locked mode, and names the fix for the failure that recurs.

.DESCRIPTION
    Every CI job restores with --locked-mode, and the failure that keeps coming back is a
    Dependabot bump whose lock-file rewrite missed a downstream project. NU1004 states the
    mismatch but not which lock file is stale or how to repair it, so at a glance it is
    indistinguishable from a breaking change in the bumped package -- and rediscovering that
    costs a read of every failed job's log.

    Only a lock-against-central mismatch gets advice. A restore that failed for any other
    reason (NU1903 from an implicit SDK package, an unreachable feed) prints nothing extra:
    --force-evaluate would rewrite the lock files without fixing those, which is worse than
    saying nothing. docs/reproducible-builds.md keeps --force-evaluate an intentional,
    committed refresh; this script never runs it.

    The exit code is the restore's own, so every caller keeps the behaviour it had when the
    step was `dotnet restore wip.slnx --locked-mode`.
#>
[CmdletBinding()]
param(
    [string] $Solution = 'wip.slnx',
    [string] $Dotnet = 'dotnet'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A nonzero restore is this script's input, not an exception to abort on.
$PSNativeCommandUseErrorActionPreference = $false

$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Get-LockFileDrift.ps1')

Push-Location $repo
try {
    & $Dotnet restore $Solution --locked-mode
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($code -eq 0) { exit 0 }

$drift = @(Get-SolutionLockFileDrift -Root $repo -Solution $Solution)
if ($drift.Count -gt 0) {
    Write-Host ''
    Write-Host (Format-LockFileDriftReport -Drift $drift -Solution $Solution)
}
exit $code
