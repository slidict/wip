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
    [string]$ProcessPattern = 'Docker Desktop|com\.docker|wslservice|wslcsession|wslrelay|wslhost',
    # Reported as its own column rather than folded into $ProcessPattern's sum: a VM's
    # Working Set can represent memory shared with/inside the guest, so adding it to ordinary
    # process working sets double-counts rather than producing a meaningful total.
    [string]$VmProcessPattern = 'vmmem|vmwp'
)

$ErrorActionPreference = 'SilentlyContinue'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Format-Invariant {
    param($Value)
    if ($null -eq $Value -or $Value -eq '') { return '' }
    return [Convert]::ToString([double]$Value, $inv)
}

$csvDir = Split-Path -Parent $CsvPath
if (-not (Test-Path $csvDir)) { New-Item -ItemType Directory -Path $csvDir -Force | Out-Null }
$writeHeader = -not (Test-Path $CsvPath)
if ($writeHeader) {
    'timestamp,config,phase,round,cpu_pct,mem_used_mb,mem_free_mb,related_process_ws_mb,related_process_count,related_vm_ws_mb,related_vm_count' |
        Out-File -FilePath $CsvPath -Encoding utf8 -Append
}

$counterPath = '\Processor(_Total)\% Processor Time'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $DurationSec) {
    $iterationStart = $sw.Elapsed
    $ts = (Get-Date).ToString('o')

    $cpu = $null
    try {
        $cpu = (Get-Counter -Counter $counterPath -ErrorAction Stop).CounterSamples[0].CookedValue
    } catch {}

    $os = Get-CimInstance Win32_OperatingSystem
    if ($null -ne $os) {
        $memFreeMb = [math]::Round($os.FreePhysicalMemory / 1024, 1)
        $memTotalMb = [math]::Round($os.TotalVisibleMemorySize / 1024, 1)
        $memUsedMb = [math]::Round($memTotalMb - $memFreeMb, 1)
    } else {
        # A failed CIM query is not "0 MB used" — leave both blank rather than pulling the
        # average toward zero with a value that was never actually measured.
        $memFreeMb = ''
        $memUsedMb = ''
    }

    $allProcs = Get-Process
    $procs = $allProcs | Where-Object { $_.ProcessName -match $ProcessPattern }
    $vmProcs = $allProcs | Where-Object { $_.ProcessName -match $VmProcessPattern }
    $relatedWsMb = [math]::Round((($procs | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
    $relatedCount = ($procs | Measure-Object).Count
    $vmWsMb = [math]::Round((($vmProcs | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
    $vmCount = ($vmProcs | Measure-Object).Count

    $cpuStr = if ($null -ne $cpu) { Format-Invariant ([math]::Round($cpu, 2)) } else { '' }
    $row = @(
        $ts, $Config, $Phase, $Round, $cpuStr,
        (Format-Invariant $memUsedMb), (Format-Invariant $memFreeMb),
        (Format-Invariant $relatedWsMb), $relatedCount,
        (Format-Invariant $vmWsMb), $vmCount
    ) -join ','
    $row | Out-File -FilePath $CsvPath -Encoding utf8 -Append

    # Sleep only what's left of the interval — the counter/CIM/process-enumeration work above
    # takes real time too, and sleeping the full interval on top of it under-samples a
    # nominally ~1s series (observed ~2s apart in practice before this fix).
    $elapsedThisIteration = ($sw.Elapsed - $iterationStart).TotalMilliseconds
    $remainingMs = ($IntervalSec * 1000) - $elapsedThisIteration
    if ($remainingMs -gt 0) { Start-Sleep -Milliseconds ([int]$remainingMs) }
}
