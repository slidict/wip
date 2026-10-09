# Dot-source from run-e2e.ps1; the sandbox and the upstream are dedicated temporary fixtures.
# Invoke-Wip/Invoke-WipWithStdin and assertion helpers belong to the caller.
#
# `wip sandbox relay` against a real container. Herdr is not on the runner, so a stub stands
# in for it: on Windows the relay passes --upstream verbatim as the name of a local named pipe
# (ReportRelayServer.NamedPipe) and expects one request line and one reply line per
# connection, which a NamedPipeServerStream in a second pwsh can serve and record.

$script:RelayFifo = '/run/wip/report.fifo'

# The relay and the stub write while the test reads, so open with full sharing and treat a
# momentary sharing violation as "nothing yet".
function Read-RelayShared([string] $Path) {
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
    }
    catch { return '' }
}

function Wait-RelayCondition([scriptblock] $Condition, [int] $Seconds, [string] $What) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "$What did not happen within $Seconds seconds"
}

# The stub Herdr: records each request line before answering it the way Herdr does.
function Start-RelayStub([string] $PipeName, [string] $RecordPath) {
    $stubPath = Join-Path $script:Workspace 'relay-stub.ps1'
    $ready = Join-Path $script:Workspace 'relay-stub.ready'
    [IO.File]::WriteAllText($stubPath, @'
param([string] $PipeName, [string] $RecordPath, [string] $ReadyPath)
$utf8 = [Text.UTF8Encoding]::new($false)
$reply = $utf8.GetBytes('{"id":"","result":{"type":"ok"}}' + "`n")
$max = [IO.Pipes.NamedPipeServerStream]::MaxAllowedServerInstances
function New-Instance { [IO.Pipes.NamedPipeServerStream]::new($PipeName, [IO.Pipes.PipeDirection]::InOut, $max, [IO.Pipes.PipeTransmissionMode]::Byte) }
$server = New-Instance
[IO.File]::WriteAllText($ReadyPath, 'ready')
while ($true) {
    $server.WaitForConnection()
    # The next instance listens before this one is answered and closed, so a report sent
    # right after this one never finds the name gone or a closing listener.
    $next = New-Instance
    try {
        $reader = [IO.StreamReader]::new($server, $utf8, $false, 4096, $true)
        $line = $reader.ReadLine()
        if ($null -ne $line) {
            [IO.File]::AppendAllText($RecordPath, $line + "`n", $utf8)
            $server.Write($reply, 0, $reply.Length)
            $server.Flush()
            # Wait for the relay to hang up after reading the reply, so closing cannot cut
            # the reply short.
            $drain = [byte[]]::new(256)
            while ($true) {
                $read = $server.ReadAsync($drain, 0, $drain.Length)
                if (-not $read.Wait(10000) -or $read.Result -le 0) { break }
            }
        }
    }
    catch { }
    finally { $server.Dispose() }
    $server = $next
}
'@)
    $pwsh = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    # Start-Process joins its arguments with spaces, so the paths are quoted here.
    $process = Start-Process -FilePath $pwsh -NoNewWindow -PassThru `
        -ArgumentList "-NoProfile -NonInteractive -File `"$stubPath`" -PipeName $PipeName -RecordPath `"$RecordPath`" -ReadyPath `"$ready`""
    Wait-RelayCondition { Test-Path -LiteralPath $ready } 60 'stub Herdr pipe ready'
    Write-Host "stub Herdr serving pipe $PipeName (pid $($process.Id))"
    return $process
}

function Start-Relay([string] $Sandbox, [string] $PipeName, [string] $Tag) {
    $out = Join-Path $script:Workspace "relay-$Tag.out"
    $err = Join-Path $script:Workspace "relay-$Tag.err"
    $process = Start-Process -FilePath $script:WipPath -WorkingDirectory $script:Workspace -NoNewWindow -PassThru `
        -ArgumentList @('sandbox', 'relay', $Sandbox, '--upstream', $PipeName) `
        -RedirectStandardOutput $out -RedirectStandardError $err
    Write-Host "`$ wip sandbox relay $Sandbox --upstream $PipeName  (pid $($process.Id), log relay-$Tag.err)"
    return [pscustomobject]@{ Process = $process; Out = $out; Err = $err; Tag = $Tag }
}

function Get-RelayLog($Relay) { return (Read-RelayShared $Relay.Out) + (Read-RelayShared $Relay.Err) }

function Stop-Relay($Relay) {
    if (-not $Relay) { return }
    try { if (-not $Relay.Process.HasExited) { $Relay.Process.Kill($true) } } catch { }
    [void]$Relay.Process.WaitForExit(30000)
    $log = Get-RelayLog $Relay
    if ($log.Trim()) { Write-Host "relay-$($Relay.Tag) log:"; Write-Host $log.TrimEnd() }
}

# A relay that must refuse to start. Bounded: one that wrongly keeps running is killed and
# reported, never waited on.
function Assert-RelayRefuses([string] $Sandbox, [string] $PipeName, [string] $Tag, [string] $Pattern, [string] $What) {
    $relay = Start-Relay $Sandbox $PipeName $Tag
    if (-not $relay.Process.WaitForExit(60000)) {
        Stop-Relay $relay
        throw "${What}: the relay kept running instead of refusing"
    }
    $result = [pscustomobject]@{ Code = $relay.Process.ExitCode; Output = (Get-RelayLog $relay) }
    Write-Host "  -> exit $($result.Code)"
    if ($result.Output.Trim()) { Write-Host $result.Output.TrimEnd() }
    Assert-NonZero $result $What
    Assert-Match $result $Pattern $What
    return $result
}

function Get-RelayStubReports([string] $RecordPath) {
    $reports = @()
    foreach ($line in (Read-RelayShared $RecordPath) -split "`n") {
        if ($line.Trim()) { $reports += @($line | ConvertFrom-Json) }
    }
    return $reports
}

function Find-RelayReport([string] $RecordPath, [string] $Message) {
    return Get-RelayStubReports $RecordPath | Where-Object { $_.params.message -eq $Message } | Select-Object -First 1
}

# The shim exits 0 whether or not a reader was there, by design, so delivery is judged at the
# stub. Retried because the relay restarts its reader after every EOF, and a report written in
# that gap is dropped -- which is the shim's contract, not a failure.
function Send-ShimReport([string] $Sandbox, [string] $RecordPath, [string] $Message, [string] $State = 'working') {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', $Sandbox, '--', '/tmp/herdr-report', 'report-agent',
                '--agent', 'claude-code', '--state', $State, '--seq', "$attempt", '--message', $Message)) 0 "shim report $Message"
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $deadline) {
            $report = Find-RelayReport $RecordPath $Message
            if ($report) {
                Write-Host "report $Message reached the stub on attempt $attempt"
                return $report
            }
            Start-Sleep -Milliseconds 250
        }
    }
    throw "shim report $Message never reached the stub upstream"
}

function Get-FifoStat([string] $Sandbox, [string] $Path, [string] $What) {
    $stat = Invoke-Wip @('sandbox', 'exec', $Sandbox, '--', 'stat', '-c', '%u-%a-%i', $Path)
    Assert-Exit $stat 0 $What
    $match = [regex]::Match($stat.Output, '(?m)^(\d+)-(\d+)-(\d+)\s*$')
    if (-not $match.Success) { throw "$What printed no owner/mode/inode: $($stat.Output.Trim())" }
    return [pscustomobject]@{ Owner = $match.Groups[1].Value; Mode = $match.Groups[2].Value; Inode = $match.Groups[3].Value }
}

# Killing the host relay ends its `wslc exec cat`; whether the cat inside the container goes
# with it is not asserted here. It is listed for the log and removed, so a leftover reader
# cannot take the next relay's reports.
function Clear-RelayReaders([string] $Sandbox) {
    Invoke-Wip @('sandbox', 'exec', $Sandbox, '--', 'pgrep', '-l', 'cat') | Out-Null
    Invoke-Wip @('sandbox', 'exec', $Sandbox, '--', 'pkill', '-x', 'cat') | Out-Null
}

function Invoke-RelayLifecycleE2E([string] $Namespace, [string] $Image) {
    $sandbox = 'relay-fixture'
    $suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $pipeName = "wip-e2e-herdr-$suffix"
    $pane = "e2e-pane-$suffix"
    $source = "wip:$sandbox"
    $record = Join-Path $script:Workspace 'relay-upstream.jsonl'
    $previousPane = $env:HERDR_PANE_ID
    $stub = $null
    $relay = $null
    $created = $false

    try {
        Write-Step 'report relay against a real sandbox and a stub Herdr pipe'

        # The relay stamps reports with its own HERDR_PANE_ID; every relay started below
        # inherits this one.
        $env:HERDR_PANE_ID = $pane
        $stub = Start-RelayStub $pipeName $record

        $created = $true
        Assert-Exit (Invoke-Wip @('sandbox', 'create', $sandbox)) 0 'create relay-fixture'

        # An exec into a report_relay sandbox carries the fixed FIFO path and the source.
        $fifoVariable = Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'printenv', 'WIP_REPORT_FIFO')
        Assert-Exit $fifoVariable 0 'WIP_REPORT_FIFO is exported'
        Assert-Match $fifoVariable ('(?m)^' + [regex]::Escape($script:RelayFifo) + '\s*$') 'WIP_REPORT_FIFO is the fixed path'
        Assert-Match (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'printenv', 'WIP_REPORT_SOURCE')) ('(?m)^' + [regex]::Escape($source) + '\s*$') 'WIP_REPORT_SOURCE names the sandbox'

        $id = Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'id', '-u')
        Assert-Exit $id 0 'sandbox exec user'
        $uid = [regex]::Match($id.Output, '(?m)^(\d+)\s*$').Groups[1].Value
        if (-not $uid) { throw "cannot read the sandbox exec uid: $($id.Output.Trim())" }

        # The shim from this repository, through stdin so no argv quoting is involved.
        $shim = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../scripts/herdr-report')
        $install = Invoke-WipWithStdin @('sandbox', 'exec', $sandbox, '--interactive', '--', 'sh') `
            (@("cat > /tmp/herdr-report <<'WIP_E2E_SHIM'") + $shim + @('WIP_E2E_SHIM', 'chmod 755 /tmp/herdr-report'))
        Assert-Exit $install 0 'install herdr-report in the sandbox'
        $usage = Invoke-Wip @('sandbox', 'exec', $sandbox, '--', '/tmp/herdr-report')
        Assert-Exit $usage 2 'herdr-report without arguments'
        Assert-Match $usage 'usage: herdr-report' 'herdr-report runs in the sandbox'

        Assert-NonZero (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'test', '-e', $script:RelayFifo)) 'no FIFO before the relay starts'

        # ---------------------------------------------------------------- creation
        Write-Step 'relay creates the FIFO with the exec user as owner, mode 600 in a 700 directory'
        $relay = Start-Relay $sandbox $pipeName 'first'
        Wait-RelayCondition {
            (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'test', '-p', $script:RelayFifo)).Code -eq 0
        } 60 'relay creates the FIFO'
        if ($relay.Process.HasExited) { throw "relay exited $($relay.Process.ExitCode) after creating the FIFO" }
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'test', '!', '-L', $script:RelayFifo)) 0 'FIFO is not a symlink'
        $initial = Get-FifoStat $sandbox $script:RelayFifo 'stat FIFO'
        if ("$($initial.Owner) $($initial.Mode)" -ne "$uid 600") { throw "FIFO has owner/mode $($initial.Owner) $($initial.Mode), expected $uid 600" }
        $directory = Get-FifoStat $sandbox '/run/wip' 'stat FIFO directory'
        if ("$($directory.Owner) $($directory.Mode)" -ne "$uid 700") { throw "/run/wip has owner/mode $($directory.Owner) $($directory.Mode), expected $uid 700" }

        # ---------------------------------------------------------------- forwarding
        Write-Step 'a shim report reaches the upstream, stamped by the relay'
        $report = Send-ShimReport $sandbox $record 'e2e-shim-first' 'blocked'
        if ($report.method -ne 'pane.report_agent') { throw "upstream got method '$($report.method)'" }
        if ($report.params.agent -ne 'claude-code' -or $report.params.state -ne 'blocked') { throw "upstream got agent/state '$($report.params.agent)'/'$($report.params.state)'" }
        if ($report.params.source -ne $source) { throw "upstream got source '$($report.params.source)', expected $source" }
        if ($report.params.pane_id -ne $pane) { throw "upstream got pane_id '$($report.params.pane_id)', expected $pane" }

        # Raw lines, not the shim: it cannot send pane_id. Written from a file under a
        # deadline so a missing reader cannot hang the run. The last line is allowed and
        # carries its own source, which the relay must overwrite.
        Write-Step 'sandbox-supplied pane_id and other methods are refused, source is overwritten'
        $raw = Invoke-WipWithStdin @('sandbox', 'exec', $sandbox, '--interactive', '--', 'sh') @(
            "cat > /tmp/relay-lines <<'WIP_E2E_LINES'",
            '{"id":"e2e-pane","method":"pane.report_agent","params":{"agent":"e2e","state":"idle","pane_id":"other-pane"}}',
            '{"id":"e2e-method","method":"pane.send_input","params":{"text":"e2e"}}',
            '{"id":"e2e-after","method":"pane.report_agent","params":{"agent":"e2e","state":"idle","message":"e2e-after-refusals","source":"spoofed"}}',
            'WIP_E2E_LINES',
            "timeout 10 sh -c 'cat /tmp/relay-lines > $($script:RelayFifo)'")
        Assert-Exit $raw 0 'write raw lines into the FIFO'
        Wait-RelayCondition { Find-RelayReport $record 'e2e-after-refusals' } 30 'allowed line after the refused ones reaches the stub'
        $after = Find-RelayReport $record 'e2e-after-refusals'
        if ($after.params.source -ne $source) { throw "sandbox-supplied source was forwarded: '$($after.params.source)'" }
        if ($after.params.pane_id -ne $pane) { throw "upstream got pane_id '$($after.params.pane_id)', expected $pane" }
        # Checked per parsed report, not by matching the recorded text: every forwarded line
        # carries the relay's own pane_id, which a text match cannot tell from a refused one.
        foreach ($forwarded in Get-RelayStubReports $record) {
            if ($forwarded.id -in 'e2e-pane', 'e2e-method') { throw "refused line $($forwarded.id) reached the upstream" }
            if ($forwarded.method -ne 'pane.report_agent') { throw "upstream got method '$($forwarded.method)'" }
            if ($forwarded.params.pane_id -ne $pane) { throw "upstream got pane_id '$($forwarded.params.pane_id)', expected $pane" }
        }
        $log = [pscustomobject]@{ Code = 0; Output = (Get-RelayLog $relay) }
        Assert-Match $log 'refused e2e-pane: pane_id is set by the relay' 'relay logs the pane_id refusal'
        Assert-Match $log 'refused e2e-method: method not allowed' 'relay logs the method refusal'

        # ---------------------------------------------------------------- lock
        Write-Step 'a second relay for the same sandbox refuses while the first holds the lock'
        [void](Assert-RelayRefuses $sandbox $pipeName 'second' 'a report relay is already running' 'second relay')
        if ($relay.Process.HasExited) { throw "first relay exited $($relay.Process.ExitCode) when the second was refused" }

        # ---------------------------------------------------------------- reader recovery
        # Each shim write closes the FIFO, so its cat has already seen EOF and exited 0; a
        # second delivered report means the relay reopened it.
        Write-Step 'reader recovers after cat exits cleanly and after it is killed'
        [void](Send-ShimReport $sandbox $record 'e2e-after-eof')

        Wait-RelayCondition {
            (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'pkill', '-x', 'cat')).Code -eq 0
        } 30 'kill the relay reader inside the sandbox'
        Wait-RelayCondition { (Get-RelayLog $relay) -match 'FIFO reader exited' } 30 'relay notices its reader died'
        [void](Send-ShimReport $sandbox $record 'e2e-after-kill')
        if ($relay.Process.HasExited) { throw "relay exited $($relay.Process.ExitCode) instead of recovering" }

        Stop-Relay $relay
        $relay = $null
        Clear-RelayReaders $sandbox

        # ---------------------------------------------------------------- reuse
        Write-Step 'relay refuses an existing FIFO with another mode or owner and does not fix it'
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'chmod', '644', $script:RelayFifo)) 0 'chmod FIFO 644'
        $badMode = Assert-RelayRefuses $sandbox $pipeName 'bad-mode' "has owner/mode $uid 644, expected $uid 600" 'relay with a 644 FIFO'
        Assert-Match $badMode 'could not create the report FIFO' 'bad mode fails FIFO setup'
        $afterMode = Get-FifoStat $sandbox $script:RelayFifo 'stat FIFO after refused mode'
        if ($afterMode.Mode -ne '644') { throw "relay changed the refused FIFO's mode to $($afterMode.Mode)" }
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'chmod', '600', $script:RelayFifo)) 0 'restore FIFO mode'

        $otherUid = if ($uid -eq '1000') { '1001' } else { '1000' }
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'chown', $otherUid, $script:RelayFifo)) 0 "chown FIFO to $otherUid"
        $badOwner = Assert-RelayRefuses $sandbox $pipeName 'bad-owner' "has owner/mode $otherUid 600, expected $uid 600" 'relay with a foreign-owned FIFO'
        Assert-Match $badOwner 'could not create the report FIFO' 'bad owner fails FIFO setup'
        $afterOwner = Get-FifoStat $sandbox $script:RelayFifo 'stat FIFO after refused owner'
        if ($afterOwner.Owner -ne $otherUid) { throw "relay changed the refused FIFO's owner to $($afterOwner.Owner)" }
        Assert-Exit (Invoke-Wip @('sandbox', 'exec', $sandbox, '--', 'chown', $uid, $script:RelayFifo)) 0 'restore FIFO owner'

        Write-Step 'relay reuses a matching FIFO in place'
        $relay = Start-Relay $sandbox $pipeName 'reuse'
        [void](Send-ShimReport $sandbox $record 'e2e-reuse')
        if ($relay.Process.HasExited) { throw "relay exited $($relay.Process.ExitCode) on a matching FIFO" }
        $reused = Get-FifoStat $sandbox $script:RelayFifo 'stat reused FIFO'
        if ($reused.Inode -ne $initial.Inode) { throw "FIFO was recreated (inode $($initial.Inode) -> $($reused.Inode)) instead of reused" }
        if ("$($reused.Owner) $($reused.Mode)" -ne "$uid 600") { throw "reused FIFO has owner/mode $($reused.Owner) $($reused.Mode)" }
        Stop-Relay $relay
        $relay = $null

        Assert-Exit (Invoke-Wip @('sandbox', 'destroy', $sandbox)) 0 'destroy relay-fixture'
        $created = $false

        Write-Step 'All report relay assertions passed'
    }
    finally {
        Stop-Relay $relay
        if ($stub) { try { if (-not $stub.HasExited) { $stub.Kill($true) } } catch { } }
        $env:HERDR_PANE_ID = $previousPane
        if ($created) {
            try {
                $cleanup = Invoke-Wip @('sandbox', 'destroy', $sandbox)
                if ($cleanup.Code -ne 0) { $script:Failed = $true; Write-Warning "sandbox cleanup failed ($sandbox): $($cleanup.Output)" }
            }
            catch { $script:Failed = $true; Write-Warning "sandbox cleanup failed ($sandbox): $_" }
        }
    }
}
