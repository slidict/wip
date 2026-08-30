<#
.SYNOPSIS
    Drives the full wip lifecycle against a real WSLC installation.

.DESCRIPTION
    The unit and golden suites deliberately never touch WSLC: their whole point is that the
    resolution, build, and execution layers are swappable, so they run anywhere and prove
    what wip *would* send. Nothing in them proves that wslc accepts it. This script is the
    other half -- it runs wip.exe against real containers and asserts on exit codes and
    output, so a change in wslc's own interface (a renamed flag, a different `list --format
    json` shape) fails here rather than in someone's terminal.

    It is used by .github/workflows/e2e-windows.yml, and is meant to be runnable by hand on
    any Windows machine with WSL2 and WSLC:

        pwsh tests/e2e/run-e2e.ps1 -Wip artifacts/win-x64/wip.exe

    The fixture is copied to a scratch directory under the Windows filesystem before
    anything runs. A repository checked out on the WSL filesystem would otherwise reach
    wslc as a UNC path, which it resolves as a Windows path -- see the README's note on
    keeping projects on C:\.

.PARAMETER Wip
    Path to the wip executable under test.

.PARAMETER Wslc
    The wslc command, used for the preflight smoke test and for failure diagnostics only.
    wip resolves its own wslc through wip.yml.

.PARAMETER BaseImage
    Image the fixture Dockerfile builds on. Override it to point at a registry mirror.

.PARAMETER WorkRoot
    Where the scratch copy of the fixture is created. Defaults to RUNNER_TEMP (set on GitHub
    Actions) and then to TEMP.

.PARAMETER KeepWorkspace
    Leave the scratch directory behind for inspection.
#>
[CmdletBinding()]
param(
    [string] $Wip = "artifacts/win-x64/wip.exe",
    [string] $Wslc = "wslc",
    [string] $BaseImage = "alpine:3.20",
    [string] $WorkRoot = $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }),
    [switch] $KeepWorkspace
)

# wip reports progress on stderr the way the Ruby build's `warn` did, and PowerShell turns a
# native command's stderr into a terminating error when $ErrorActionPreference is 'stop' --
# which the GitHub Actions pwsh shell sets. Disarm both, or `wip up` announcing that it
# started a container would abort the run.
$ErrorActionPreference = 'Continue'
$PSNativeCommandUseErrorActionPreference = $false

$script:Container = 'wip-e2e-app'
$script:Workspace = $null
$script:Failed = $false

function Write-Step([string] $Message) {
    Write-Host ""
    Write-Host "=== $Message" -ForegroundColor Cyan
}

# Every invocation goes through here: stderr is merged into the captured output so it never
# reaches PowerShell's error stream, and the exit code is read before anything else can
# clobber $LASTEXITCODE.
function Invoke-Capture([string] $Exe, [string[]] $Arguments, [string] $WorkingDirectory) {
    $previous = $PWD
    if ($WorkingDirectory) { Set-Location $WorkingDirectory }
    try {
        $output = & $Exe @Arguments 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        Set-Location $previous
    }

    Write-Host "`$ $Exe $($Arguments -join ' ')  -> exit $code"
    if ($output.Trim()) { Write-Host $output.TrimEnd() }
    return [pscustomobject]@{ Code = $code; Output = $output }
}

function Invoke-Wip([string[]] $Arguments) {
    return Invoke-Capture -Exe $script:WipPath -Arguments $Arguments -WorkingDirectory $script:Workspace
}

function Invoke-Wslc([string[]] $Arguments) {
    return Invoke-Capture -Exe $Wslc -Arguments $Arguments
}

function Assert-Exit($Result, [int] $Expected, [string] $What) {
    if ($Result.Code -ne $Expected) {
        throw "$What exited $($Result.Code), expected $Expected"
    }
}

function Assert-NonZero($Result, [string] $What) {
    if ($Result.Code -eq 0) {
        throw "$What exited 0, expected a non-zero status to be forwarded"
    }
}

function Assert-Match($Result, [string] $Pattern, [string] $What) {
    if ($Result.Output -notmatch $Pattern) {
        throw "$What did not match /$Pattern/"
    }
}

function Assert-NoMatch($Result, [string] $Pattern, [string] $What) {
    if ($Result.Output -match $Pattern) {
        throw "$What unexpectedly matched /$Pattern/"
    }
}

# Runs whatever wslc can still tell us about the machine. Best effort by design: this is
# reached when something has already failed, so a second failure here must not replace the
# original one.
function Write-Diagnostics {
    Write-Step "Diagnostics"
    foreach ($arguments in @(@('version'), @('list', '--all'))) {
        try { Invoke-Wslc $arguments | Out-Null } catch { Write-Host "  (wslc $($arguments -join ' ') itself failed: $_)" }
    }

    # Container logs run behind a job with a deadline. `wslc logs` is only asked not to
    # follow by wip passing no -f, and a diagnostics helper that hung would replace a
    # readable failure with a job timing out twenty minutes later.
    $logs = Start-Job -ScriptBlock { param($exe, $name) & $exe logs $name 2>&1 } -ArgumentList $Wslc, $script:Container
    if (Wait-Job $logs -Timeout 30) { Receive-Job $logs | Out-String | Write-Host }
    else { Write-Host "  (wslc logs $script:Container did not finish within 30s)" }
    Remove-Job $logs -Force

    try { & wsl.exe --status 2>&1 | Out-String | Write-Host } catch { Write-Host "  (wsl --status failed: $_)" }
}

function Remove-Leftovers {
    # `wip down` is the command under test, so cleanup cannot rely on it having worked --
    # this removes the container directly, and says nothing when there was none. It also runs
    # from the finally block, where wslc may be the very thing that was missing, so a failure
    # here must not replace the error that got us there.
    try { & $Wslc remove -f $script:Container 2>&1 | Out-Null } catch { }
}

$resolvedWip = Resolve-Path -LiteralPath $Wip -ErrorAction SilentlyContinue
if (-not $resolvedWip) {
    throw "wip executable not found at '$Wip'. Publish it first: dotnet publish src/Wip.Cli/Wip.Cli.csproj -c Release -r win-x64 -o artifacts/win-x64"
}

$script:WipPath = $resolvedWip.Path
$fixture = $PSScriptRoot

try {
    # ------------------------------------------------------------------ preflight
    #
    # Separating this from the lifecycle assertions matters: if wslc cannot pull and run an
    # image at all, that is the environment failing, not wip, and the log should say so
    # before any wip command has run.
    Write-Step "Preflight: WSLC is present and can run a container"
    $version = Invoke-Wslc @('version')
    Assert-Exit $version 0 "wslc version"

    $hello = Invoke-Wslc @('run', '--rm', $BaseImage, 'echo', 'wslc-smoke-ok')
    Assert-Exit $hello 0 "wslc run --rm $BaseImage"
    Assert-Match $hello 'wslc-smoke-ok' "wslc run --rm $BaseImage"

    # ------------------------------------------------------------------ workspace
    Write-Step "Staging the fixture project"
    $script:Workspace = Join-Path $WorkRoot ("wip-e2e-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $script:Workspace -Force | Out-Null
    Copy-Item -Path (Join-Path $fixture 'wip.yml') -Destination $script:Workspace
    Copy-Item -Path (Join-Path $fixture 'Dockerfile') -Destination $script:Workspace

    $dockerfile = Join-Path $script:Workspace 'Dockerfile'
    (Get-Content -LiteralPath $dockerfile) -replace '^FROM\s+\S+$', "FROM $BaseImage" |
        Set-Content -LiteralPath $dockerfile
    Write-Host "workspace: $script:Workspace (base image: $BaseImage)"

    Remove-Leftovers

    # ------------------------------------------------------------------ wip finds wslc
    Write-Step "wip version / wip config"
    $wipVersion = Invoke-Wip @('version')
    Assert-Exit $wipVersion 0 "wip version"
    Assert-Match $wipVersion '(?m)^wip \d+\.\d+\.\d+' "wip version"

    $config = Invoke-Wip @('config')
    Assert-Exit $config 0 "wip config"
    Assert-Match $config 'wip-e2e-app' "wip config"

    # ------------------------------------------------------------------ build
    Write-Step "wip build"
    $build = Invoke-Wip @('build')
    Assert-Exit $build 0 "wip build"

    # ------------------------------------------------------------------ up
    Write-Step "wip up -d"
    $up = Invoke-Wip @('up', '-d')
    Assert-Exit $up 0 "wip up -d"

    $ps = Invoke-Wip @('ps')
    Assert-Exit $ps 0 "wip ps"
    Assert-Match $ps 'wip-e2e-app' "wip ps"

    # Straight from wslc as well, because `wip ps` reads the same `list --format json` that
    # `wip up` used to decide whether to create the container: if that parse is wrong, both
    # are wrong together and only wslc's own output shows it.
    $listed = Invoke-Wslc @('list', '--all')
    Assert-Match $listed 'wip-e2e-app' "wslc list --all after wip up"

    # ------------------------------------------------------------------ exec
    Write-Step "wip exec"
    $exec = Invoke-Wip @('exec', '--no-interactive', 'cat', '/srv/marker.txt')
    Assert-Exit $exec 0 "wip exec"
    Assert-Match $exec 'wip-e2e-build-marker' "wip exec"

    # An interaction: entry reaches the same container by name rather than as a bare argv.
    $marker = Invoke-Wip @('marker')
    Assert-Exit $marker 0 "wip marker (interaction: entry)"
    Assert-Match $marker 'wip-e2e-build-marker' "wip marker (interaction: entry)"

    # A failing command inside the container has to come back as wip's own exit status; a
    # wrapper that swallowed it would make every CI use of `wip exec` silently green.
    $failing = Invoke-Wip @('exec', '--no-interactive', 'cat', '/srv/does-not-exist')
    Assert-NonZero $failing "wip exec on a missing file"

    # ------------------------------------------------------------------ run
    Write-Step "wip run"
    $run = Invoke-Wip @('run', '--no-interactive', 'echo', 'wip-e2e-run-marker')
    Assert-Exit $run 0 "wip run"
    Assert-Match $run 'wip-e2e-run-marker' "wip run"

    # `remove: true` in the fixture means the ephemeral container is `wslc run --rm`, so the
    # long-lived one from `wip up` should still be the only wip-e2e container around.
    $afterRun = Invoke-Wslc @('list', '--all')
    Assert-Match $afterRun 'wip-e2e-app' "wslc list --all after wip run"

    # ------------------------------------------------------------------ down
    Write-Step "wip down"
    $down = Invoke-Wip @('down')
    Assert-Exit $down 0 "wip down"

    $afterDown = Invoke-Wslc @('list', '--all')
    Assert-NoMatch $afterDown 'wip-e2e-app' "wslc list --all after wip down"

    Write-Step "All lifecycle assertions passed"
}
catch {
    $script:Failed = $true
    Write-Host ""
    Write-Host "E2E FAILED: $_" -ForegroundColor Red
    Write-Diagnostics
}
finally {
    Remove-Leftovers
    if ($script:Workspace -and -not $KeepWorkspace) {
        Remove-Item -LiteralPath $script:Workspace -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif ($script:Workspace) {
        Write-Host "workspace kept at $script:Workspace"
    }
}

if ($script:Failed) { exit 1 }
exit 0
