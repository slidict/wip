<#
.SYNOPSIS
    Runs wsl.exe, prints what it said, and returns its exit code and output together.

.DESCRIPTION
    Shared by the Update WSL and Diagnostics steps of .github/workflows/e2e-windows.yml,
    which each dot-source this file. They used to carry a copy of this function apiece;
    the copies had already drifted in their return value by the time they were unified.

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

.OUTPUTS
    Hashtable with Code (the child's exit code) and Text (stdout and stderr joined).
#>

function Show-Wsl([string[]] $WslArgs) {
    $wslExe = Join-Path $env:SystemRoot 'System32\wsl.exe'
    if (-not (Test-Path -LiteralPath $wslExe -PathType Leaf)) {
        throw "the WSL launcher was not found at $wslExe"
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new($wslExe)
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
