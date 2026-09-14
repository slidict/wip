# Shared helpers for the wip vs Docker Desktop benchmark. Dot-source this from
# Invoke-Benchmark.ps1 or an interactive session: `. .\common.ps1`

$ErrorActionPreference = 'Continue'

function Wait-HttpReady {
    param(
        [string]$Url,
        [double]$TimeoutSec = 60,
        [double]$PollIntervalSec = 0.2
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2
            if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300) {
                $sw.Stop()
                return [pscustomobject]@{ Ready = $true; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
            }
        } catch {
            Start-Sleep -Milliseconds ([int]($PollIntervalSec * 1000))
        }
    }
    $sw.Stop()
    return [pscustomobject]@{ Ready = $false; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Test-DockerReady {
    docker info 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Wait-DockerState {
    param([bool]$WantReady, [double]$TimeoutSec = 120)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if ((Test-DockerReady) -eq $WantReady) {
            $sw.Stop()
            return [pscustomobject]@{ Reached = $true; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
        }
        Start-Sleep -Seconds 2
    }
    $sw.Stop()
    return [pscustomobject]@{ Reached = $false; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Start-DockerDesktopInfra {
    param([double]$TimeoutSec = 180)
    $exe = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Process -FilePath $exe | Out-Null
    $result = Wait-DockerState -WantReady $true -TimeoutSec $TimeoutSec
    $sw.Stop()
    return [pscustomobject]@{
        Ready       = $result.Reached
        ElapsedMs   = $sw.Elapsed.TotalMilliseconds
    }
}

function Stop-DockerDesktopInfra {
    param([double]$TimeoutSec = 120)
    # Ask nicely first (CloseMainWindow — Docker Desktop's own quit path stops containers
    # gracefully before tearing down the VM) and only escalate to a hard kill if it ignores
    # the request. A force-kill here bypasses container shutdown entirely, which can hard-kill
    # anything still running under it (see: this script's own incident with a user's
    # unrelated project containers, restarted-and-crash-recovered by repeated force kills).
    $guiProcs = Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue
    if ($guiProcs) {
        foreach ($proc in $guiProcs) { [void]$proc.CloseMainWindow() }
        $graceSw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($graceSw.Elapsed.TotalSeconds -lt 45 -and (Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 2
        }
    }
    Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name 'com.docker.backend' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name 'com.docker.build' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($distro in @('docker-desktop', 'docker-desktop-data')) {
        wsl --terminate $distro 2>$null | Out-Null
    }
    $result = Wait-DockerState -WantReady $false -TimeoutSec $TimeoutSec
    return $result
}

function Get-WslcSessionCount {
    $out = wslc system session list 2>$null
    if (-not $out) { return 0 }
    # Header line + one line per session; header always present when the command succeeds.
    $lines = $out | Where-Object { $_.Trim().Length -gt 0 }
    return [Math]::Max(0, $lines.Count - 1)
}

function Start-WslcInfra {
    param([double]$TimeoutSec = 120)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    wslc list 2>$null | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    $sw.Stop()
    return [pscustomobject]@{ Ready = $ok; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Stop-WslcInfra {
    param([double]$TimeoutSec = 60)
    wslc system session terminate 2>$null | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = $false
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if ((Get-WslcSessionCount) -eq 0) { $ok = $true; break }
        Start-Sleep -Seconds 1
    }
    $sw.Stop()
    return [pscustomobject]@{ Stopped = $ok; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
}

function Format-ProcessArgument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

function Invoke-TimedProcess {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory = $null
    )
    # Windows PowerShell 5.1 runs on .NET Framework, where ProcessStartInfo.ArgumentList is
    # present but left null (never auto-instantiated the way .NET Core does), so building it
    # via .Add() silently no-ops the whole argument list. Build the classic Arguments string
    # instead — it works identically on both runtimes.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($ArgumentList | ForEach-Object { Format-ProcessArgument $_ }) -join ' ')
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    $sw.Stop()

    return [pscustomobject]@{
        ExitCode  = $p.ExitCode
        ElapsedMs = $sw.Elapsed.TotalMilliseconds
        StdOut    = $stdout
        StdErr    = $stderr
    }
}

function Invoke-TimedWsl {
    param(
        [string]$Distro,
        [string]$BashCommand
    )
    return Invoke-TimedProcess -FilePath 'wsl' -ArgumentList @('-d', $Distro, '--', 'bash', '-lc', $BashCommand)
}

function Start-HostSampler {
    param(
        [string]$CsvPath,
        [string]$Config,
        [string]$Phase,
        [int]$Round,
        [double]$DurationSec,
        [double]$IntervalSec = 1.0,
        [string]$ProcessPattern = 'Docker Desktop|com\.docker|wslservice|wslcsession|wslrelay|vmmem|vmwp|wslhost'
    )
    $scriptPath = Join-Path $PSScriptRoot 'sample-host.ps1'
    # Start-Process -ArgumentList does not auto-quote elements containing spaces (unlike
    # ProcessStartInfo.ArgumentList on .NET Core) — an unquoted -ProcessPattern value with a
    # space (e.g. 'Docker Desktop|com\.docker') silently breaks param binding and the sampler
    # exits without writing a single row. Quote every element explicitly.
    $argList = @(
        '-NoProfile', '-NonInteractive', '-File', (Format-ProcessArgument $scriptPath),
        '-CsvPath', (Format-ProcessArgument $CsvPath),
        '-Config', (Format-ProcessArgument $Config),
        '-Phase', (Format-ProcessArgument $Phase),
        '-Round', $Round,
        '-DurationSec', $DurationSec,
        '-IntervalSec', $IntervalSec,
        '-ProcessPattern', (Format-ProcessArgument $ProcessPattern)
    )
    return Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -WindowStyle Hidden -PassThru
}
