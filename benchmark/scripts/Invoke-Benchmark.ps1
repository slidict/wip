<#
.SYNOPSIS
  Runs one or more timed rounds of the wip/WSLC vs Docker Desktop benchmark for one or more
  of the four configs (windows-docker, windows-wip, wsl-docker, wsl-wip) and writes
  environment.json, results.csv, samples.csv, and report.md under -OutDir. See
  ..\..\.claude\skills\wip-benchmark\SKILL.md for the full protocol this implements.

.EXAMPLE
  . .\common.ps1
  .\Invoke-Benchmark.ps1 -OutDir ..\results\20260914-pilot -Configs windows-docker,windows-wip,wsl-docker,wsl-wip -Rounds 1 -Warmups 0 -LoadDurationSec 30 -IdleWaitSec 15 -BaselineWaitSec 15
#>
param(
    [string[]]$Configs = @('windows-docker', 'windows-wip', 'wsl-docker', 'wsl-wip'),
    [int]$Rounds = 3,
    [int]$Warmups = 1,
    [double]$BaselineWaitSec = 60,
    [double]$IdleWaitSec = 60,
    [double]$LoadDurationSec = 120,
    [int]$LoadConcurrency = 20,
    [string]$AppDir = (Join-Path $PSScriptRoot '..\app' | Resolve-Path).Path,
    [string]$WslDistro = 'Ubuntu',
    # Not 'localhost': on this host it resolves ::1 first, and IPv6 loopback traffic to the
    # published port hangs instead of connecting or failing fast (root-caused during the pilot
    # run — TCP succeeds but HTTP over 'localhost' times out, while 127.0.0.1 answers instantly).
    [string]$AppUrl = 'http://127.0.0.1:18080/',
    # Skips the docker/wip image build preflight. Only for repeat runs against images already
    # confirmed present (e.g. iterating on load parameters) — a clean checkout needs the build.
    [switch]$SkipBuild,
    [Parameter(Mandatory = $true)][string]$OutDir
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$resultsCsv = Join-Path $OutDir 'results.csv'
$samplesCsv = Join-Path $OutDir 'samples.csv'
$logPath = Join-Path $OutDir 'run.log'
$environmentJsonPath = Join-Path $OutDir 'environment.json'
$reportPath = Join-Path $OutDir 'report.md'

function Write-Log {
    param([string]$Message)
    $line = "[{0:o}] {1}" -f (Get-Date), $Message
    Write-Host $line
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            Add-Content -Path $logPath -Value $line -ErrorAction Stop
            return
        } catch {
            Start-Sleep -Milliseconds 200
        }
    }
}

function ConvertTo-WslPath {
    # Robust drive-letter conversion instead of a bare Substring assumption, and the result is
    # always shell-escaped by the caller (ConvertTo-BashSingleQuoted) rather than interpolated
    # directly into a single-quoted Bash string, where an apostrophe in the path would otherwise
    # break — or change the meaning of — the command.
    param([string]$WindowsPath)
    $full = (Resolve-Path $WindowsPath).Path
    if ($full -notmatch '^[A-Za-z]:\\') {
        throw "Cannot convert '$full' to a WSL path: expected a drive-letter path such as C:\...; UNC paths and PSDrives are not supported here."
    }
    $drive = $full.Substring(0, 1).ToLower()
    $rest = $full.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

function ConvertTo-BashSingleQuoted {
    param([string]$Value)
    # Standard POSIX single-quote escaping: close the quote, emit an escaped literal quote,
    # reopen — a single-quoted string otherwise cannot contain a literal single quote at all.
    return "'" + ($Value -replace "'", "'\''") + "'"
}

$wslAppDir = ConvertTo-WslPath $AppDir
$wslAppDirQuoted = ConvertTo-BashSingleQuoted $wslAppDir

$resultsColumns = @(
    'timestamp', 'config', 'round', 'is_warmup', 'order_index',
    'infra_start_ready', 'infra_start_elapsed_ms',
    'app_start_cmd_ok', 'app_start_cmd_elapsed_ms', 'app_ready_ok', 'app_ready_elapsed_ms',
    'app_stop_ok', 'app_stop_elapsed_ms',
    'load_concurrency', 'load_requested_duration_s', 'load_actual_duration_ms',
    'load_total_requests', 'load_success_requests', 'load_failed_requests', 'load_error_rate',
    'load_throughput_rps', 'load_latency_p50_ms', 'load_latency_p95_ms', 'load_latency_min_ms', 'load_latency_max_ms',
    'notes'
)
if (-not (Test-Path $resultsCsv)) {
    ($resultsColumns -join ',') | Out-File -FilePath $resultsCsv -Encoding utf8 -Append
}

function Add-ResultRow {
    param([hashtable]$Row)
    $values = foreach ($col in $resultsColumns) {
        $v = $Row[$col]
        if ($null -eq $v) { '' } else { ($v -replace ',', ';' -replace "`r`n", ' ' -replace "`n", ' ') }
    }
    ($values -join ',') | Out-File -FilePath $resultsCsv -Encoding utf8 -Append
}

function Start-App {
    param([string]$Config)
    switch ($Config) {
        'windows-docker' {
            return Invoke-TimedProcess -FilePath 'docker' -ArgumentList @('run', '-d', '--name', 'wip-bench', '-p', '18080:3000', 'wip-bench:latest')
        }
        'windows-wip' {
            return Invoke-TimedProcess -FilePath 'wip' -ArgumentList @('up', '-d') -WorkingDirectory $AppDir
        }
        'wsl-docker' {
            return Invoke-TimedWsl -Distro $WslDistro -BashCommand "cd $wslAppDirQuoted && docker run -d --name wip-bench -p 18080:3000 wip-bench:latest"
        }
        'wsl-wip' {
            return Invoke-TimedWsl -Distro $WslDistro -BashCommand "cd $wslAppDirQuoted && wip.exe up -d"
        }
    }
}

function Stop-App {
    param([string]$Config)
    switch ($Config) {
        'windows-docker' {
            return Invoke-TimedProcess -FilePath 'docker' -ArgumentList @('rm', '-f', 'wip-bench')
        }
        'windows-wip' {
            return Invoke-TimedProcess -FilePath 'wip' -ArgumentList @('down') -WorkingDirectory $AppDir
        }
        'wsl-docker' {
            return Invoke-TimedWsl -Distro $WslDistro -BashCommand "docker rm -f wip-bench"
        }
        'wsl-wip' {
            return Invoke-TimedWsl -Distro $WslDistro -BashCommand "cd $wslAppDirQuoted && wip.exe down"
        }
    }
}

function Get-Backend { param([string]$Config) if ($Config -match 'docker$') { 'docker' } else { 'wip' } }

function Wait-WslDockerReady {
    # Docker Desktop's WSL integration exposes /var/run/docker.sock inside the distro via its
    # own forwarder, which has been observed live to lag behind `docker info` succeeding on the
    # Windows side right after a fresh Docker Desktop start — `docker run` inside WSL then fails
    # with "Cannot connect to the Docker daemon" even though the engine itself is already up.
    # Poll from inside the distro specifically, not just the Windows side, before calling
    # wsl-docker's infra "ready".
    param([string]$Distro, [double]$TimeoutSec = 30)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $r = Invoke-TimedWsl -Distro $Distro -BashCommand 'docker info'
        if ($r.ExitCode -eq 0) { $sw.Stop(); return [pscustomobject]@{ Ready = $true; ElapsedMs = $sw.Elapsed.TotalMilliseconds } }
        Start-Sleep -Milliseconds 500
    }
    $sw.Stop()
    return [pscustomobject]@{ Ready = $false; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Start-Infra {
    param([string]$Config, [string]$Backend)
    if ($Backend -ne 'docker') { return Start-WslcInfra }
    $result = Start-DockerDesktopInfra
    if ($result.Ready -and $Config -eq 'wsl-docker') {
        $wslReady = Wait-WslDockerReady -Distro $WslDistro
        return [pscustomobject]@{ Ready = $wslReady.Ready; ElapsedMs = $result.ElapsedMs + $wslReady.ElapsedMs }
    }
    return $result
}

function Stop-Infra {
    param([string]$Backend)
    if ($Backend -eq 'docker') { return Stop-DockerDesktopInfra } else { return Stop-WslcInfra }
}

function Get-ProcessPattern { param([string]$Backend)
    if ($Backend -eq 'docker') { 'Docker Desktop|com\.docker' } else { 'wslservice|wslcsession|wslrelay|wslhost' }
}

function Invoke-Preflight {
    # Runs once, before the timed schedule, and none of it counts toward any result row:
    # - refuses to proceed if either backend already has a container this benchmark did not
    #   create, since the schedule loop below force-cycles both backends repeatedly (see
    #   common.ps1's Assert-NoForeign*Containers for the incident this guards against)
    # - builds wip-bench:latest for whichever backends are under test, so a clean checkout
    #   doesn't record spurious "app start" failures against a missing image
    param([string[]]$ConfigsInScope)
    $needsDocker = $ConfigsInScope -match 'docker$'
    $needsWip = $ConfigsInScope -match 'wip$'

    if ($needsDocker) {
        Write-Log 'preflight: checking Docker Desktop for pre-existing containers'
        if (-not (Test-DockerReady)) {
            Write-Log 'preflight: starting Docker Desktop to check'
            $infra = Start-DockerDesktopInfra -TimeoutSec 180
            if (-not $infra.Ready) { throw 'preflight: Docker Desktop did not become ready; cannot check for pre-existing containers safely.' }
        }
        Assert-NoForeignDockerContainers
    }
    if ($needsWip) {
        Write-Log 'preflight: checking WSLC for a pre-existing session/containers'
        Assert-NoForeignWslcContainers
    }

    if ($SkipBuild) {
        Write-Log 'preflight: -SkipBuild set, not building wip-bench:latest'
        return
    }

    if ($needsDocker) {
        Write-Log 'preflight: docker build -t wip-bench:latest (not counted in any timed result)'
        if (-not (Test-DockerReady)) {
            $infra = Start-DockerDesktopInfra -TimeoutSec 180
            if (-not $infra.Ready) { throw 'preflight: Docker Desktop did not become ready for the build.' }
        }
        $b = Invoke-TimedProcess -FilePath 'docker' -ArgumentList @('build', '-t', 'wip-bench:latest', $AppDir)
        if ($b.ExitCode -ne 0) { throw "preflight: docker build failed (exit $($b.ExitCode)): $($b.StdErr)" }
    }
    if ($needsWip) {
        Write-Log 'preflight: wip build (drives wslc build; not counted in any timed result)'
        $b = Invoke-TimedProcess -FilePath 'wip' -ArgumentList @('build') -WorkingDirectory $AppDir
        if ($b.ExitCode -ne 0) { throw "preflight: wip build failed (exit $($b.ExitCode)): $($b.StdErr)" }
    }
}

function New-EnvironmentJson {
    param([string]$Path)
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $cs = Get-CimInstance Win32_ComputerSystem
    $env = [ordered]@{
        capturedAt = (Get-Date).ToString('o')
        host       = [ordered]@{
            os                    = $os.Caption
            osVersion             = $os.Version
            cpu                   = $cpu.Name
            cpuCores              = $cpu.NumberOfCores
            cpuLogicalProcessors  = $cpu.NumberOfLogicalProcessors
            totalPhysicalMemoryGB = [math]::Round($cs.TotalPhysicalMemory / 1GB, 2)
        }
        wsl        = [ordered]@{ versionRaw = ((wsl --version 2>&1) -join ' | ') }
        docker     = [ordered]@{
            versionRaw = ((docker version --format '{{.Client.Version}} (client) / {{.Server.Version}} (server)' 2>&1) -join ' | ')
            context    = Get-DockerContextName
        }
        wip        = [ordered]@{ versionRaw = ((wip version 2>&1) -join ' | ') }
        image      = [ordered]@{
            # Reproducibility trail: which exact bytes this run actually benchmarked, not just
            # which tag. Compare against Dockerfile's pinned base-image digest for a full chain.
            wipRepoCommit     = ((git -C $PSScriptRoot rev-parse HEAD 2>&1) -join '')
            dockerImageId     = if ($Configs -match 'docker$') { ((docker image inspect wip-bench:latest --format '{{.Id}}' 2>&1) -join '') } else { 'not built this run' }
            wslcImageInspect  = if ($Configs -match 'wip$') { ((wslc images 2>&1) -join ' | ') } else { 'not built this run' }
        }
        benchmark  = [ordered]@{
            appDir          = $AppDir
            wslAppDir       = $wslAppDir
            appUrl          = $AppUrl
            wslDistro       = $WslDistro
            configs         = $Configs
            rounds          = $Rounds
            warmups         = $Warmups
            baselineWaitSec = $BaselineWaitSec
            idleWaitSec     = $IdleWaitSec
            loadDurationSec = $LoadDurationSec
            loadConcurrency = $LoadConcurrency
        }
    }
    $env | ConvertTo-Json -Depth 6 | Out-File -FilePath $Path -Encoding utf8
}

function Get-ColumnStat {
    param([string[]]$Values, [string]$Stat)
    $nums = $Values | Where-Object { $_ -ne '' -and $_ -ne $null } | ForEach-Object { [double]$_ }
    if (-not $nums) { return '' }
    switch ($Stat) {
        'median' { return (($nums | Sort-Object)[[math]::Floor(($nums.Count - 1) / 2)]) }
        'min' { return (($nums | Measure-Object -Minimum).Minimum) }
        'max' { return (($nums | Measure-Object -Maximum).Maximum) }
    }
}

function New-ReportMarkdown {
    # Auto-generated baseline report: every trial row plus median/min/max per numeric column
    # when more than one measured round exists. This satisfies the skill's "results.csv and
    # report.md are both deliverables of a run" requirement on its own; add narrative
    # interpretation and caveats by hand afterward — this only reports what was measured.
    param([string]$ResultsCsvPath, [string]$ReportPath, [string[]]$ConfigsInScope)
    if (-not (Test-Path $ResultsCsvPath)) { return }
    $rows = Import-Csv -Path $ResultsCsvPath
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# wip vs Docker Desktop benchmark — results')
    $lines.Add('')
    $lines.Add("Generated: $((Get-Date).ToString('o'))")
    $lines.Add('')
    $lines.Add('Auto-generated from `results.csv` by `Invoke-Benchmark.ps1`. Each row is one measured trial (warmup rounds excluded). See `samples.csv` for the CPU/memory time series and `SKILL.md` for the protocol.')
    $lines.Add('')
    $numericCols = @('infra_start_elapsed_ms', 'app_start_cmd_elapsed_ms', 'app_ready_elapsed_ms', 'app_stop_elapsed_ms', 'load_throughput_rps', 'load_error_rate', 'load_latency_p50_ms', 'load_latency_p95_ms')
    foreach ($cfg in $ConfigsInScope) {
        $cfgRows = $rows | Where-Object { $_.config -eq $cfg -and $_.is_warmup -eq 'False' }
        if (-not $cfgRows) { continue }
        $lines.Add("## $cfg")
        $lines.Add('')
        $lines.Add('| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |')
        $lines.Add('|---|---|---|---|---|---|---|---|---|---|---|')
        foreach ($r in $cfgRows) {
            $lines.Add("| $($r.round) | $($r.infra_start_elapsed_ms) | $($r.app_start_cmd_elapsed_ms) | $($r.app_ready_elapsed_ms) | $($r.app_ready_ok) | $($r.app_stop_elapsed_ms) | $($r.load_throughput_rps) | $($r.load_error_rate) | $($r.load_latency_p50_ms) | $($r.load_latency_p95_ms) | $($r.notes) |")
        }
        $roundCount = ($cfgRows | Measure-Object).Count
        if ($roundCount -gt 1) {
            $lines.Add('')
            $lines.Add('| stat | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms |')
            $lines.Add('|---|---|---|---|---|---|---|---|---|')
            foreach ($stat in @('median', 'min', 'max')) {
                $vals = $numericCols | ForEach-Object {
                    Get-ColumnStat -Values ($cfgRows | Select-Object -ExpandProperty $_) -Stat $stat
                }
                $lines.Add("| $stat | $($vals[0]) | $($vals[1]) | $($vals[2]) | $($vals[3]) | $($vals[4]) | $($vals[5]) | $($vals[6]) | $($vals[7]) |")
            }
        }
        $lines.Add('')
    }
    ($lines -join "`n") | Out-File -FilePath $ReportPath -Encoding utf8
}

$exitedCleanly = $false
Set-SystemAwake
try {
    Invoke-Preflight -ConfigsInScope $Configs
    New-EnvironmentJson -Path $environmentJsonPath
    Write-Log "environment.json written to $environmentJsonPath"

    # Build the (round, config) schedule, randomizing config order within each round per the
    # protocol ("実行順をラウンドごとに変え、順序を記録する" / "vary the execution order... each round").
    $schedule = New-Object System.Collections.Generic.List[object]
    for ($r = -$Warmups; $r -lt $Rounds; $r++) {
        $isWarmup = $r -lt 0
        $roundLabel = if ($isWarmup) { 0 } else { $r + 1 }
        $order = $Configs | Sort-Object { Get-Random }
        $idx = 0
        foreach ($cfg in $order) {
            $schedule.Add([pscustomobject]@{ Round = $roundLabel; IsWarmup = $isWarmup; Config = $cfg; OrderIndex = $idx })
            $idx++
        }
    }
    $scheduleLabels = $schedule | ForEach-Object { "$($_.Config)#r$($_.Round)$(if ($_.IsWarmup) { '(warmup)' })" }
    Write-Log "schedule: $($scheduleLabels -join ' -> ')"

    foreach ($step in $schedule) {
        $cfg = $step.Config
        $backend = Get-Backend $cfg
        $procPattern = Get-ProcessPattern $backend
        Write-Log "=== $cfg round=$($step.Round) warmup=$($step.IsWarmup) order=$($step.OrderIndex) ==="

        $row = @{
            timestamp   = (Get-Date).ToString('o')
            config      = $cfg
            round       = $step.Round
            is_warmup   = $step.IsWarmup
            order_index = $step.OrderIndex
            notes       = ''
        }

        Write-Log 'stopping both backends to reach baseline state'
        $dockerStopped = Stop-DockerDesktopInfra
        $wslcStopped = Stop-WslcInfra
        if (-not $dockerStopped.Stopped -or -not $wslcStopped.Stopped) {
            $row.notes += 'baseline could not be confirmed clean (a backend did not stop within its timeout); skipping this round unmeasured; '
            Add-ResultRow -Row $row
            Write-Log "row written (baseline unconfirmed, skipped): config=$cfg round=$($step.Round)"
            continue
        }

        Write-Log "sampling baseline for $BaselineWaitSec s"
        $p = Start-HostSampler -CsvPath $samplesCsv -Config $cfg -Phase 'baseline' -Round $step.Round -DurationSec $BaselineWaitSec -ProcessPattern $procPattern
        Start-Sleep -Seconds ($BaselineWaitSec + 1)
        $p | Wait-Process -ErrorAction SilentlyContinue

        Write-Log "starting infra ($backend)"
        $infra = Start-Infra -Config $cfg -Backend $backend
        $row.infra_start_ready = $infra.Ready
        $row.infra_start_elapsed_ms = [math]::Round($infra.ElapsedMs, 1)
        if (-not $infra.Ready) { $row.notes += 'infra failed to become ready; ' }

        Write-Log "sampling infra-idle for $IdleWaitSec s"
        $p = Start-HostSampler -CsvPath $samplesCsv -Config $cfg -Phase 'infra_idle' -Round $step.Round -DurationSec $IdleWaitSec -ProcessPattern $procPattern
        Start-Sleep -Seconds ($IdleWaitSec + 1)
        $p | Wait-Process -ErrorAction SilentlyContinue

        Write-Log 'starting app'
        $start = Start-App $cfg
        $row.app_start_cmd_ok = ($start.ExitCode -eq 0)
        $row.app_start_cmd_elapsed_ms = [math]::Round($start.ElapsedMs, 1)
        if ($start.ExitCode -ne 0) { $row.notes += "app start failed: $($start.StdErr) $($start.StdOut); " }

        # Only probe readiness (and only run the load phase) when the start command actually
        # succeeded — otherwise a leftover container or unrelated service already on port 18080
        # could answer instead, and this row would silently record someone else's app as ready.
        $ready = if ($start.ExitCode -eq 0) {
            Wait-HttpReady -Url $AppUrl -TimeoutSec 60
        } else {
            [pscustomobject]@{ Ready = $false; ElapsedMs = 0 }
        }
        $row.app_ready_ok = $ready.Ready
        $row.app_ready_elapsed_ms = [math]::Round($ready.ElapsedMs, 1)
        if ($start.ExitCode -eq 0 -and -not $ready.Ready) { $row.notes += 'app never became http-ready; ' }

        if ($ready.Ready) {
            Write-Log "sampling app-idle (no load) for $IdleWaitSec s"
            $p = Start-HostSampler -CsvPath $samplesCsv -Config $cfg -Phase 'app_idle' -Round $step.Round -DurationSec $IdleWaitSec -ProcessPattern $procPattern
            Start-Sleep -Seconds ($IdleWaitSec + 1)
            $p | Wait-Process -ErrorAction SilentlyContinue

            Write-Log "running load for $LoadDurationSec s at concurrency $LoadConcurrency"
            $p = Start-HostSampler -CsvPath $samplesCsv -Config $cfg -Phase 'load' -Round $step.Round -DurationSec ($LoadDurationSec + 2) -ProcessPattern $procPattern
            $loadOutPath = Join-Path $OutDir "load-$cfg-r$($step.Round)-w$($step.IsWarmup).json"
            # A file left by an earlier run into the same -OutDir would otherwise be read as
            # this round's result if load-gen fails before writing its own output.
            Remove-Item -Path $loadOutPath -Force -ErrorAction SilentlyContinue
            $loadProc = Invoke-TimedProcess -FilePath 'node' -ArgumentList @(
                (Join-Path $PSScriptRoot 'load-gen.mjs'), '--url', $AppUrl, '--concurrency', $LoadConcurrency, '--duration', $LoadDurationSec, '--out', $loadOutPath
            )
            $p | Wait-Process -ErrorAction SilentlyContinue

            if ($loadProc.ExitCode -ne 0) {
                $row.notes += "load-gen exited $($loadProc.ExitCode): $($loadProc.StdErr); "
            } elseif (Test-Path $loadOutPath) {
                $load = Get-Content $loadOutPath -Raw | ConvertFrom-Json
                $row.load_concurrency = $load.concurrency
                $row.load_requested_duration_s = $load.requestedDurationSec
                $row.load_actual_duration_ms = $load.actualDurationMs
                $row.load_total_requests = $load.totalRequests
                $row.load_success_requests = $load.successRequests
                $row.load_failed_requests = $load.failedRequests
                $row.load_error_rate = $load.errorRate
                $row.load_throughput_rps = $load.throughputRps
                $row.load_latency_p50_ms = $load.latencyMsMedian
                $row.load_latency_p95_ms = $load.latencyMsP95
                $row.load_latency_min_ms = $load.latencyMsMin
                $row.load_latency_max_ms = $load.latencyMsMax
            } else {
                $row.notes += 'load-gen exited 0 but produced no output; '
            }
        }

        Write-Log 'stopping app'
        $stop = Stop-App $cfg
        $row.app_stop_ok = ($stop.ExitCode -eq 0)
        $row.app_stop_elapsed_ms = [math]::Round($stop.ElapsedMs, 1)

        Add-ResultRow -Row $row
        Write-Log "row written: config=$cfg round=$($step.Round) warmup=$($step.IsWarmup)"

        if ($stop.ExitCode -ne 0 -and $start.ExitCode -eq 0) {
            # Only fatal when the app actually started: a container Stop-App then couldn't
            # remove can still own port 18080 and answer in place of the next config's app,
            # silently contaminating every measurement after this one. Recorded above; now abort
            # rather than continue on unconfirmed ground. (When $start itself already failed,
            # there was nothing for Stop-App to remove in the first place — e.g. `docker rm` on
            # a container that was never created — so that failure is expected, not dangerous,
            # and must not abort the run: this exact case happened live, from a Docker
            # Desktop/WSL-integration timing flake making `docker run` fail right after a fresh
            # Docker Desktop start.)
            throw "app stop failed for $cfg (exit $($stop.ExitCode)): $($stop.StdErr) — stopping the run rather than risk contaminating later rounds. Resolve manually (check `docker ps -a` / `wslc list` for a leftover 'wip-bench' container) before re-running."
        }
    }

    New-ReportMarkdown -ResultsCsvPath $resultsCsv -ReportPath $reportPath -ConfigsInScope $Configs
    Write-Log "report.md written to $reportPath"
    $exitedCleanly = $true
} finally {
    Write-Log "stopping both backends $(if ($exitedCleanly) { 'after run' } else { '(cleanup after early exit)' })"
    # Swallow a repeat Assert-NoForeign* here: if that is what aborted the run above, the
    # user already has the loud error and needs to resolve it by hand; failing again inside
    # this finally would only mask the original failure, not add information.
    try { Stop-DockerDesktopInfra | Out-Null } catch { Write-Log "cleanup: Stop-DockerDesktopInfra: $_" }
    try { Stop-WslcInfra | Out-Null } catch { Write-Log "cleanup: Stop-WslcInfra: $_" }
    Reset-SystemAwake
    Write-Log 'done'
}
