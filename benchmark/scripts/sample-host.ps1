# Samples host-wide CPU/memory (and a process-name pattern's summed working set) once per
# interval for DurationSec, appending rows to CsvPath. Run detached by Start-HostSampler in
# common.ps1; intended to be launched, not dot-sourced.
param(
    [Parameter(Mandatory = $true)][string]$CsvPath,
    [Parameter(Mandatory = $true)][string]$Config,
    [Parameter(Mandatory = $true)][string]$Phase,
    [Parameter(Mandatory = $true)][int]$Round,
    [Parameter(Mandatory = $true)][double]$DurationSec,
    [double]$IntervalSec = 1.0,
    [string]$ProcessPattern = 'Docker Desktop|com\.docker|wslservice|wslcsession|wslrelay|vmmem|vmwp|wslhost'
)

$ErrorActionPreference = 'SilentlyContinue'

$csvDir = Split-Path -Parent $CsvPath
if (-not (Test-Path $csvDir)) { New-Item -ItemType Directory -Path $csvDir -Force | Out-Null }
$writeHeader = -not (Test-Path $CsvPath)
if ($writeHeader) {
    'timestamp,config,phase,round,cpu_pct,mem_used_mb,mem_free_mb,related_process_ws_mb,related_process_count' |
        Out-File -FilePath $CsvPath -Encoding utf8 -Append
}

$counterPath = '\Processor(_Total)\% Processor Time'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $DurationSec) {
    $ts = (Get-Date).ToString('o')

    $cpu = $null
    try {
        $cpu = (Get-Counter -Counter $counterPath -ErrorAction Stop).CounterSamples[0].CookedValue
    } catch {}

    $os = Get-CimInstance Win32_OperatingSystem
    $memFreeMb = [math]::Round($os.FreePhysicalMemory / 1024, 1)
    $memTotalMb = [math]::Round($os.TotalVisibleMemorySize / 1024, 1)
    $memUsedMb = [math]::Round($memTotalMb - $memFreeMb, 1)

    $procs = Get-Process | Where-Object { $_.ProcessName -match $ProcessPattern }
    $relatedWsMb = [math]::Round((($procs | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
    $relatedCount = ($procs | Measure-Object).Count

    $cpuStr = if ($null -ne $cpu) { [math]::Round($cpu, 2) } else { '' }
    "$ts,$Config,$Phase,$Round,$cpuStr,$memUsedMb,$memFreeMb,$relatedWsMb,$relatedCount" |
        Out-File -FilePath $CsvPath -Encoding utf8 -Append

    Start-Sleep -Milliseconds ([int]($IntervalSec * 1000))
}
