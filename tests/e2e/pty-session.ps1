# Dot-source from run-e2e.ps1; Invoke-Wip and the assertion helpers belong to the caller.
#
# The terminal half of `sandbox exec --interactive`. The rest of the suite drives it through a
# pipe, which covers stdin, EOF and exit codes but leaves everything that only exists when a
# terminal does: whether the container's shell sees a tty at all, what size it reads, whether a
# resize reaches it, and whether Ctrl-C arrives as an interrupt rather than as a byte of text.
#
# A CI runner has no terminal, but that only means nobody hands one over. tests/e2e/PtyHarness
# creates one with ConPTY, starts wip inside it, and drives the session from a script of
# directives. So these assertions run unattended on windows-latest, against the real
# Windows -> WSLC -> container path.

function Invoke-PtySession {
    param(
        [Parameter(Mandatory)] [string] $Harness,
        [Parameter(Mandatory)] [string[]] $Lines,
        [Parameter(Mandatory)] [string[]] $WipArguments,
        [int] $Columns = 80,
        [int] $Rows = 24,
        [int] $TimeoutSeconds = 60
    )

    $stem = Join-Path ([IO.Path]::GetTempPath()) ("wip-e2e-pty-" + [guid]::NewGuid().ToString('N'))
    $scriptPath = "$stem.script"
    $resultPath = "$stem.result"
    $stdoutPath = "$stem.out"
    $stderrPath = "$stem.err"
    # LF, like every other script the harness reads; its parser trims either way, but keeping
    # the file in one shape makes a failure easier to read.
    [IO.File]::WriteAllText($scriptPath, (($Lines -join "`n") + "`n"))

    try {
        $arguments = @(
            '--script', $scriptPath,
            '--cols', $Columns, '--rows', $Rows,
            '--timeout', $TimeoutSeconds,
            '--result', $resultPath,
            '--',
            $script:WipPath
        ) + $WipArguments

        $process = Start-Process -FilePath $Harness -ArgumentList $arguments `
            -WorkingDirectory $script:Workspace -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

        $transcript = [IO.File]::ReadAllText($stdoutPath)
        $diagnostics = [IO.File]::ReadAllText($stderrPath)

        # The result file, not $process.ExitCode: closing a pseudo console raises a control
        # event that can end the harness with STATUS_CONTROL_C_EXIT after it has already
        # decided what to report. The file is written first, so it survives that.
        if (-not (Test-Path -LiteralPath $resultPath)) {
            throw "pty harness wrote no result file (process exit $($process.ExitCode)): $diagnostics$transcript"
        }

        $result = @{}
        foreach ($line in [IO.File]::ReadAllLines($resultPath)) {
            $pair = $line.Split('=', 2)
            if ($pair.Length -eq 2) { $result[$pair[0]] = $pair[1] }
        }

        Write-Host "`$ pty($($Columns)x$($Rows)) wip $($WipArguments -join ' ')  -> status=$($result['status']) exit=$($result['exit'])"
        if ($transcript.Trim()) { Write-Host $transcript.TrimEnd() }
        if ($diagnostics.Trim()) { Write-Host $diagnostics.TrimEnd() }

        return [pscustomobject]@{
            Status = $result['status']
            Code   = if ($result['exit'] -match '^-?\d+$') { [int] $result['exit'] } else { $null }
            Output = $transcript + $diagnostics
        }
    }
    finally {
        Remove-Item $scriptPath, $resultPath, $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-PtyOk($Result, [string] $What) {
    if ($Result.Status -ne 'ok') {
        throw "$What did not complete: status=$($Result.Status)"
    }
}

function Invoke-PtyE2E([string] $Harness, [string] $Sandbox) {
    Write-Step 'interactive sandbox exec through a real pseudo console'

    # Each scenario starts from a running sandbox. `sandbox create` resumes an owned stopped
    # container, so this is also the recovery if a previous scenario's session took the
    # container's main process with it.
    Assert-Exit (Invoke-Wip @('sandbox', 'create', $Sandbox)) 0 'sandbox running before the pty session'

    # A terminal, its size, and a resize reaching the container -- then the shell's own status
    # on the way back out.
    $session = Invoke-PtySession -Harness $Harness -Columns 80 -Rows 24 -Lines @(
        'send test -t 0 && test -t 1 && echo TTY_OK'
        'expect TTY_OK'
        'send stty size'
        'expect (^|\n)24 80'
        'resize 120x40'
        # Nothing announces the resize, and the container reads its size when asked, so the
        # query has to come after it arrives. Without this the shell answers 24 80 and the
        # assertion fails for a reason that has nothing to do with the resize path.
        'sleep 1500'
        'send stty size'
        'expect (^|\n)40 120'
        'send exit 7'
    ) -WipArguments @('sandbox', 'exec', $Sandbox, '--interactive', '--', 'sh')

    Assert-PtyOk $session 'pty session (tty, size, resize)'
    Assert-Match $session 'TTY_OK' 'the container shell sees a terminal on stdin and stdout'
    Assert-Match $session '(^|\n)24 80' 'the container reads the console size it was started with'
    Assert-Match $session '(^|\n)40 120' 'resizing the console reaches the container pty'
    Assert-Exit $session 7 'the shell exit status survives the pty session'

    Assert-Exit (Invoke-Wip @('sandbox', 'create', $Sandbox)) 0 'sandbox running before the interrupt session'

    # Ctrl-C has to arrive as an interrupt, which only a terminal delivers: a trap in the
    # container is what proves it, and the status it chooses proves the chain carried it back.
    $interrupt = Invoke-PtySession -Harness $Harness -Lines @(
        "send trap 'echo GOT_SIGINT; exit 42' INT"
        'send echo READY; sleep 30'
        'expect READY'
        'sleep 1000'
        'ctrl-c'
        'expect GOT_SIGINT'
    ) -WipArguments @('sandbox', 'exec', $Sandbox, '--interactive', '--', 'sh')

    Assert-PtyOk $interrupt 'pty session (interrupt)'
    Assert-Match $interrupt 'GOT_SIGINT' 'Ctrl-C reaches the container as an interrupt'
    Assert-Exit $interrupt 42 'the interrupted shell status survives the pty session'

    # The suite's own cleanup repeats this; destroying here keeps the section self-contained
    # and proves the fixture survived the sessions well enough to be removed by its ID.
    Assert-Exit (Invoke-Wip @('sandbox', 'destroy', $Sandbox)) 0 'destroy the fixture after the pty sessions'
}
