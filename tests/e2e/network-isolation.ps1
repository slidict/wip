# Dot-source from run-e2e.ps1; Invoke-Wip/Invoke-Wslc and the assertion helpers belong to the
# caller. Every network and sandbox here is a dedicated temporary fixture of this run.
#
# The unit suite proves wip refuses to start a container it cannot put behind a verified
# boundary. It cannot prove the boundary blocks anything, because a fake backend has no
# packets. That is what this file is for: a scan sandbox on a declared internal network, and
# assertions that the one allowed path works while every forbidden one does not.
function Invoke-NetworkIsolationE2E([string] $Namespace, [string] $Image) {
    $sandboxes = New-Object 'Collections.Generic.List[string]'
    $squatted = $null

    # The address of a sandbox's own interface, read from inside it. Everything here is
    # addressed by IP: container-name resolution is absent on an internal network in the WSLC
    # measured for this test (2026-10-24), so a name-based check would fail for a reason that
    # has nothing to do with isolation.
    function Get-SandboxAddress([string] $Sandbox) {
        $shown = Invoke-Wip @('sandbox', 'exec', $Sandbox, '--', 'ip', '-4', '-o', 'addr', 'show', 'eth0')
        Assert-Exit $shown 0 "read $Sandbox's address"
        $match = [regex]::Match($shown.Output, 'inet (\d+\.\d+\.\d+\.\d+)')
        if (-not $match.Success) { throw "cannot read $Sandbox's IPv4 address from: $($shown.Output.Trim())" }
        return $match.Groups[1].Value
    }

    # A shell test inside the sandbox, asserted on its exit status alone. Written as one `sh
    # -c` so the probe's own non-zero status is what wip forwards -- which is the whole
    # assertion for a blocked path.
    function Assert-Reachable([string] $Sandbox, [string] $Probe, [string] $What) {
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', $Sandbox, '--', 'sh', '-c', $Probe)) 0 "$What is reachable"
    }

    function Assert-Blocked([string] $Sandbox, [string] $Probe, [string] $What) {
        Assert-NonZero (Invoke-Wip @('sandbox', 'exec', $Sandbox, '--', 'sh', '-c', $Probe)) "$What is blocked"
    }

    try {
        Write-Step 'security scan sandboxes run behind a verified internal network'

        $sandboxes.Add('scan-target')
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'scan-target')) 0 'create scan-target'
        $sandboxes.Add('scan-scanner')
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'scan-scanner')) 0 'create scan-scanner'
        $sandboxes.Add('scan-outsider')
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'scan-outsider')) 0 'create scan-outsider'

        # ------------------------------------------------------------ the boundary itself
        #
        # wip's own view is not evidence here: what matters is that the network wslc holds is
        # internal and labelled as wip's, because `--internal` is the entire mechanism.
        $networks = Invoke-Wslc @('network', 'list', '--format', 'json')
        Assert-Exit $networks 0 'wslc network list'
        $owned = Read-WslcRecords $networks.Output |
            Where-Object { $_.Labels -match [regex]::Escape("io.slidict.wip.owner=v1:${Namespace}:network:scan-isolated") }
        if (-not $owned) { throw "no network labelled for this run's scan-isolated: $($networks.Output.Trim())" }
        if (@($owned).Count -ne 1) { throw "expected exactly one scan-isolated network, found $(@($owned).Count)" }
        $isolatedName = @($owned)[0].Name
        if ($isolatedName -notmatch '^wip-n-[a-f0-9]{40}$') { throw "unexpected network name: $isolatedName" }

        $inspected = Invoke-Wslc @('network', 'inspect', '--format', 'json', $isolatedName)
        Assert-Exit $inspected 0 'wslc network inspect'
        $record = @(Read-WslcRecords $inspected.Output)[0]
        if ($record.Internal -ne $true) { throw "$isolatedName is not internal; the boundary does not exist" }

        # The two sandboxes on the declared network must be on it and nothing else: a second
        # attachment would be a second, unrestricted route out, and the network being internal
        # would say nothing about it.
        foreach ($sandbox in 'scan-scanner', 'scan-target') {
            $status = Invoke-Wip @('sandbox', 'status', $sandbox)
            Assert-Exit $status 0 "status of $sandbox"
            $identity = [regex]::Match($status.Output, "(?m)^$([regex]::Escape($sandbox))\s+running\s+wip-s-[a-f0-9]+\s+(\S+)\s*$")
            if (-not $identity.Success) { throw "cannot read $sandbox's container ID from: $($status.Output.Trim())" }
            $container = Invoke-Wslc @('inspect', '--type', 'container', '--format', 'json', $identity.Groups[1].Value)
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
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', 'scan-target', '--', 'sh', '-c',
                'nohup nc -l -p 9090 -e /bin/echo scan-target-reached >/dev/null 2>&1 & echo listening')) 0 `
            'start the target listener'

        Assert-Reachable 'scan-scanner' "ping -c 1 -W 2 $targetAddress" 'the declared target over ICMP'
        Assert-Reachable 'scan-scanner' "nc -z -w 3 $targetAddress 9090" 'the declared target port'

        # ------------------------------------------------------------ the forbidden paths
        #
        # The list #687 names. Each is one probe whose success would be the leak.
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 1.1.1.1 443' 'an internet IPv4 address over TCP'
        Assert-Blocked 'scan-scanner' 'ping -c 1 -W 2 1.1.1.1' 'an internet IPv4 address over ICMP'
        Assert-Blocked 'scan-scanner' 'ping -6 -c 1 -W 2 2606:4700:4700::1111' 'an internet IPv6 address'
        Assert-Blocked 'scan-scanner' 'nc -u -z -w 2 8.8.8.8 53' 'a public DNS resolver'
        # Name resolution, separately from reaching a resolver: a scanner handed a hostname
        # must not be able to turn it into an address it could then be redirected to.
        Assert-Blocked 'scan-scanner' 'nslookup example.com' 'DNS resolution'
        # The gateway of the backend's default bridge -- the host-facing route a container on
        # the ordinary network has. Being off that network is what must make it unreachable.
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 172.17.0.1 22' "the default bridge's host gateway"
        Assert-Blocked 'scan-scanner' 'ping -c 1 -W 2 172.17.0.1' "the default bridge's host gateway over ICMP"
        # The link-local metadata address every cloud runner answers on.
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 169.254.169.254 80' 'the cloud metadata endpoint'
        # A sandbox of this same run on its own internal network. This is the assertion that a
        # parallel scan cannot reach the one beside it.
        Assert-Blocked 'scan-scanner' "ping -c 1 -W 2 $outsiderAddress" 'a sandbox on another internal network'
        Assert-Blocked 'scan-scanner' "nc -z -w 3 $outsiderAddress 9090" 'a sandbox on another internal network over TCP'

        # ------------------------------------------------------------ isolation survives a resume
        Assert-Exit (Invoke-Wip @('sandbox', 'stop', 'scan-scanner')) 0 'stop scan-scanner'
        Assert-Exit (Invoke-Wip @('sandbox', 'create', 'scan-scanner')) 0 'resume scan-scanner'
        Assert-Match (Invoke-Wip @('sandbox', 'status', 'scan-scanner')) 'scan-scanner\s+running' 'scan-scanner resumed'
        Assert-Blocked 'scan-scanner' 'nc -z -w 3 1.1.1.1 443' 'an internet IPv4 address after a resume'
        Assert-Blocked 'scan-scanner' 'nslookup example.com' 'DNS resolution after a resume'
        Assert-Reachable 'scan-scanner' "ping -c 1 -W 2 $targetAddress" 'the declared target after a resume'

        # ------------------------------------------------------------ fail closed
        #
        # Isolation that cannot be verified must stop the operation rather than fall back to
        # the default bridge. Forced by putting a non-internal network under the exact name
        # wip derives, so wip finds one it may neither adopt nor replace.
        Write-Step 'a sandbox whose boundary cannot be verified is not started'
        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', 'scan-scanner')) 0 'destroy scan-scanner'
        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', 'scan-target')) 0 'destroy scan-target'
        [void]$sandboxes.Remove('scan-scanner')
        [void]$sandboxes.Remove('scan-target')
        Assert-NoMatch (Invoke-Wslc @('network', 'list')) ([regex]::Escape($isolatedName)) `
            'destroying the last sandbox removed its network'

        $squatted = $isolatedName
        Assert-Exit (Invoke-Wslc @('network', 'create', $isolatedName)) 0 'create a non-internal network under the derived name'
        $refused = Invoke-Wip @('sandbox', 'create', 'scan-scanner')
        Assert-NonZero $refused 'sandbox create against an unverifiable boundary'
        Assert-Match (Invoke-Wip @('sandbox', 'status', 'scan-scanner')) 'scan-scanner\s+not found' `
            'no container was started without a verified boundary'
        # And the squatted network is still there: wip reports it rather than deleting a
        # network it could not establish ownership of.
        Assert-Match (Invoke-Wslc @('network', 'list')) ([regex]::Escape($isolatedName)) `
            'an unowned network is left alone'
    }
    finally {
        foreach ($sandbox in $sandboxes) {
            try { Invoke-Wip @('sandbox', 'destroy', $sandbox) | Out-Null } catch { }
        }
        if ($squatted) {
            # wip refused to touch this one, so the fixture removes what it created itself.
            try { Invoke-Wslc @('network', 'remove', $squatted) | Out-Null } catch { }
        }
    }
}
