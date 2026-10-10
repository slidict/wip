# Dot-source from run-e2e.ps1; use only its staged, namespaced fixture.
function Invoke-ShimPersistenceE2E {
    try {
        Write-Step 'persistent shim survives sandbox destroy and create'
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'shim-first')) 0 'create shim sandbox'

        # Keep the expected bytes outside the volume so a changed shim cannot redefine them.
        $write = 'printf "%s\n" "#!/bin/sh" "echo shim-e2e-ok" > /shim/report && chmod 755 /shim/report && cp /shim/report /tmp/shim-container-only'
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'shim-first', '--', 'sh', '-c', $write)) 0 'write executable shim and container copy'
        $before = Invoke-Wip @('sandbox', 'exec', 'shim-first', '--', 'sha256sum', '/shim/report')
        Assert-Exit $before 0 'hash original shim'
        $digest = [regex]::Match($before.Output, '(?m)^([a-f0-9]{64})\s+/shim/report\s*$')
        if (-not $digest.Success) { throw 'original shim hash missing' }
        $hashPattern = '(?m)^' + $digest.Groups[1].Value + '\s+/shim/report\s*$'
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'shim-first', '--', 'sh', '-c', 'test -f /shim/report && test -x /shim/report && cmp /shim/report /tmp/shim-container-only')) 0 'original shim and container copy are identical and executable'
        $originalRun = Invoke-Wip @('sandbox', 'exec', 'shim-first', '--', '/shim/report')
        Assert-Exit $originalRun 0 'execute original shim'
        Assert-Match $originalRun '^shim-e2e-ok\s*$' 'original shim output'

        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', 'shim-first')) 0 'destroy shim sandbox'
        $absent = Invoke-Wip @('sandbox', 'status', 'shim-first')
        Assert-Exit $absent 0 'status after shim sandbox destroy'
        Assert-Match $absent 'shim-first\s+not found' 'shim sandbox was removed'
        $volume = Invoke-Wip @('volume', 'status', 'shim-fixture')
        Assert-Exit $volume 0 'persistent shim volume status after final detach'
        Assert-Match $volume 'exists\s+persistent' 'shim volume survives final detach'

        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'shim-first')) 0 'recreate shim sandbox'
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'shim-first', '--', 'sh', '-c', 'test -f /shim/report && test -x /shim/report && test ! -e /tmp/shim-container-only')) 0 'shim exists and is executable while container copy is gone'
        $after = Invoke-Wip @('sandbox', 'exec', 'shim-first', '--', 'sha256sum', '/shim/report')
        Assert-Exit $after 0 'hash recreated sandbox shim'
        Assert-Match $after $hashPattern 'recreated sandbox shim is byte-identical'
        $run = Invoke-Wip @('sandbox', 'exec', 'shim-first', '--', '/shim/report')
        Assert-Exit $run 0 'execute shim after recreation'
        Assert-Match $run '^shim-e2e-ok\s*$' 'recreated shim output'

        Write-Step 'second sandbox mounts the same persistent shim'
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'shim-second')) 0 'create second shim sandbox'
        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', 'shim-first')) 0 'destroy first sandbox with second attached'
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'shim-second', '--', 'sh', '-c', 'test -f /shim/report && test -x /shim/report')) 0 'second sandbox retains executable shim'
        $shared = Invoke-Wip @('sandbox', 'exec', 'shim-second', '--', 'sha256sum', '/shim/report')
        Assert-Exit $shared 0 'hash second sandbox shim'
        Assert-Match $shared $hashPattern 'second sandbox shim is byte-identical'
        $sharedRun = Invoke-Wip @('sandbox', 'exec', 'shim-second', '--', '/shim/report')
        Assert-Exit $sharedRun 0 'execute shim in second sandbox'
        Assert-Match $sharedRun '^shim-e2e-ok\s*$' 'second sandbox shim output'
        Write-Step 'All shim persistence assertions passed'
    }
    finally {
        foreach ($sandbox in @('shim-first', 'shim-second')) {
            try { Assert-Exit (Invoke-Wip @('sandbox', 'destroy', $sandbox)) 0 "cleanup $sandbox" }
            catch { $script:Failed = $true; Write-Warning "shim sandbox cleanup failed: $_" }
        }
        try { Assert-Exit (Invoke-Wip @('volume', 'destroy', 'shim-fixture')) 0 'cleanup shim volume' }
        catch { $script:Failed = $true; Write-Warning "shim volume cleanup failed: $_" }
    }
}
