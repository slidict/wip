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
# which the GitHub Actions pwsh shell sets. Only the native half of that is disarmed here:
# `wip up` announcing that it started a container must not abort the run, but a cmdlet
# failing must. Staging is the case that matters -- a failed Copy-Item or Set-Location under
# 'Continue' would print its error and then run the lifecycle against an incomplete
# workspace, reporting whatever that produced as if it were wip's behaviour.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$script:Container = 'wip-e2e-app'
# Every assertion below is built from $script:Container rather than repeating the literal, so
# renaming the fixture's container: cannot leave a check silently pointed at a container that
# no longer exists -- which would pass by matching nothing.
$script:ContainerPattern = [regex]::Escape($script:Container)
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

# wslc's own container states, from microsoft/WSL's ContainerModel.h -- the same table
# CliContext maps. `--format json` reported the number through 2.9.9 and docker's name for it
# from 2.9.10; both are accepted here for the reason wip accepts both, which is that the
# assertion is about the container's state and not about which release printed it.
$script:WslcStateNumbers = @{ created = 1; running = 2; exited = 3; deleted = 4 }

function Assert-State($Result, [string] $Expected, [string] $What) {
    $number = $script:WslcStateNumbers[$Expected]
    if ($null -eq $number) { throw "no wslc state number for '$Expected'" }

    # Anchored on the field so a state name appearing anywhere else in the record -- in an
    # image tag, a container name -- cannot answer for it.
    $pattern = '"State"\s*:\s*(?:' + $number + '|"' + $Expected + '")'
    if ($Result.Output -notmatch $pattern) {
        throw "$What did not report State $Expected ($number): $($Result.Output.Trim())"
    }
}

# Runs whatever wslc can still tell us about the machine. Best effort by design: this is
# reached when something has already failed, so a second failure here must not replace the
# original one.
function Write-Diagnostics {
    Write-Step "Diagnostics"
    foreach ($arguments in @(
            @('version'),
            @('list', '--all'),
            @('list', '--all', '--filter', "name=$($script:Container)", '--format', 'json'))) {
        try { Invoke-Wslc $arguments | Out-Null } catch { Write-Host "  (wslc $($arguments -join ' ') itself failed: $_)" }
    }

    # Container logs run behind a job with a deadline. `wslc logs` is only asked not to
    # follow by wip passing no -f, and a diagnostics helper that hung would replace a
    # readable failure with a job timing out twenty minutes later.
    try {
        $logs = Start-Job -ScriptBlock { param($exe, $name) & $exe logs $name 2>&1 } -ArgumentList $Wslc, $script:Container
        if (Wait-Job $logs -Timeout 30) { Receive-Job $logs -ErrorAction SilentlyContinue | Out-String | Write-Host }
        else { Write-Host "  (wslc logs $script:Container did not finish within 30s)" }
        Remove-Job $logs -Force
    }
    catch { Write-Host "  (wslc logs $script:Container failed: $_)" }

    # wsl.exe writes UTF-16LE while wslc.exe writes UTF-8, so reading wsl.exe the ordinary way
    # prints "W`0S`0L`0 ...". Stating the encoding for this one child covers it without
    # touching [Console]::OutputEncoding, which governs how *every* other command here is read
    # and, under a host started without a console, may govern nothing at all.
    try {
        $status = [System.Diagnostics.ProcessStartInfo]::new('wsl.exe')
        $status.ArgumentList.Add('--status')
        $status.UseShellExecute = $false
        $status.RedirectStandardOutput = $true
        $status.StandardOutputEncoding = [System.Text.Encoding]::Unicode

        $reader = [System.Diagnostics.Process]::Start($status)
        Write-Host $reader.StandardOutput.ReadToEnd().TrimEnd()
        $reader.WaitForExit()
    }
    catch { Write-Host "  (wsl --status failed: $_)" }
}

function Remove-Leftovers {
    # `wip down` is the command under test, so cleanup cannot rely on it having worked --
    # this removes the container directly, and says nothing when there was none.
    #
    # Behind a job with a deadline, and with everything swallowed, because this also runs
    # from the finally block: an unresponsive wslc there would hang the run until the job
    # timeout, and a throw there would replace the failure that got us there with its own.
    try {
        $removal = Start-Job -ScriptBlock { param($exe, $name) & $exe remove -f $name 2>&1 } `
            -ArgumentList $Wslc, $script:Container
        if (-not (Wait-Job $removal -Timeout 60)) {
            Write-Host "  (wslc remove -f $script:Container did not finish within 60s; leaving it)"
        }

        Receive-Job $removal -ErrorAction SilentlyContinue | Out-Null
        Remove-Job $removal -Force
    }
    catch { }
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
    Assert-Match $config $script:ContainerPattern "wip config"

    # ------------------------------------------------------------------ build
    Write-Step "wip build"
    $build = Invoke-Wip @('build')
    Assert-Exit $build 0 "wip build"

    # ------------------------------------------------------------------ up
    Write-Step "wip up -d"
    $up = Invoke-Wip @('up', '-d')
    Assert-Exit $up 0 "wip up -d"

    # wslc's own view first, state and all. It runs ahead of `wip ps` deliberately: if the
    # container really is not running, that is the container failing and this line says so,
    # and only once wslc has been made to agree does a disagreeing `wip ps` mean wip is wrong.
    #
    # It is the JSON probe that is asserted, not the table: State there is the field wip's own
    # status and create-vs-start decisions parse, so this assertion covers what wip reads. The
    # table's STATUS column is prose for a person -- 2.9.9 wrote "running Less than a second
    # ago" and 2.9.10 writes docker's "Up Less than a second", which names no state at all --
    # and a lifecycle assertion has no business resting on which phrasing is current.
    Write-Step "wslc's own view of the container"
    $probe = Invoke-Wslc @('list', '--all', '--filter', "name=$($script:Container)", '--format', 'json')
    Assert-Exit $probe 0 "wslc list --format json after wip up"
    Assert-Match $probe $script:ContainerPattern "wslc list --format json after wip up"
    Assert-State $probe 'running' "wslc list --format json after wip up"

    # The table too, unasserted beyond the name: it is the view a person reading this log has,
    # and it is where a renamed column or a dropped row shows up at a glance.
    $listed = Invoke-Wslc @('list', '--all')
    Assert-Match $listed $script:ContainerPattern "wslc list --all after wip up"

    # The state column, not just the name: `wip ps` exits 0 and prints the row whatever the
    # container's state is, so matching the name alone would pass on a row reading
    # "wip-e2e-app  not found" -- which is exactly what wip printed here against the container
    # wslc just reported as running.
    $ps = Invoke-Wip @('ps')
    Assert-Exit $ps 0 "wip ps"
    Assert-Match $ps "(?m)^$($script:ContainerPattern)\s+running\b" "wip ps after wip up -d"

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

    # `remove: true` in the fixture means the ephemeral container is `wslc run --rm`, so
    # counting rows built from the fixture image is what actually proves it was removed --
    # the long-lived one from `wip up` should be the only one left. Matching the name alone
    # would pass with the ephemeral container still sitting there beside it.
    $afterRun = Invoke-Wslc @('list', '--all')
    Assert-Match $afterRun $script:ContainerPattern "wslc list --all after wip run"
    $fixtureRows = ([regex]::Matches($afterRun.Output, 'wip-e2e:latest')).Count
    if ($fixtureRows -ne 1) {
        throw "expected exactly one wip-e2e:latest container after wip run, found $fixtureRows -- did --rm not remove the ephemeral one?"
    }

    # ------------------------------------------------------------------ down
    Write-Step "wip down"
    $down = Invoke-Wip @('down')
    Assert-Exit $down 0 "wip down"

    $afterDown = Invoke-Wslc @('list', '--all')
    Assert-NoMatch $afterDown $script:ContainerPattern "wslc list --all after wip down"

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
