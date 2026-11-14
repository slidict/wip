<#
.SYNOPSIS
    Restores the solution in locked mode, and names the fix for the failure that recurs.

.DESCRIPTION
    Every CI job restores with --locked-mode, and the failure that keeps coming back is a
    Dependabot bump whose lock-file rewrite missed a downstream project. NU1004 states the
    mismatch but not which lock file is stale or how to repair it, so at a glance it is
    indistinguishable from a breaking change in the bumped package -- and rediscovering that
    costs a read of every failed job's log.

    Advice needs two things: NuGet reporting a lock mismatch in this restore, and a lock file
    that really disagrees with the central versions. Drift alone would misread an unrelated
    failure -- SDK selection failing before NuGet evaluates the locks, NU1903 from an implicit
    SDK package, an unreachable feed -- as something --force-evaluate repairs, which is worse
    than saying nothing. docs/reproducible-builds.md keeps --force-evaluate an intentional,
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

# Tee-Object creates its variable only when a record reaches it, and a restore that writes
# nothing to stdout (a runtime-only install with no SDK) would leave it unset for the read
# below to fail on under Set-StrictMode.
$output = @()

Push-Location $repo
try {
    # Tee rather than capture: the restore's own output still streams to the job log, and
    # the copy is what decides whether NuGet reported a lock mismatch at all. Both streams
    # are teed, so the decision does not depend on which one MSBuild's logger writes NU1004
    # to -- a diagnosis that disappeared when the error moved to stderr would be the same
    # bare NU1004 this script exists to replace.
    #
    # Windows PowerShell turns every redirected stderr line into an error record, and 'Stop'
    # would raise it before the advice could run. Measured: the restore aborts with the
    # stderr text as a RemoteException. Lowered for this one call, so an unrelated failure
    # elsewhere in the script still stops.
    $preference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Dotnet restore $Solution --locked-mode 2>&1 | Tee-Object -Variable output
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $preference
    }
}
finally {
    Pop-Location
}

$advice = Get-LockedRestoreAdvice -ExitCode $code -Output $output -Root $repo -Solution $Solution
if ($advice) {
    Write-Host ''
    Write-Host $advice
}
exit $code
