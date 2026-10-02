# Dot-source from run-e2e.ps1; all data and sandboxes are dedicated temporary fixtures.
# Invoke-Wip/Invoke-Wslc and assertion helpers belong to the caller.
function Invoke-SandboxMountE2E([string] $Namespace, [string] $Image) {
    $sandboxes = New-Object 'Collections.Generic.List[string]'

    try {
        Write-Step 'sandbox mounts declared volumes with shared and isolated storage'

        # 1. Create first sandbox with persistent and ephemeral volume mounts
        $sandboxes.Add('mount-shared-first')
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'mount-shared-first')) 0 'create mount-shared-first'
        Assert-Match (Invoke-Wip @('sandbox', 'status', 'mount-shared-first')) 'mount-shared-first\s+running' 'mount-shared-first is running'

        # Write data to both mounts in first sandbox
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'mount-shared-first', '--', 'sh', '-c', 'printf persistent-shared-data > /data/persistent.txt')) 0 'write persistent data in first sandbox'
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'mount-shared-first', '--', 'sh', '-c', 'printf ephemeral-shared-data > /scratch/ephemeral.txt')) 0 'write ephemeral data in first sandbox'

        # 2. Create second sandbox sharing the two volumes plus an isolated volume
        $sandboxes.Add('mount-shared-second')
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'mount-shared-second')) 0 'create mount-shared-second'
        Assert-Match (Invoke-Wip @('sandbox', 'status', 'mount-shared-second')) 'mount-shared-second\s+running' 'mount-shared-second is running'

        # Verify shared data is visible in second sandbox
        $readPersistent = Invoke-Wip @('sandbox', 'exec', 'mount-shared-second', '--', 'cat', '/data/persistent.txt')
        Assert-Exit $readPersistent 0 'read shared persistent data in second sandbox'
        Assert-Match $readPersistent 'persistent-shared-data' 'shared persistent data matches'

        $readEphemeral = Invoke-Wip @('sandbox', 'exec', 'mount-shared-second', '--', 'cat', '/scratch/ephemeral.txt')
        Assert-Exit $readEphemeral 0 'read shared ephemeral data in second sandbox'
        Assert-Match $readEphemeral 'ephemeral-shared-data' 'shared ephemeral data matches'

        # Write to isolated volume in second sandbox and verify absence in first sandbox
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'mount-shared-second', '--', 'sh', '-c', 'printf isolated-data > /isolated/private.txt')) 0 'write isolated data in second sandbox'
        Assert-NonZero (Invoke-Wip @('sandbox', 'exec', 'mount-shared-first', '--', 'sh', '-c', 'test -f /isolated/private.txt')) 'isolated storage is absent from first sandbox'

        # 3. Destroy first sandbox; second sandbox and shared storage remain intact
        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', 'mount-shared-first')) 0 'destroy mount-shared-first'
        [void]$sandboxes.Remove('mount-shared-first')
        Assert-Match (Invoke-Wip @('sandbox', 'status', 'mount-shared-first')) 'mount-shared-first\s+not found' 'first sandbox is absent'
        Assert-Match (Invoke-Wip @('sandbox', 'status', 'mount-shared-second')) 'mount-shared-second\s+running' 'second sandbox still running'

        # Verify storage remains accessible in second sandbox while it holds references
        $readStillRunning = Invoke-Wip @('sandbox', 'exec', 'mount-shared-second', '--', 'cat', '/scratch/ephemeral.txt')
        Assert-Exit $readStillRunning 0 'read ephemeral data while second sandbox still holds reference'
        Assert-Match $readStillRunning 'ephemeral-shared-data' 'ephemeral data retained during partial sandbox destroy'

        # 4. Destroy second sandbox; ephemeral storage is reconciled, persistent storage retained
        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', 'mount-shared-second')) 0 'destroy mount-shared-second'
        [void]$sandboxes.Remove('mount-shared-second')
        Assert-Match (Invoke-Wip @('sandbox', 'status', 'mount-shared-second')) 'mount-shared-second\s+not found' 'second sandbox is absent'

        Assert-Match (Invoke-Wip @('volume', 'status', 'ephemeral-fixture')) 'not found' 'ephemeral fixture cleaned up after last detach'
        Assert-Match (Invoke-Wip @('volume', 'status', 'isolated-fixture')) 'not found' 'isolated fixture cleaned up after last detach'
        Assert-Match (Invoke-Wip @('volume', 'status', 'persistent-fixture')) 'exists\s+persistent' 'persistent fixture retained after all sandboxes destroyed'

        # 5. Recreate first sandbox; persistent data survives, ephemeral storage starts fresh
        $sandboxes.Add('mount-shared-first')
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'mount-shared-first')) 0 'recreate mount-shared-first'
        $readRecreated = Invoke-Wip @('sandbox', 'exec', 'mount-shared-first', '--', 'cat', '/data/persistent.txt')
        Assert-Exit $readRecreated 0 'read persistent data after sandbox recreation'
        Assert-Match $readRecreated 'persistent-shared-data' 'persistent data survived sandbox recreation'

        Assert-NonZero (Invoke-Wip @('sandbox', 'exec', 'mount-shared-first', '--', 'sh', '-c', 'test -f /scratch/ephemeral.txt')) 'ephemeral scratch starts fresh on recreation'

        # 6. Final cleanup
        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', 'mount-shared-first')) 0 'cleanup recreated first sandbox'
        [void]$sandboxes.Remove('mount-shared-first')
        Assert-Exit (Invoke-Wip @('volume', 'destroy', 'persistent-fixture')) 0 'explicit persistent volume cleanup'
        Assert-Match (Invoke-Wip @('volume', 'status', 'persistent-fixture')) 'not found' 'persistent volume destroyed'

        Write-Step 'All sandbox volume mount assertions passed'
    }
    finally {
        foreach ($sb in @($sandboxes.ToArray())) {
            try {
                $cleanup = Invoke-Wip @('sandbox', 'destroy', $sb)
                if ($cleanup.Code -ne 0) { $script:Failed = $true; Write-Warning "sandbox cleanup failed ($sb): $($cleanup.Output)" }
            }
            catch { $script:Failed = $true; Write-Warning "sandbox cleanup failed ($sb): $_" }
        }
        foreach ($vol in @('persistent-fixture', 'ephemeral-fixture', 'isolated-fixture')) {
            try {
                [void](Invoke-Wip @('volume', 'destroy', $vol))
            }
            catch { }
        }
    }
}
