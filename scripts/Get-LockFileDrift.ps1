<#
.SYNOPSIS
    Reports which committed packages.lock.json files disagree with Directory.Packages.props.

.DESCRIPTION
    The single place this repository decides what "the lock file is stale" means, so the
    restore wrapper and its test read one rule rather than two.

    Why it is needed: Dependabot bumps a version in Directory.Packages.props and rewrites
    only the lock file of the project that declares the direct PackageReference. With
    CentralPackageTransitivePinningEnabled, a downstream project carries the same package as
    a CentralTransitive entry, and that entry keeps the old range. Locked restore then fails
    with NU1004 in every job, and the error names neither the stale file nor the command
    that repairs it.

    The comparison is textual, against the range NuGet writes for a central version: a plain
    version V becomes "[V, )", and an explicit pin "[V]" becomes "[V, V]". Any other range
    syntax is compared as written with whitespace collapsed. A package with no central
    version is skipped -- the implicit SDK references (Microsoft.DotNet.ILCompiler,
    Microsoft.NET.ILLink.Tasks) follow the SDK, and there is nothing for them to disagree
    with. So is a lock entry with no requested range: central management does not pin it.

    Dot-sourced, so it sets no preferences of its own; the caller keeps its own.
#>

function Get-CentralPackageVersion {
    param([Parameter(Mandatory = $true)][string] $PropsPath)

    $versions = @{}
    $document = [xml](Get-Content -LiteralPath $PropsPath -Raw)
    foreach ($node in $document.SelectNodes('//PackageVersion')) {
        $versions[$node.GetAttribute('Include')] = $node.GetAttribute('Version')
    }
    return $versions
}

<#
.SYNOPSIS
    The requested range NuGet records in a lock file for a central version.
#>
function ConvertTo-RequestedRange {
    param([Parameter(Mandatory = $true)][string] $CentralVersion)

    $value = $CentralVersion.Trim()
    if ($value -match '^[^\[\](),\s]+$') { return "[$value, )" }
    if ($value -match '^\[\s*([^\[\](),\s]+)\s*\]$') { return "[$($Matches[1]), $($Matches[1])]" }
    return ($value -replace '\s+', ' ')
}

function Get-LockFileDrift {
    param(
        [Parameter(Mandatory = $true)][string] $LockPath,
        [Parameter(Mandatory = $true)][hashtable] $CentralVersion,
        [string] $DisplayPath
    )

    $drift = @()
    if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) { return $drift }
    if (-not $DisplayPath) { $DisplayPath = $LockPath }

    $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
    if (-not $lock.PSObject.Properties['dependencies']) { return $drift }

    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($entry in $framework.Value.PSObject.Properties) {
            $package = $entry.Value
            if (-not $package.PSObject.Properties['requested']) { continue }
            if (-not $CentralVersion.ContainsKey($entry.Name)) { continue }

            $expected = ConvertTo-RequestedRange -CentralVersion $CentralVersion[$entry.Name]
            if (($package.requested -replace '\s+', ' ') -ne $expected) {
                $drift += [pscustomobject]@{
                    LockFile  = $DisplayPath
                    Framework = $framework.Name
                    Package   = $entry.Name
                    Locked    = $package.requested
                    Central   = $CentralVersion[$entry.Name]
                }
            }
        }
    }
    return $drift
}

<#
.SYNOPSIS
    The drift across every project the solution builds.

.DESCRIPTION
    The project list comes from the solution file, so a lock file outside it
    (tests/e2e/PtyHarness, restored by its own step) is not reported against a restore that
    never looked at it.
#>
function Get-SolutionLockFileDrift {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [string] $Solution = 'wip.slnx'
    )

    $props = Join-Path $Root 'Directory.Packages.props'
    if (-not (Test-Path -LiteralPath $props -PathType Leaf)) { return @() }
    $central = Get-CentralPackageVersion -PropsPath $props

    $solutionPath = Join-Path $Root $Solution
    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) { return @() }
    $document = [xml](Get-Content -LiteralPath $solutionPath -Raw)

    $drift = @()
    foreach ($node in $document.SelectNodes('//Project')) {
        $relative = $node.GetAttribute('Path').Replace([char]92, [char]47)
        $directory = [IO.Path]::GetDirectoryName($relative).Replace([char]92, [char]47)
        $display = "$directory/packages.lock.json"
        $drift += Get-LockFileDrift -LockPath (Join-Path $Root $display) -CentralVersion $central -DisplayPath $display
    }
    return $drift
}

<#
.SYNOPSIS
    The report a failed restore prints: what is stale, and the command that repairs it.
#>
function Format-LockFileDriftReport {
    param(
        [Parameter(Mandatory = $true)][object[]] $Drift,
        [string] $Solution = 'wip.slnx'
    )

    $lines = @()
    foreach ($group in ($Drift | Group-Object -Property LockFile)) {
        $lines += "$($group.Name) is stale:"
        foreach ($item in $group.Group) {
            $lines += "  $($item.Package) ($($item.Framework)) locked $($item.Locked), central $($item.Central)"
        }
    }
    $lines += "Dependabot rewrites only the lock file of the project that declares the direct reference."
    $lines += "Fix with: dotnet restore $Solution --force-evaluate"
    return ($lines -join [Environment]::NewLine)
}
