<#
.SYNOPSIS
  Storage phase of the wip vs Docker Desktop benchmark, kept separate from
  Invoke-Benchmark.ps1 per SKILL.md ("treat storage measurement as a separate phase"). Writes
  the same amount of data to a dedicated test volume and measures logical usage, the backend's
  virtual disk file size, and Windows drive free space at each of the four points SKILL.md
  defines: before creation, after writing, after deleting the test data, and after a
  space-reclamation attempt.

.NOTES
  Virtual disk locations were found by inspecting this host, not documented anywhere:
    Docker Desktop: %LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx (volumes/images) and
                     %LOCALAPPDATA%\Docker\wsl\main\ext4.vhdx (main distro disk)
    WSLC:           %LOCALAPPDATA%\wslc\sessions\<session-name>\storage.vhdx
  These paths may differ on another install; if not found, that measurement is recorded as
  "not measured" rather than guessed. "Actual allocated size" beyond the file's own length
  (e.g. via fsutil sparse queries) was not implemented — not confirmed reliable across NTFS
  configurations — so only the file length is recorded. VHDX compaction (Optimize-VHD) was not
  attempted: it needs the Hyper-V PowerShell module and an elevated session, neither assumed
  available here. The "after reclaim" point instead just fully stops the backend, which is the
  only reclaim mechanism this script can trigger without extra privileges — WSL2's own sparse-VHD
  auto-compaction (if enabled on this host) would act at that point, nothing else.

.EXAMPLE
  . .\common.ps1
  .\Measure-Storage.ps1 -Backend docker -OutDir ..\results\20260914-pilot
  .\Measure-Storage.ps1 -Backend wip -OutDir ..\results\20260914-pilot
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('docker', 'wip')][string]$Backend,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$DataSizeMB = 512,
    [string]$VolumeName = 'wip-bench-storage-test',
    [string]$Image = 'alpine:latest'
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$storageCsv = Join-Path $OutDir 'storage.csv'
$columns = @('backend', 'point', 'timestamp', 'vhdx_name', 'vhdx_path', 'vhdx_size_bytes', 'drive_c_free_bytes', 'logical_usage_raw', 'notes')
if (-not (Test-Path $storageCsv)) {
    ($columns -join ',') | Out-File -FilePath $storageCsv -Encoding utf8 -Append
}

function Add-StorageRow {
    param([hashtable]$Row)
    $values = foreach ($col in $columns) {
        $v = $Row[$col]
        if ($null -eq $v) { '' } else { ($v -replace ',', ';' -replace "`r`n", ' ' -replace "`n", ' ') }
    }
    ($values -join ',') | Out-File -FilePath $storageCsv -Encoding utf8 -Append
}

function Get-DockerVhdxPaths {
    $base = Join-Path $env:LOCALAPPDATA 'Docker\wsl'
    $result = [ordered]@{}
    $candidates = @{ 'docker_data.vhdx' = Join-Path $base 'disk\docker_data.vhdx'; 'ext4.vhdx (main)' = Join-Path $base 'main\ext4.vhdx' }
    foreach ($name in $candidates.Keys) {
        if (Test-Path $candidates[$name]) { $result[$name] = $candidates[$name] }
    }
    return $result
}

function Get-WslcVhdxPaths {
    $base = Join-Path $env:LOCALAPPDATA 'wslc\sessions'
    $result = [ordered]@{}
    if (Test-Path $base) {
        Get-ChildItem -Path $base -Filter 'storage.vhdx' -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $result["storage.vhdx ($($_.Directory.Name))"] = $_.FullName
        }
    }
    return $result
}

function Write-MeasurementPoint {
    param([string]$Point, [string]$LogicalUsageRaw = '', [string]$Notes = '')
    $vhdxPaths = if ($Backend -eq 'docker') { Get-DockerVhdxPaths } else { Get-WslcVhdxPaths }
    $drive = Get-PSDrive -Name C -ErrorAction SilentlyContinue
    if ($vhdxPaths.Count -eq 0) {
        Add-StorageRow -Row @{
            backend = $Backend; point = $Point; timestamp = (Get-Date).ToString('o')
            vhdx_name = ''; vhdx_path = ''; vhdx_size_bytes = ''
            drive_c_free_bytes = $drive.Free; logical_usage_raw = $LogicalUsageRaw
            notes = "not measured: no virtual disk found for backend '$Backend'; $Notes"
        }
        return
    }
    foreach ($name in $vhdxPaths.Keys) {
        $size = (Get-Item -Path $vhdxPaths[$name] -ErrorAction SilentlyContinue).Length
        Add-StorageRow -Row @{
            backend = $Backend; point = $Point; timestamp = (Get-Date).ToString('o')
            vhdx_name = $name; vhdx_path = $vhdxPaths[$name]; vhdx_size_bytes = $size
            drive_c_free_bytes = $drive.Free; logical_usage_raw = $LogicalUsageRaw; notes = $Notes
        }
    }
}

if ($Backend -eq 'docker') {
    if (-not (Test-DockerReady)) {
        $infra = Start-DockerDesktopInfra -TimeoutSec 180
        if (-not $infra.Ready) { throw 'Docker Desktop did not become ready for the storage phase.' }
    }
    Write-MeasurementPoint -Point 'before_creation'

    docker pull $Image 2>&1 | Out-Null
    docker volume create $VolumeName 2>&1 | Out-Null
    docker run --rm -v "${VolumeName}:/data" $Image sh -c "dd if=/dev/zero of=/data/testfile bs=1M count=$DataSizeMB status=none && sync" 2>&1 | Out-Null
    $logicalUsage = (docker run --rm -v "${VolumeName}:/data" $Image du -sh /data 2>&1) -join ' '
    Write-MeasurementPoint -Point 'after_write' -LogicalUsageRaw $logicalUsage

    docker volume rm $VolumeName 2>&1 | Out-Null
    Write-MeasurementPoint -Point 'after_delete'

    # Best-effort reclaim: no Optimize-VHD (needs Hyper-V module + elevation). Fully stopping
    # Docker Desktop is the only reclaim mechanism available here — see the file header comment.
    Stop-DockerDesktopInfra | Out-Null
    Write-MeasurementPoint -Point 'after_reclaim_attempt' -Notes 'reclaim = Docker Desktop fully stopped, not Optimize-VHD (needs Hyper-V module + elevation, not attempted)'
} else {
    $infra = Start-WslcInfra -TimeoutSec 120
    if (-not $infra.Ready) { throw 'WSLC did not become ready for the storage phase.' }
    Write-MeasurementPoint -Point 'before_creation'

    wslc pull $Image 2>&1 | Out-Null
    wslc volume create $VolumeName 2>&1 | Out-Null
    wslc run --rm -v "${VolumeName}:/data" $Image sh -c "dd if=/dev/zero of=/data/testfile bs=1M count=$DataSizeMB status=none && sync" 2>&1 | Out-Null
    $logicalUsage = (wslc run --rm -v "${VolumeName}:/data" $Image du -sh /data 2>&1) -join ' '
    Write-MeasurementPoint -Point 'after_write' -LogicalUsageRaw $logicalUsage

    wslc volume remove $VolumeName 2>&1 | Out-Null
    Write-MeasurementPoint -Point 'after_delete'

    Stop-WslcInfra | Out-Null
    Write-MeasurementPoint -Point 'after_reclaim_attempt' -Notes 'reclaim = WSLC session terminated, not Optimize-VHD (needs Hyper-V module + elevation, not attempted)'
}

Write-Host "storage.csv written to $storageCsv"
