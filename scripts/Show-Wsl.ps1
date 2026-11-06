<#
.SYNOPSIS
    Resolves and runs the WSL launcher, printing what it said.

.DESCRIPTION
    The single place this repository decides which wsl.exe to run and how to read it.
    Dot-sourced by the Update WSL, Verify wslc is available and Diagnostics steps of
    .github/workflows/e2e-windows.yml and by tests/e2e/run-e2e.ps1.

    Three things here are deliberate and easy to undo by accident:

    * The launcher is named by absolute path. With UseShellExecute = $false a bare name
      reaches CreateProcess as lpApplicationName = null, and that search order places the
      current directory ahead of System32 -- in the E2E job the current directory is the
      checked-out tree of the pull request under test.
    * StandardOutputEncoding is stated for this one child. wsl.exe writes UTF-16LE, and
      every attempt to fix that through [Console]::OutputEncoding came back garbled in
      these steps: the runner starts pwsh with no console of its own, so the property has
      nothing to govern.
    * One pipe is read asynchronously. A child that fills stderr while the parent is
      blocked on stdout deadlocks, and here that means hanging to the job timeout.

    The exit code and the text are returned together because the caller cannot read
    $LASTEXITCODE for a process started this way, and the text is gone once this returns.
#>

<#
.SYNOPSIS
    Returns the absolute path of the WSL launcher, or throws naming where it looked.

.DESCRIPTION
    System32 holds the launcher when the Microsoft-Windows-Subsystem-Linux optional
    component is enabled. It is not the only place: with WSL installed only as the Store
    package, the launcher lives with the package instead, which is why the sibling lookup
    in Verify wslc is available checks ProgramFiles\WSL and the Appx install location for
    wslc.exe. The same locations are checked here, in that order.

    Order is the whole design here. The first three locations all need administrator rights
    to write to, so a decoy cannot be placed in them by whoever can write to the working
    directory. They are tried first and, on any machine where WSL is installed normally --
    the CI runner included -- one of them matches and the search stops there.

    %LOCALAPPDATA%\Microsoft\WindowsApps, where the app execution alias lives, is checked
    only after all three have missed. That covers a machine with WSL installed solely as
    the Store package and the Microsoft-Windows-Subsystem-Linux optional component never
    enabled, which is the one case the trusted locations do not reach.

    Be clear about what that last entry costs: it is writable without administrator
    rights, so on a machine where the first three miss, the launcher is being taken from a
    directory that anything running as the current user can write to. It is last so that
    it is never reached when a trusted copy exists; it is not a safe location, it is a
    reachable one. If this function is ever used somewhere a third party supplies the
    code, drop this entry rather than reordering it.

    What is still NOT checked: PATH, via Get-Command or a bare name. That is the search
    this exists to avoid -- PATH is assembled from entries this script does not control,
    and can include the current directory.
#>
function Resolve-WslLauncher {
    $candidates = @(
        (Join-Path $env:SystemRoot 'System32\wsl.exe')
        (Join-Path $env:ProgramFiles 'WSL\wsl.exe')
    )

    $package = Get-AppxPackage -Name '*WindowsSubsystemForLinux*' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($package) { $candidates += (Join-Path $package.InstallLocation 'wsl.exe') }

    # Last, and only because the three above do not reach a Store-only install. User-writable:
    # see the note in this function's help before moving it or adding anything beside it.
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wsl.exe')

    foreach ($candidate in $candidates) {
        # Rooted as well as present: a relative path would reintroduce the thing being avoided.
        if ([System.IO.Path]::IsPathRooted($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    throw "the WSL launcher was not found. Checked: $($candidates -join '; ')"
}

function Show-Wsl([string[]] $WslArgs) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new((Resolve-WslLauncher))
    foreach ($argument in $WslArgs) { $psi.ArgumentList.Add($argument) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::Unicode
    $psi.StandardErrorEncoding = [System.Text.Encoding]::Unicode

    $process = [System.Diagnostics.Process]::Start($psi)
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        $out = $process.StandardOutput.ReadToEnd()
        $err = $errorRead.GetAwaiter().GetResult()
        $process.WaitForExit()
        # Read before the finally block: ExitCode is not available on a disposed Process.
        $code = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    foreach ($text in @($out, $err)) { if ($text.Trim()) { Write-Host $text.TrimEnd() } }
    return @{ Code = $code; Text = ($out + "`n" + $err) }
}
