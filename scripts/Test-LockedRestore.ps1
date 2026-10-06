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
        Write-Output 'PASS: exact SDK, immutable locks, NU1004 on drift, and whole-solution lock refresh'
    } finally { Pop-Location }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^wip-lock-[a-f0-9]{32}$') {
        throw 'Refusing cleanup outside the allocated fixture'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
