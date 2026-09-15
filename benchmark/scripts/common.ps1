# Shared helpers for the wip vs Docker Desktop benchmark. Dot-source this from
# Invoke-Benchmark.ps1 or an interactive session: `. .\common.ps1`

$ErrorActionPreference = 'Continue'

if (-not ('WipBench.Power' -as [type])) {
    Add-Type -Namespace WipBench -Name Power -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@
}

# A benchmark round can run unattended for minutes; Windows Modern Standby has been observed
# TWICE to suspend/kill this script's own background process mid-run with no exception logged
# (root-caused live via Kernel-Power event 507/566 landing within a second of each
# disappearance). The first attempt at a fix used only ES_SYSTEM_REQUIRED and still let the
# machine suspend once more — Modern Standby on this class of laptop appears to key off the
# *display* idle timer, not just the system one, so ES_DISPLAY_REQUIRED is included too. Even
# with this, treat unattended long runs as best-effort: keep the laptop plugged in, the lid
# open, and check on a long run periodically rather than trusting this alone.
# ES_CONTINUOUS keeps the state in effect until explicitly reset or the process exits;
# Reset-SystemAwake does the former so this doesn't leave the machine unable to sleep after the
# script ends normally.
# PowerShell's hex literal parser treats 0x80000000 as Int32 (which overflows to negative)
# before any cast runs, so [uint32]0x80000000 fails; parsing the hex digits directly avoids that.
$WipBenchEsContinuous = [Convert]::ToUInt32('80000000', 16)
$WipBenchEsSystemRequired = [Convert]::ToUInt32('00000001', 16)
$WipBenchEsDisplayRequired = [Convert]::ToUInt32('00000002', 16)

function Set-SystemAwake {
    [WipBench.Power]::SetThreadExecutionState($WipBenchEsContinuous -bor $WipBenchEsSystemRequired -bor $WipBenchEsDisplayRequired) | Out-Null
}

function Reset-SystemAwake {
    [WipBench.Power]::SetThreadExecutionState($WipBenchEsContinuous) | Out-Null
}

function Wait-HttpReady {
    param(
        [string]$Url,
        [double]$TimeoutSec = 60,
        [double]$PollIntervalSec = 0.2,
        # Without this, any 2xx response counts as "ready" — including an unrelated service
        # that happens to already own the port (a leftover container, something else on the
        # host). Checking for a substring unique to this benchmark's app catches that case
        # instead of silently benchmarking the wrong listener.
        [string]$ExpectedBodyContains = 'wip-bench'
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2
            if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300 -and
                (-not $ExpectedBodyContains -or $resp.Content -match [regex]::Escape($ExpectedBodyContains))) {
                $sw.Stop()
                return [pscustomobject]@{ Ready = $true; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
            }
        } catch {
            Start-Sleep -Milliseconds ([int]($PollIntervalSec * 1000))
        }
    }
    $sw.Stop()
    return [pscustomobject]@{ Ready = $false; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Test-DockerReady {
    docker info 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Get-DockerContextName {
    $name = (docker context show 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $name) { return $null }
    return $name.Trim()
}

function Assert-LocalDockerContext {
    # docker info/run always target whatever context is current. If that context points at a
    # remote or otherwise non-Docker-Desktop engine, every timing/load number in this run would
    # describe that other engine while the "infra start" phase still only starts/stops the
    # local Docker Desktop process — silently comparing the wrong things.
    param([string]$ExpectedContext = 'desktop-linux')
    $ctx = Get-DockerContextName
    if ($ctx -and $ctx -ne $ExpectedContext) {
        throw "Refusing to run: docker context is '$ctx', not the expected local Docker Desktop context '$ExpectedContext'. Run ``docker context use $ExpectedContext`` first, or pass a different -ExpectedContext if that's genuinely the local engine on this machine."
    }
}

function Wait-DockerState {
    param([bool]$WantReady, [double]$TimeoutSec = 120)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if ((Test-DockerReady) -eq $WantReady) {
            $sw.Stop()
            return [pscustomobject]@{ Reached = $true; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
        }
        Start-Sleep -Seconds 2
    }
    $sw.Stop()
    return [pscustomobject]@{ Reached = $false; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Start-DockerDesktopInfra {
    param([double]$TimeoutSec = 180)
    $exe = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Process -FilePath $exe | Out-Null
    $result = Wait-DockerState -WantReady $true -TimeoutSec $TimeoutSec
    $sw.Stop()
    if ($result.Reached) { Assert-LocalDockerContext }
    return [pscustomobject]@{
        Ready       = $result.Reached
        ElapsedMs   = $sw.Elapsed.TotalMilliseconds
    }
}

function Assert-NoForeignDockerContainers {
    # This benchmark repeatedly force-cycles Docker Desktop to reach a clean baseline. A
    # *running* container it did not create (name != wip-bench) would be hard-killed by that,
    # not stopped gracefully — restart policies then bring it back up in a crash-recovered state
    # each round. Refuse rather than repeat that incident silently; see
    # Stop-DockerDesktopInfra's comment and the pilot report for what happened the one time this
    # wasn't checked. Deliberately `docker ps` (running only), not `-a`: an already-stopped
    # container isn't at risk from cycling Docker Desktop, so it isn't a reason to refuse.
    param([string]$OwnedContainerName = 'wip-bench')
    if (-not (Test-DockerReady)) { return }
    $names = (docker ps --format '{{.Names}}' 2>$null) | Where-Object { $_ -and $_ -ne $OwnedContainerName }
    if ($names) {
        throw "Refusing to run: Docker Desktop already has running container(s) this benchmark did not create ($($names -join ', ')). This benchmark force-cycles Docker Desktop every round, which would hard-kill them. Stop them yourself first, then re-run."
    }
}

function Stop-DockerDesktopInfra {
    param([double]$TimeoutSec = 120)
    # Checked every time, not just once up front: something other than this benchmark could
    # start a container between rounds. See Assert-NoForeignDockerContainers for why this
    # matters — it throws rather than silently force-killing someone else's container.
    Assert-NoForeignDockerContainers
    # Ask nicely first (CloseMainWindow — Docker Desktop's own quit path stops containers
    # gracefully before tearing down the VM) and only escalate to a hard kill if it ignores
    # the request. A force-kill here bypasses container shutdown entirely, which can hard-kill
    # anything still running under it (see: this script's own incident with a user's
    # unrelated project containers, restarted-and-crash-recovered by repeated force kills).
    $guiProcs = Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue
    if ($guiProcs) {
        foreach ($proc in $guiProcs) { [void]$proc.CloseMainWindow() }
        $graceSw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($graceSw.Elapsed.TotalSeconds -lt 45 -and (Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 2
        }
    }
    Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name 'com.docker.backend' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name 'com.docker.build' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($distro in @('docker-desktop', 'docker-desktop-data')) {
        wsl --terminate $distro 2>$null | Out-Null
    }
    $result = Wait-DockerState -WantReady $false -TimeoutSec $TimeoutSec
    # Normalized to .Stopped (matching Stop-WslcInfra) rather than Wait-DockerState's own
    # .Reached, so callers can check both backends' stop result the same way.
    return [pscustomobject]@{ Stopped = $result.Reached; ElapsedMs = $result.ElapsedMs }
}

function Get-WslcSessionCount {
    # Returns $null (not 0) when the query itself failed, so callers can tell "confirmed no
    # sessions" apart from "couldn't tell" instead of treating a suppressed error as a clean
    # baseline.
    $out = wslc system session list 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    if (-not $out) { return 0 }
    # Header line + one line per session; header always present when the command succeeds.
    $lines = $out | Where-Object { $_.Trim().Length -gt 0 }
    return [Math]::Max(0, $lines.Count - 1)
}

function Start-WslcInfra {
    # A single successful `wslc list` does not prove the backend is actually up — WSLC has no
    # documented explicit "start" command, so this polls the same probe on a timeout like
    # Wait-DockerState does for Docker Desktop, instead of trusting one call. (A bare `wslc
    # list` that races a session still initializing would otherwise report Ready=$false forever
    # rather than retrying — TimeoutSec used to be accepted but silently ignored.)
    param([double]$TimeoutSec = 120)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = $false
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        wslc list 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Get-WslcSessionCount) -ge 0) { $ok = $true; break }
        Start-Sleep -Milliseconds 500
    }
    $sw.Stop()
    return [pscustomobject]@{ Ready = $ok; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Assert-NoForeignWslcContainers {
    # Mirrors Assert-NoForeignDockerContainers: `wslc system session terminate` resets the
    # entire WSLC session, not just this benchmark's own container, and this benchmark calls it
    # every round. WSLC sessions are not per-project, so anything else running under one would
    # be torn down right alongside the benchmark's own container.
    param([string]$OwnedContainerName = 'wip-bench')
    $count = Get-WslcSessionCount
    if ($null -eq $count -or $count -eq 0) { return }
    $lines = (wslc list 2>$null) | Select-Object -Skip 1 | Where-Object { $_.Trim().Length -gt 0 }
    $foreign = $lines | Where-Object { $_ -notmatch [regex]::Escape($OwnedContainerName) }
    if ($foreign) {
        throw "Refusing to run: an existing WSLC session already has container(s) this benchmark did not create. This benchmark terminates the whole WSLC session every round (wslc system session terminate), which resets every project sharing it. Stop/save them yourself first, then re-run."
    }
}

function Stop-WslcInfra {
    param([double]$TimeoutSec = 60)
    Assert-NoForeignWslcContainers
    wslc system session terminate 2>$null | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = $false
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $count = Get-WslcSessionCount
        if ($count -eq 0) { $ok = $true; break }
        Start-Sleep -Seconds 1
    }
    $sw.Stop()
    return [pscustomobject]@{ Stopped = $ok; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Format-ProcessArgument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

function Invoke-TimedProcess {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory = $null
    )
    # Windows PowerShell 5.1 runs on .NET Framework, where ProcessStartInfo.ArgumentList is
    # present but left null (never auto-instantiated the way .NET Core does), so building it
    # via .Add() silently no-ops the whole argument list. Build the classic Arguments string
    # instead — it works identically on both runtimes.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($ArgumentList | ForEach-Object { Format-ProcessArgument $_ }) -join ' ')
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    # Reading stdout to completion before touching stderr can deadlock: a child that fills the
    # stderr OS pipe buffer blocks on write before it ever closes stdout, so ReadToEnd() on
    # stdout never returns. Read both streams concurrently instead.
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()
    [System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask))
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $p.WaitForExit()
    $sw.Stop()

    return [pscustomobject]@{
        ExitCode  = $p.ExitCode
        ElapsedMs = $sw.Elapsed.TotalMilliseconds
        StdOut    = $stdout
        StdErr    = $stderr
    }
}

function Invoke-TimedWsl {
    param(
        [string]$Distro,
        [string]$BashCommand
    )
    return Invoke-TimedProcess -FilePath 'wsl' -ArgumentList @('-d', $Distro, '--', 'bash', '-lc', $BashCommand)
}

function Start-HostSampler {
    param(
        [string]$CsvPath,
        [string]$Config,
        [string]$Phase,
        [int]$Round,
        [double]$DurationSec,
        [double]$IntervalSec = 1.0,
        [string]$ProcessPattern = 'Docker Desktop|com\.docker|wslservice|wslcsession|wslrelay|wslhost',
        # Reported as a separate sampler column (related_vm_ws_mb) rather than folded into
        # $ProcessPattern's sum — see sample-host.ps1's header comment for why.
        [string]$VmProcessPattern = 'vmmem|vmwp'
    )
    $scriptPath = Join-Path $PSScriptRoot 'sample-host.ps1'
    # Start-Process -ArgumentList does not auto-quote elements containing spaces (unlike
    # ProcessStartInfo.ArgumentList on .NET Core) — an unquoted -ProcessPattern value with a
    # space (e.g. 'Docker Desktop|com\.docker') silently breaks param binding and the sampler
    # exits without writing a single row. Quote every element explicitly.
    $argList = @(
        '-NoProfile', '-NonInteractive', '-File', (Format-ProcessArgument $scriptPath),
        '-CsvPath', (Format-ProcessArgument $CsvPath),
        '-Config', (Format-ProcessArgument $Config),
        '-Phase', (Format-ProcessArgument $Phase),
        '-Round', $Round,
        '-DurationSec', $DurationSec,
        '-IntervalSec', $IntervalSec,
        '-ProcessPattern', (Format-ProcessArgument $ProcessPattern),
        '-VmProcessPattern', (Format-ProcessArgument $VmProcessPattern)
    )
    return Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -WindowStyle Hidden -PassThru
}
