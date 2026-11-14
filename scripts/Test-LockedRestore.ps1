# Exercise NuGet's locked restore rather than only inspecting lock-file syntax.
[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $tempRoot ('wip-lock-' + [Guid]::NewGuid().ToString('N'))
$projects = @('src/Wip.Core', 'src/Wip.Cli', 'tests/Wip.Tests')
# The diagnosis Invoke-LockedRestore.ps1 prints when the restore below fails. Checked against
# fixture files rather than four more real restores: the rule is a text comparison, and the
# NU1004 path is already exercised for real further down.
. (Join-Path $PSScriptRoot 'Get-LockFileDrift.ps1')
$diagnosis = Join-Path $tempRoot ('wip-drift-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $diagnosis 'src/Lib') -Force | Out-Null
try {
    function Set-DriftFixture {
        param([string]$Central, [string]$Requested, [string]$Package = 'Pkg')
        [IO.File]::WriteAllText((Join-Path $diagnosis 'Directory.Packages.props'), @"
<Project>
  <ItemGroup>
    <PackageVersion Include="$Package" Version="$Central" />
  </ItemGroup>
</Project>
"@)
        [IO.File]::WriteAllText((Join-Path $diagnosis 'wip.slnx'), @"
<Solution>
  <Project Path="src/Lib/Lib.csproj" />
</Solution>
"@)
        [IO.File]::WriteAllText((Join-Path $diagnosis 'src/Lib/packages.lock.json'), @"
{
  "version": 2,
  "dependencies": {
    "net10.0": {
      "Pkg": { "type": "CentralTransitive", "requested": "$Requested", "resolved": "1.0.0" },
      "Microsoft.DotNet.ILCompiler": { "type": "Direct", "requested": "[10.0.14, )", "resolved": "10.0.14" },
      "Other": { "type": "Transitive", "resolved": "1.0.0" }
    }
  }
}
"@)
    }

    Set-DriftFixture -Central '2.0.0' -Requested '[1.0.0, )'
    $drift = @(Get-SolutionLockFileDrift -Root $diagnosis)
    if ($drift.Count -ne 1) { throw "Drift was not reported once (got $($drift.Count))" }
    $report = Format-LockFileDriftReport -Drift $drift
    foreach ($expected in @('src/Lib/packages.lock.json is stale', 'Pkg', '[1.0.0, )', '2.0.0',
            'dotnet restore wip.slnx --force-evaluate')) {
        if ($report -notmatch [regex]::Escape($expected)) { throw "Report did not name '$expected': $report" }
    }

    # The advice is gated on NuGet having reported the mismatch, not on drift alone: a restore
    # that failed before it evaluated the locks is not repaired by --force-evaluate, even when a
    # lock file happens to be stale at the same time.
    $reported = @('/x/y.csproj : error NU1004: CentralTransitive ... [/x/wip.slnx]')
    $unrelated = @('error : Cannot find a .NET SDK matching global.json')
    if (-not (Get-LockedRestoreAdvice -ExitCode 1 -Output $reported -Root $diagnosis)) {
        throw 'A reported mismatch with a stale lock file gave no advice'
    }
    if (Get-LockedRestoreAdvice -ExitCode 1 -Output $unrelated -Root $diagnosis) {
        throw 'An unrelated failure was answered with the lock advice'
    }
    if (Get-LockedRestoreAdvice -ExitCode 0 -Output $reported -Root $diagnosis) {
        throw 'A successful restore produced advice'
    }

    # An in-sync pair, an explicit [V] pin against the "[V, V]" NuGet writes for it, and a
    # package with no central version must all stay silent: a restore that failed for another
    # reason must not be answered with --force-evaluate.
    Set-DriftFixture -Central '1.0.0' -Requested '[1.0.0, )'
    if (@(Get-SolutionLockFileDrift -Root $diagnosis).Count -ne 0) { throw 'An in-sync lock file was reported as stale' }
    if (Get-LockedRestoreAdvice -ExitCode 1 -Output $reported -Root $diagnosis) {
        throw 'Advice was produced with nothing stale to name'
    }
    Set-DriftFixture -Central '[1.0.0]' -Requested '[1.0.0, 1.0.0]'
    if (@(Get-SolutionLockFileDrift -Root $diagnosis).Count -ne 0) { throw 'An explicit pin was reported as stale' }
    Set-DriftFixture -Central '9.9.9' -Requested '[1.0.0, )' -Package 'Unmanaged'
    if (@(Get-SolutionLockFileDrift -Root $diagnosis).Count -ne 0) { throw 'A package with no central version was reported as stale' }
}
finally {
    Remove-Item -LiteralPath $diagnosis -Recurse -Force
}
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    foreach ($name in @('Directory.Build.props', 'Directory.Packages.props', 'global.json', 'NuGet.Config', 'wip.slnx')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $fixture
    }
    foreach ($project in $projects) {
        $destination = Join-Path $fixture $project
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $source = Join-Path $repo $project
        Get-ChildItem -LiteralPath $source -Filter '*.csproj' | Copy-Item -Destination $destination
        Copy-Item -LiteralPath (Join-Path $source 'packages.lock.json') -Destination $destination
    }
    Push-Location $fixture
    try {
        $expectedSdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
        $actualSdk = (& $Dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) { throw 'SDK does not match global.json' }
        $before = @{}
        foreach ($project in $projects) {
            $before[$project] = (Get-FileHash (Join-Path $project 'packages.lock.json') -Algorithm SHA256).Hash
        }
        & $Dotnet restore wip.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Unchanged graph failed locked restore' }
        foreach ($project in $projects) {
            $after = (Get-FileHash (Join-Path $project 'packages.lock.json') -Algorithm SHA256).Hash
            if ($after -ne $before[$project]) { throw "Locked restore rewrote $project" }
        }
        # NU1004 proves graph mismatch; a network or SDK error is not a passing test.
        $propsPath = Join-Path $fixture 'Directory.Packages.props'
        $props = [IO.File]::ReadAllText($propsPath)
        # Tighten the existing range without requiring a different package download.
        $changed = $props -replace '(Include="YamlDotNet" Version=")([^"]+)', '${1}[${2}]'
        if ($changed -eq $props) { throw 'Drift fixture did not change YamlDotNet' }
        [IO.File]::WriteAllText($propsPath, $changed)
        $output = & $Dotnet restore wip.slnx --locked-mode 2>&1 | Out-String
        $code = $LASTEXITCODE
        if ($code -eq 0 -or $output -notmatch '\bNU1004\b') {
            throw "Dependency drift was not rejected with NU1004 (exit $code): $output"
        }
        foreach ($project in $projects) {
            $after = (Get-FileHash (Join-Path $project 'packages.lock.json') -Algorithm SHA256).Hash
            if ($after -ne $before[$project]) { throw "Rejected restore rewrote $project" }
        }
        # This is the intentional update path, also used to repair partial Dependabot
        # lock updates. Evaluate the entire solution, including downstream projects.
        & $Dotnet restore wip.slnx --force-evaluate
        if ($LASTEXITCODE -ne 0) { throw 'Intentional lock refresh failed' }
        foreach ($project in $projects) {
            $after = (Get-FileHash (Join-Path $project 'packages.lock.json') -Algorithm SHA256).Hash
            if ($after -eq $before[$project]) { throw "Lock refresh missed $project" }
        }
        & $Dotnet restore wip.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Updated graph failed locked restore' }
        Write-Output 'PASS: exact SDK, immutable locks, NU1004 on drift, the stale-lock diagnosis, and whole-solution lock refresh'
    } finally { Pop-Location }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^wip-lock-[a-f0-9]{32}$') {
        throw 'Refusing cleanup outside the allocated fixture'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
