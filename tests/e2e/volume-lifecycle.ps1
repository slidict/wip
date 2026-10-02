# Dot-source from run-e2e.ps1; all data and consumers are dedicated temporary fixtures.
# Invoke-Wip/Invoke-Wslc and assertion helpers belong to the caller.
function Invoke-VolumeStorageE2E([string] $Namespace, [string] $Image) {
    $consumers = New-Object 'Collections.Generic.List[string]'

    function Backend-Volume([string] $LogicalName) {
        $result = Invoke-Wip @('volume', 'status', $LogicalName)
        Assert-Exit $result 0 "volume status $LogicalName"
        $match = [regex]::Match($result.Output, '\bwip-v-[a-f0-9]{24}-[a-f0-9]{32}\b')
        if (-not $match.Success) { throw "no owned backend generation for $LogicalName" }
        return $match.Value
    }

    function Start-VolumeConsumer([string] $Name, [string] $BackendName) {
        $consumers.Add($Name)
        $result = Invoke-Wslc @('run', '--name', $Name, '-d', '--label', "io.slidict.wip.e2e=$Namespace",
            '--mount', "type=volume,source=$BackendName,target=/data", $Image, 'sleep', '600')
        Assert-Exit $result 0 "start dedicated volume consumer $Name"
    }

    function Remove-VolumeConsumer([string] $Name) {
        $listed = Invoke-Wslc @('list', '--all', '--filter', "name=$Name", '--format', 'json')
        Assert-Exit $listed 0 'list dedicated consumer before cleanup'
        $rows = @(Read-WslcRecords $listed.Output)
        if ($rows.Count -eq 0) { [void]$consumers.Remove($Name); return }
        $inspection = Invoke-Wslc @('inspect', '--type', 'container', '--format', 'json', $Name)
        Assert-Exit $inspection 0 'inspect dedicated consumer before cleanup'
        $records = @($inspection.Output | ConvertFrom-Json)
        if ($records.Count -ne 1 -or $records[0].Name.TrimStart('/') -ne $Name -or
            $records[0].Labels.'io.slidict.wip.e2e' -ne $Namespace -or
            [string]::IsNullOrWhiteSpace($records[0].Id) -or $records[0].Id.StartsWith('-')) {
            throw 'dedicated consumer identity/ownership mismatch; cleanup refused'
        }
        Assert-Exit (Invoke-Wslc @('remove', '-f', $records[0].Id)) 0 'remove verified dedicated consumer'
        [void]$consumers.Remove($Name)
    }

    try {
        Write-Step 'persistent volume survives container recreation'
        Assert-Exit (Invoke-Wip @('volume', 'create', 'persistent-fixture')) 0 'volume create'
        $persistent = Backend-Volume 'persistent-fixture'
        Assert-Exit (Invoke-Wip @('volume', 'create', 'persistent-fixture')) 0 'volume reuse'
        if ((Backend-Volume 'persistent-fixture') -ne $persistent) { throw 'reuse changed persistent generation' }
        $first = "$Namespace-p-first"
        Start-VolumeConsumer $first $persistent
        Assert-Exit (Invoke-Wslc @('exec', $first, 'sh', '-c', 'printf retained-data > /data/marker')) 0 'write temporary persistent data'
        Assert-NonZero (Invoke-Wip @('volume', 'destroy', 'persistent-fixture')) 'in-use persistent deletion'
        Remove-VolumeConsumer $first
        Assert-Exit (Invoke-Wip @('volume', 'reconcile', 'persistent-fixture')) 0 'persistent detach reconciliation'
        Assert-Exit (Invoke-Wip @('volume', 'create', 'persistent-fixture')) 0 'persistent reuse after recreation'
        $second = "$Namespace-p-second"
        Start-VolumeConsumer $second $persistent
        $read = Invoke-Wslc @('exec', $second, 'cat', '/data/marker')
        Assert-Exit $read 0 'persistent data read after container recreation'
        Assert-Match $read 'retained-data' 'persistent marker survived'
        Remove-VolumeConsumer $second
        Assert-Exit (Invoke-Wip @('volume', 'destroy', 'persistent-fixture')) 0 'explicit persistent deletion'
        Assert-Exit (Invoke-Wip @('volume', 'destroy', 'persistent-fixture')) 0 'repeated persistent deletion'

        Write-Step 'ephemeral storage waits for the final confirmed detach'
        Assert-Exit (Invoke-Wip @('volume', 'create', 'ephemeral-fixture')) 0 'ephemeral create'
        $ephemeral = Backend-Volume 'ephemeral-fixture'
        Assert-Exit (Invoke-Wip @('volume', 'reconcile', 'ephemeral-fixture')) 0 'unused ephemeral retained before attach'
        if ((Backend-Volume 'ephemeral-fixture') -ne $ephemeral) { throw 'unused ephemeral was deleted' }
        $one = "$Namespace-e-one"; $two = "$Namespace-e-two"
        Start-VolumeConsumer $one $ephemeral
        Start-VolumeConsumer $two $ephemeral
        Assert-Exit (Invoke-Wslc @('exec', $one, 'sh', '-c', 'printf cycle-data > /data/marker')) 0 'write temporary ephemeral data'
        Assert-Match (Invoke-Wip @('volume', 'status', 'ephemeral-fixture')) 'references=2' 'two volume references'
        Assert-NonZero (Invoke-Wip @('volume', 'destroy', 'ephemeral-fixture')) 'multiple-reference deletion protection'
        Assert-Exit (Invoke-Wip @('volume', 'reconcile', 'ephemeral-fixture')) 0 'observe ephemeral usage durably'
        Remove-VolumeConsumer $one
        Assert-Exit (Invoke-Wip @('volume', 'reconcile', 'ephemeral-fixture')) 0 'retain last reference'
        Assert-Match (Invoke-Wip @('volume', 'status', 'ephemeral-fixture')) 'references=1' 'remaining volume reference'
        Remove-VolumeConsumer $two
        Assert-Exit (Invoke-Wip @('volume', 'reconcile', 'ephemeral-fixture')) 0 'ephemeral final detach cleanup'
        Assert-Match (Invoke-Wip @('volume', 'status', 'ephemeral-fixture')) 'not found' 'ephemeral absent after cleanup'
        Assert-Exit (Invoke-Wip @('volume', 'create', 'ephemeral-fixture')) 0 'new ephemeral cycle'
        $newGeneration = Backend-Volume 'ephemeral-fixture'
        if ($newGeneration -eq $ephemeral) { throw 'new ephemeral cycle reused deleted generation name' }
        $fresh = "$Namespace-e-fresh"
        Start-VolumeConsumer $fresh $newGeneration
        Assert-NonZero (Invoke-Wslc @('exec', $fresh, 'cat', '/data/marker')) 'new ephemeral cycle has no old data'
        Remove-VolumeConsumer $fresh
        Assert-Exit (Invoke-Wip @('volume', 'destroy', 'ephemeral-fixture')) 0 'explicit unused-cycle cleanup'
        Write-Step 'All volume storage assertions passed'
    }
    finally {
        foreach ($consumer in @($consumers.ToArray())) {
            try { Remove-VolumeConsumer $consumer }
            catch { $script:Failed = $true; Write-Warning "dedicated volume consumer cleanup failed: $_" }
        }
        foreach ($logicalName in @('persistent-fixture', 'ephemeral-fixture')) {
            try {
                $cleanup = Invoke-Wip @('volume', 'destroy', $logicalName)
                if ($cleanup.Code -ne 0) { $script:Failed = $true; Write-Warning "volume cleanup failed: $($cleanup.Output)" }
            }
            catch { $script:Failed = $true; Write-Warning "volume cleanup failed: $_" }
        }
    }
}
