<#
.SYNOPSIS
  Runs one or more timed rounds of the wip/WSLC vs Docker Desktop benchmark for one or more
  of the four configs (windows-docker, windows-wip, wsl-docker, wsl-wip) and appends results
  to results.csv / samples.csv under -OutDir. See ..\..\.claude\skills\wip-benchmark\SKILL.md
  for the full protocol this implements.

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
    [Parameter(Mandatory = $true)][string]$OutDir
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$resultsCsv = Join-Path $OutDir 'results.csv'
$samplesCsv = Join-Path $OutDir 'samples.csv'
$logPath = Join-Path $OutDir 'run.log'

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

function Get-WslAppDir {
    # Same NTFS-backed directory as $AppDir, referenced via the WSL /mnt/c path so both
    # windows-* and wsl-* configs read/write identical files in one physical location.
    $full = (Resolve-Path $AppDir).Path
    $drive = $full.Substring(0, 1).ToLower()
    $rest = $full.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}
$wslAppDir = Get-WslAppDir

$resultsHeaderNeeded = -not (Test-Path $resultsCsv)
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
if ($resultsHeaderNeeded) {
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
            return Invoke-TimedWsl -Distro $WslDistro -BashCommand "cd '$wslAppDir' && docker run -d --name wip-bench -p 18080:3000 wip-bench:latest"
        }
        'wsl-wip' {
            return Invoke-TimedWsl -Distro $WslDistro -BashCommand "cd '$wslAppDir' && wip.exe up -d"
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
            return Invoke-TimedWsl -Distro $WslDistro -BashCommand "cd '$wslAppDir' && wip.exe down"
        }
    }
}

function Get-Backend { param([string]$Config) if ($Config -match 'docker$') { 'docker' } else { 'wip' } }

function Start-Infra {
    param([string]$Backend)
    if ($Backend -eq 'docker') { return Start-DockerDesktopInfra } else { return Start-WslcInfra }
}

function Stop-Infra {
    param([string]$Backend)
    if ($Backend -eq 'docker') { return Stop-DockerDesktopInfra } else { return Stop-WslcInfra }
}

function Get-ProcessPattern { param([string]$Backend)
    if ($Backend -eq 'docker') { 'Docker Desktop|com\.docker' } else { 'wslservice|wslcsession|wslrelay|vmmem|vmwp|wslhost' }
}

# Build the (round, config) schedule, randomizing config order within each round per the
# protocol ("実行順をラウンドごとに変え、順序を記録する").
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
    Stop-DockerDesktopInfra | Out-Null
    Stop-WslcInfra | Out-Null

    Write-Log "sampling baseline for $BaselineWaitSec s"
    $p = Start-HostSampler -CsvPath $samplesCsv -Config $cfg -Phase 'baseline' -Round $step.Round -DurationSec $BaselineWaitSec -ProcessPattern $procPattern
    Start-Sleep -Seconds ($BaselineWaitSec + 1)
    $p | Wait-Process -ErrorAction SilentlyContinue

    Write-Log "starting infra ($backend)"
    $infra = Start-Infra $backend
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

    $ready = Wait-HttpReady -Url $AppUrl -TimeoutSec 60
    $row.app_ready_ok = $ready.Ready
    $row.app_ready_elapsed_ms = [math]::Round($ready.ElapsedMs, 1)
    if (-not $ready.Ready) { $row.notes += 'app never became http-ready; ' }

    if ($ready.Ready) {
        Write-Log "sampling app-idle (no load) for $IdleWaitSec s"
        $p = Start-HostSampler -CsvPath $samplesCsv -Config $cfg -Phase 'app_idle' -Round $step.Round -DurationSec $IdleWaitSec -ProcessPattern $procPattern
        Start-Sleep -Seconds ($IdleWaitSec + 1)
        $p | Wait-Process -ErrorAction SilentlyContinue

        Write-Log "running load for $LoadDurationSec s at concurrency $LoadConcurrency"
        $p = Start-HostSampler -CsvPath $samplesCsv -Config $cfg -Phase 'load' -Round $step.Round -DurationSec ($LoadDurationSec + 2) -ProcessPattern $procPattern
        $loadOutPath = Join-Path $OutDir "load-$cfg-r$($step.Round)-w$($step.IsWarmup).json"
        $loadProc = Invoke-TimedProcess -FilePath 'node' -ArgumentList @(
            (Join-Path $PSScriptRoot 'load-gen.mjs'), '--url', $AppUrl, '--concurrency', $LoadConcurrency, '--duration', $LoadDurationSec, '--out', $loadOutPath
        )
        $p | Wait-Process -ErrorAction SilentlyContinue

        if (Test-Path $loadOutPath) {
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
            $row.notes += 'load-gen produced no output; '
        }
    }

    Write-Log 'stopping app'
    $stop = Stop-App $cfg
    $row.app_stop_ok = ($stop.ExitCode -eq 0)
    $row.app_stop_elapsed_ms = [math]::Round($stop.ElapsedMs, 1)
    if ($stop.ExitCode -ne 0) { $row.notes += "app stop failed: $($stop.StdErr); " }

    Add-ResultRow -Row $row
    Write-Log "row written: config=$cfg round=$($step.Round) warmup=$($step.IsWarmup)"
}

Write-Log 'stopping both backends after run'
Stop-DockerDesktopInfra | Out-Null
Stop-WslcInfra | Out-Null
Write-Log 'done'
