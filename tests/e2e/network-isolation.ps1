# Dot-source from run-e2e.ps1; Invoke-Wip/Invoke-Wslc and the assertion helpers belong to the
# caller. Every network and sandbox here is a dedicated temporary fixture of this run.
#
# The unit suite proves wip refuses to start a container it cannot put behind a verified
# boundary. It cannot prove the boundary blocks anything, because a fake backend has no
# packets. That is what this file is for: a scan sandbox on a declared internal network, and
# assertions that the one allowed path works while every forbidden one does not.
# $Image is not taken: the sandboxes come from wip.yml's own declarations, unlike the volume
# and mount fixtures that run throwaway containers of their own.
function Invoke-NetworkIsolationE2E([string] $Namespace) {
    $sandboxes = New-Object 'Collections.Generic.List[string]'
    $squatted = $null

    # Every wip invocation here goes through a deadline, and the reason is measured: on
    # 2026-10-24 this fixture hung the e2e job for the full 45-minute timeout, and because the
    # job was killed mid-step GitHub kept no log at all -- so there was nothing to say where
    # it stopped. A caller's `& $exe ... | Out-String` waits for the output stream to close,
    # not for the child to exit, so a container-side process holding that handle waits
    # forever. A deadline turns that into a failure that names the command, inside a job that
    # completes and therefore keeps its log.
    #
    # The invocation itself stays `& $exe @argv`, the way the caller's Invoke-Capture does it,
    # because that is what preserves argv: Start-Process -ArgumentList re-joins the arguments
    # into one command line with its own quoting, which tears apart the `sh -c '<script>'`
    # probes (measured: the listener came back as exit 127, the shell's "not found"). Running
    # it inside a job is what makes it killable -- the same Start-Job/Wait-Job/Remove-Job
    # shape run-e2e.ps1 already uses for cleanup and for `wslc logs`, and for the same reason.
    function Invoke-Bounded([string] $Exe, [string[]] $Arguments, [int] $TimeoutSeconds = 180) {
        $job = Start-Job -ScriptBlock {
            param($exe, $argv, $cwd)
            Set-Location $cwd
            $text = & $exe @argv 2>&1 | Out-String
            [pscustomobject]@{ Code = $LASTEXITCODE; Output = $text }
        } -ArgumentList $Exe, $Arguments, $script:Workspace

        try {
            if (-not (Wait-Job $job -Timeout $TimeoutSeconds)) {
                throw "timed out after ${TimeoutSeconds}s: $Exe $($Arguments -join ' ')"
            }
            # -ErrorAction SilentlyContinue for the reason the three Start-Job sites in
            # run-e2e.ps1 use it: a job runspace inherits neither this script's
            # $ErrorActionPreference nor its $PSNativeCommandUseErrorActionPreference = $false,
            # and under pwsh 7.4+ that preference defaults to $true -- so a probe that is
            # *expected* to exit non-zero can arrive as an error record and be re-raised here
            # as a terminating error, instead of as the status the assertion reads. CI has not
            # hit it (the blocked probes pass today), but the assertions depend on exit codes,
            # not on exceptions.
            $result = Receive-Job $job -ErrorAction SilentlyContinue
            $code = $result.Code
            $output = [string]$result.Output
        }
        finally {
            # In the finally so a throw above -- deadline or re-raised error -- does not leak
            # the job.
            Remove-Job $job -Force -ErrorAction SilentlyContinue
        }

        Write-Host "`$ $Exe $($Arguments -join ' ')  -> exit $code"
        if ($output.Trim()) { Write-Host $output.TrimEnd() }
        return [pscustomobject]@{ Code = $code; Output = $output }
    }

    function Invoke-WipHere([string[]] $Arguments, [int] $TimeoutSeconds = 180) {
        return Invoke-Bounded -Exe $script:WipPath -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
    }

    function Invoke-WslcHere([string[]] $Arguments, [int] $TimeoutSeconds = 120) {
        return Invoke-Bounded -Exe $script:WslcPath -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
    }

    # The address of a sandbox's own interface, read from inside it. Everything here is
    # addressed by IP: container-name resolution is absent on an internal network in the WSLC
    # measured for this test (2026-10-24), so a name-based check would fail for a reason that
    # has nothing to do with isolation.
    function Get-SandboxAddress([string] $Sandbox) {
        $shown = Invoke-WipHere @('sandbox', 'exec', $Sandbox, '--', 'ip', '-4', '-o', 'addr', 'show', 'eth0')
        Assert-Exit $shown 0 "read $Sandbox's address"
        $match = [regex]::Match($shown.Output, 'inet (\d+\.\d+\.\d+\.\d+)')
        if (-not $match.Success) { throw "cannot read $Sandbox's IPv4 address from: $($shown.Output.Trim())" }
        return $match.Groups[1].Value
    }

    # A shell test inside the sandbox, asserted on its exit status alone. Written as one `sh
    # -c` so the probe's own non-zero status is what wip forwards -- which is the whole
    # assertion for a blocked path.
    function Assert-Reachable([string] $Sandbox, [string] $Probe, [string] $What) {
        Assert-Exit (Invoke-WipHere @('sandbox', 'exec', $Sandbox, '--', 'sh', '-c', $Probe)) 0 "$What is reachable"
    }

    # BusyBox's answer when an applet or an option does not exist. A probe that never ran
    # exits non-zero too, so without this a missing applet would read as a blocked network
    # and the assertion would pass having tested nothing.
    # `: not found` is the shell's form (`sh: nslookup: not found`) rather than a bare "not
    # found", which a resolver's own answer can contain; `(?m)` is needed because PowerShell's
    # -match anchors ^ to the start of the whole string otherwise.
    $unsupported = 'unrecognized option|invalid option|applet not found|: not found|(?m)^Usage:|BusyBox v'

    function Assert-Blocked([string] $Sandbox, [string] $Probe, [string] $What) {
        $result = Invoke-WipHere @('sandbox', 'exec', $Sandbox, '--', 'sh', '-c', $Probe)
        if ($result.Output -match $unsupported) {
            throw "$What was not tested: the probe itself is unsupported -- $($result.Output.Trim())"
        }
        Assert-NonZero $result "$What is blocked"
    }

    # A probe asserted to work, so that the forbidden ones failing means something. Run
    # against the sandbox's own loopback or the local filesystem, which an internal network
    # does not touch either way.
    function Assert-ProbeWorks([string] $Sandbox, [string] $Probe, [string] $What) {
        $result = Invoke-WipHere @('sandbox', 'exec', $Sandbox, '--', 'sh', '-c', $Probe)
        if ($result.Output -match $unsupported) {
            throw "$What is unsupported in this image, so the assertions built on it would test nothing -- $($result.Output.Trim())"
        }
        Assert-Exit $result 0 "$What works"
    }

    try {
        Write-Step 'security scan sandboxes run behind a verified internal network'

        $sandboxes.Add('scan-target')
        Assert-Exit (Invoke-WipHere @('sandbox', 'create', 'scan-target')) 0 'create scan-target'
        $sandboxes.Add('scan-scanner')
        Assert-Exit (Invoke-WipHere @('sandbox', 'create', 'scan-scanner')) 0 'create scan-scanner'
        $sandboxes.Add('scan-outsider')
        Assert-Exit (Invoke-WipHere @('sandbox', 'create', 'scan-outsider')) 0 'create scan-outsider'

        # ------------------------------------------------------------ the boundary itself
        #
        # wip's own view is not evidence here: what matters is that the network wslc holds is
        # internal and labelled as wip's, because `--internal` is the entire mechanism.
        $networks = Invoke-WslcHere @('network', 'list', '--format', 'json')
        Assert-Exit $networks 0 'wslc network list'
        $owned = Read-WslcRecords $networks.Output |
            Where-Object { $_.Labels -match [regex]::Escape("io.slidict.wip.owner=v1:${Namespace}:network:scan-isolated") }
        if (-not $owned) { throw "no network labelled for this run's scan-isolated: $($networks.Output.Trim())" }
        if (@($owned).Count -ne 1) { throw "expected exactly one scan-isolated network, found $(@($owned).Count)" }
        $isolatedName = @($owned)[0].Name
        if ($isolatedName -notmatch '^wip-n-[a-f0-9]{40}$') { throw "unexpected network name: $isolatedName" }

        $inspected = Invoke-WslcHere @('network', 'inspect', '--format', 'json', $isolatedName)
        Assert-Exit $inspected 0 'wslc network inspect'
        $record = @(Read-WslcRecords $inspected.Output)[0]
        if ($record.Internal -ne $true) { throw "$isolatedName is not internal; the boundary does not exist" }

        # The two sandboxes on the declared network must be on it and nothing else: a second
        # attachment would be a second, unrestricted route out, and the network being internal
        # would say nothing about it.
        foreach ($sandbox in 'scan-scanner', 'scan-target') {
            $status = Invoke-WipHere @('sandbox', 'status', $sandbox)
            Assert-Exit $status 0 "status of $sandbox"
            $identity = [regex]::Match($status.Output, "(?m)^$([regex]::Escape($sandbox))\s+running\s+wip-s-[a-f0-9]+\s+(\S+)\s*$")
            if (-not $identity.Success) { throw "cannot read $sandbox's container ID from: $($status.Output.Trim())" }
            $container = Invoke-WslcHere @('inspect', '--type', 'container', '--format', 'json', $identity.Groups[1].Value)
            Assert-Exit $container 0 "inspect $sandbox's container"
            $attached = @(Read-WslcRecords $container.Output)[0].NetworkSettings.Networks.PSObject.Properties.Name
            if (@($attached).Count -ne 1 -or @($attached)[0] -ne $isolatedName) {
                throw "$sandbox is attached to [$(@($attached) -join ', ')], expected only $isolatedName"
            }
        }

        # ------------------------------------------------------------ the allowed path
        $targetAddress = Get-SandboxAddress 'scan-target'
        $outsiderAddress = Get-SandboxAddress 'scan-outsider'
        if ($targetAddress -eq $outsiderAddress) { throw 'the two networks handed out the same address' }

        # The listener outlives the exec that started it, which is what makes a TCP assertion
        # possible at all: `wip sandbox exec` returns when its own command does, and busybox
        # nc backgrounded here keeps accepting until the connection below arrives.
        # Exit 0 only says the shell ran: a backgrounded nc reports nothing about itself, so
        # whether the listener is really there is established by the reachability assertion
        # below, not here.
        Assert-Exit (Invoke-WipHere @('sandbox', 'exec', 'scan-target', '--', 'sh', '-c',
                'nohup nc -l -p 9090 -e /bin/echo scan-target-reached </dev/null >/dev/null 2>&1 & echo listening')) 0 `
            'ask scan-target to start a listener'

        Assert-Reachable 'scan-scanner' "ping -c 1 -W 2 $targetAddress" 'the declared target over ICMP'
        Assert-Reachable 'scan-scanner' "nc -z -w 3 $targetAddress 9090" 'the declared target port'

        # ------------------------------------------------------------ the probes themselves
        #
        # Asserted before anything is called blocked. ICMP and TCP are already proven by the
        # allowed path above; these are the ones whose absence would otherwise be read as a
        # block. Measured 2026-10-24: `nc -u -z` returns non-zero even to the sandbox's own
        # loopback, so UDP reachability is not something it can report at all -- which is why
        # egress to a resolver is probed below with nslookup against that resolver instead.
        Assert-ProbeWorks 'scan-scanner' 'ping -c 1 -W 2 127.0.0.1' 'ping'
        Assert-ProbeWorks 'scan-scanner' 'command -v nc' 'nc'
        Assert-ProbeWorks 'scan-scanner' 'command -v nslookup' 'nslookup'

        # ------------------------------------------------------------ the forbidden paths
        #
        # The list #687 names. Each is one probe whose success would be the leak.
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 1.1.1.1 443' 'an internet IPv4 address over TCP'
        Assert-Blocked 'scan-scanner' 'ping -c 1 -W 2 1.1.1.1' 'an internet IPv4 address over ICMP'
        # Egress to a public resolver, as a query actually addressed to it. Verified to
        # discriminate: this exits 0 from a container on the default bridge and non-zero here.
        Assert-Blocked 'scan-scanner' 'nslookup example.com 8.8.8.8' 'a public DNS resolver'
        # Name resolution through whatever resolver the sandbox was given, separately from
        # reaching one: a scanner handed a hostname must not be able to turn it into an
        # address it could then be redirected to.
        Assert-Blocked 'scan-scanner' 'nslookup example.com' 'DNS resolution'

        # IPv6 is asserted for whichever reason actually holds. The fixture image has no IPv6
        # address on this WSLC (measured 2026-10-24), and a `ping -6` that fails for want of a
        # stack is not evidence that a network blocked anything -- so the absence is asserted
        # directly, and the reachability assertion runs only once there is an address to
        # reach from.
        $ipv6 = Invoke-WipHere @('sandbox', 'exec', 'scan-scanner', '--', 'sh', '-c', 'ip -6 -o addr show scope global')
        Assert-Exit $ipv6 0 'read the sandbox IPv6 addresses'
        if ($ipv6.Output.Trim()) {
            Assert-Blocked 'scan-scanner' 'ping -6 -c 1 -W 2 2606:4700:4700::1111' 'an internet IPv6 address'
        }
        else {
            Write-Host '  IPv6: the sandbox holds no global IPv6 address, so there is no IPv6 egress to block'
        }
        # The gateway of the backend's default bridge -- the host-facing route a container on
        # the ordinary network has. Being off that network is what must make it unreachable.
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 172.17.0.1 22' "the default bridge's host gateway"
        Assert-Blocked 'scan-scanner' 'ping -c 1 -W 2 172.17.0.1' "the default bridge's host gateway over ICMP"
        # The link-local metadata address every cloud runner answers on.
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 169.254.169.254 80' 'the cloud metadata endpoint'
        # A sandbox of this same run on its own internal network. This is the assertion that a
        # parallel scan cannot reach the one beside it.
        Assert-Blocked 'scan-scanner' "ping -c 1 -W 2 $outsiderAddress" 'a sandbox on another internal network'
        # The TCP half needs something to refuse it, or it would fail merely because no
        # process listens on that port and would pass even if cross-network TCP were allowed.
        # The control runs after the blocked probe on purpose: `nc -l` without `-k` serves one
        # connection and exits, so a control placed first would consume the listener.
        Assert-Exit (Invoke-WipHere @('sandbox', 'exec', 'scan-outsider', '--', 'sh', '-c',
                'nohup nc -l -p 9090 -e /bin/echo scan-outsider-reached </dev/null >/dev/null 2>&1 & echo listening')) 0 `
            'start the outsider listener'
        Assert-Blocked 'scan-scanner' "nc -z -w 3 $outsiderAddress 9090" 'a sandbox on another internal network over TCP'
        Assert-Reachable 'scan-outsider' "nc -z -w 3 $outsiderAddress 9090" 'the outsider listener from its own network'

        # ------------------------------------------------------------ isolation survives a resume
        Assert-Exit (Invoke-WipHere @('sandbox', 'stop', 'scan-scanner')) 0 'stop scan-scanner'
        Assert-Exit (Invoke-WipHere @('sandbox', 'create', 'scan-scanner')) 0 'resume scan-scanner'
        Assert-Match (Invoke-WipHere @('sandbox', 'status', 'scan-scanner')) 'scan-scanner\s+running' 'scan-scanner resumed'
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 1.1.1.1 443' 'an internet IPv4 address after a resume'
        Assert-Blocked 'scan-scanner' 'nslookup example.com 8.8.8.8' 'a public DNS resolver after a resume'
        Assert-Blocked 'scan-scanner' 'nslookup example.com' 'DNS resolution after a resume'
        Assert-Reachable 'scan-scanner' "ping -c 1 -W 2 $targetAddress" 'the declared target after a resume'

        # ------------------------------------------------------------ fail closed
        #
        # Isolation that cannot be verified must stop the operation rather than fall back to
        # the default bridge. Forced by putting a non-internal network under the exact name
        # wip derives, so wip finds one it may neither adopt nor replace.
        Write-Step 'a sandbox whose boundary cannot be verified is not started'
        # A stopped sandbox still holds the shared network, which is the case `network
        # inspect` cannot answer -- it lists only running containers, and WSLC removes the
        # network regardless. The stopped container would then be unstartable for good, its
        # attachment pinned to the removed network's id.
        Assert-Exit (Invoke-WipHere @('sandbox', 'stop', 'scan-target')) 0 'stop scan-target'
        Assert-Exit (Invoke-WipHere @('sandbox', 'destroy', 'scan-scanner')) 0 'destroy scan-scanner while scan-target is stopped'
        [void]$sandboxes.Remove('scan-scanner')
        Assert-Match (Invoke-WslcHere @('network', 'list')) ([regex]::Escape($isolatedName)) `
            'the shared network survives while a stopped sandbox is attached'
        # And the stopped sandbox can still be resumed, which is what the kept network buys.
        Assert-Exit (Invoke-WipHere @('sandbox', 'create', 'scan-target')) 0 'resume scan-target after the scanner was destroyed'
        Assert-Match (Invoke-WipHere @('sandbox', 'status', 'scan-target')) 'scan-target\s+running' 'scan-target resumed'

        Assert-Exit (Invoke-WipHere @('sandbox', 'destroy', 'scan-target')) 0 'destroy scan-target'
        [void]$sandboxes.Remove('scan-target')
        Assert-NoMatch (Invoke-WslcHere @('network', 'list')) ([regex]::Escape($isolatedName)) `
            'destroying the last sandbox removed its network'

        $squatted = $isolatedName
        Assert-Exit (Invoke-WslcHere @('network', 'create', $isolatedName)) 0 'create a non-internal network under the derived name'
        $refused = Invoke-WipHere @('sandbox', 'create', 'scan-scanner')
        Assert-NonZero $refused 'sandbox create against an unverifiable boundary'
        Assert-Match (Invoke-WipHere @('sandbox', 'status', 'scan-scanner')) 'scan-scanner\s+not found' `
            'no container was started without a verified boundary'
        # And the squatted network is still there: wip reports it rather than deleting a
        # network it could not establish ownership of.
        Assert-Match (Invoke-WslcHere @('network', 'list')) ([regex]::Escape($isolatedName)) `
            'an unowned network is left alone'
    }
    finally {
        # Warnings rather than throws: this also runs on the way out of a failure, and a
        # cleanup problem must be visible without replacing the failure that got us here.
        foreach ($sandbox in $sandboxes) {
            try {
                $cleanup = Invoke-WipHere @('sandbox', 'destroy', $sandbox)
                if ($cleanup.Code -ne 0) { Write-Warning "network fixture cleanup failed (${sandbox}): $($cleanup.Output.Trim())" }
            }
            catch { Write-Warning "network fixture cleanup failed (${sandbox}): $_" }
        }
        if ($squatted) {
            # wip refused to touch this one, so the fixture removes what it created itself.
            try {
                $cleanup = Invoke-WslcHere @('network', 'remove', $squatted)
                if ($cleanup.Code -ne 0) { Write-Warning "squatted network was left behind (${squatted}): $($cleanup.Output.Trim())" }
            }
            catch { Write-Warning "squatted network was left behind (${squatted}): $_" }
        }
    }
}
