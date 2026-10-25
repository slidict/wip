<#
.SYNOPSIS
    Runs the built-in ZAP adapter against one unauthenticated internal target.
.DESCRIPTION
    Invoked only by the scheduled/manual real-WSLC workflow, never on pull requests.
    Uses the published CLI and a digest-pinned ZAP image, not the lightweight scan fixture.
#>
[CmdletBinding()]
param(
    [string] $Wip = 'artifacts/win-x64/wip.exe',
    [string] $Wslc = 'wslc',
    [string] $TargetImage = 'wip-e2e:latest',
    [string] $WorkRoot = $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP })
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$wipPath = (Resolve-Path -LiteralPath $Wip).Path
$wslcPath = (Get-Command $Wslc -ErrorAction Stop).Source
$namespace = 'zap-e2e-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$workspace = Join-Path $WorkRoot $namespace
$zapImage = 'ghcr.io/zaproxy/zaproxy@sha256:5ccfff9a411743ab2615136a2edd7416bec9ce41572fa94e935df38c14adb2b6'
$sandboxes = New-Object 'Collections.Generic.List[string]'
$builtImages = New-Object 'Collections.Generic.List[string]'
$failed = $false
New-Item -ItemType Directory -Path $workspace -Force | Out-Null
$configPath = Join-Path $workspace 'wip.yml'
$quotedWslc = $wslcPath.Replace("'", "''")

# The HTTP target is the same derived image security-scan.ps1 builds, from the same
# Dockerfile, because httpd is not in Alpine's base busybox -- measured on alpine:3.20,
# `busybox --list` has no httpd applet and `command -v httpd` finds nothing. Running
# $TargetImage directly is what failed the first dispatched run of this scenario with
# `exec: "httpd": executable file not found in $PATH`. One Dockerfile keeps the HTTP target
# defined in a single place rather than twice.
# Named so it cannot collide with the $TargetImage parameter: PowerShell variable names are
# case-insensitive, so an earlier $targetImage here overwrote the parameter and the generated
# Dockerfile said `FROM <the derived tag>` -- which failed the dispatched run with
# "pull access denied, repository does not exist".
$derivedTarget = "wip-e2e-zap-target-${namespace}:latest"
$targetBuildDirectory = Join-Path $workspace 'zap-target-build'
New-Item -ItemType Directory -Path $targetBuildDirectory -Force | Out-Null
$targetDockerfile = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Dockerfile.security-scan'))
$targetDockerfile = $targetDockerfile.Replace('FROM wip-e2e:latest', "FROM $TargetImage")
[IO.File]::WriteAllText((Join-Path $targetBuildDirectory 'Dockerfile'), $targetDockerfile)
$quotedTarget = $derivedTarget.Replace("'", "''")
[IO.File]::WriteAllText($configPath, @"
version: 1
resource_namespace: $namespace
mode: container
wslc:
  command: '$quotedWslc'
networks:
  - name: zap-isolated
    internal: true
sandboxes:
  - name: zap-target
    image: '$quotedTarget'
    network: zap-isolated
    command: [httpd, -f, -p, '8080', -h, /srv]
  - name: zap-scanner
    image: '$zapImage'
    network: zap-isolated
    command: [sleep, '900']
"@)

# Native argv stays an array. Every process, including pull and finally cleanup, has an
# outer deadline because a remote process can keep a native output stream open indefinitely.
function Invoke-ZapBounded([string] $Exe, [string[]] $Arguments, [int] $TimeoutSeconds = 120) {
    $job = Start-Job -ScriptBlock {
        param($exe, $argv, $cwd)
        $ErrorActionPreference = 'Stop'
        $PSNativeCommandUseErrorActionPreference = $false
        Set-Location $cwd
        $stdoutPath = Join-Path $cwd ([guid]::NewGuid().ToString('N') + '.stdout')
        $stderrPath = Join-Path $cwd ([guid]::NewGuid().ToString('N') + '.stderr')
        try {
            & $exe @argv 1> $stdoutPath 2> $stderrPath
            $code = $LASTEXITCODE
            $stdout = [IO.File]::ReadAllText($stdoutPath)
            $stderr = [IO.File]::ReadAllText($stderrPath)
            [pscustomobject]@{ Code = $code; Stdout = $stdout; Stderr = $stderr; Output = "$stdout`n$stderr" }
        }
        finally {
            Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
        }
    } -ArgumentList $Exe, $Arguments, $workspace
    try {
        if (-not (Wait-Job $job -Timeout $TimeoutSeconds)) {
            throw "timed out after ${TimeoutSeconds}s: $Exe $($Arguments -join ' ')"
        }
        $result = Receive-Job $job -ErrorAction SilentlyContinue
        if ($null -eq $result -or $null -eq $result.Code) {
            throw "no exit status from: $Exe $($Arguments -join ' ')"
        }
        Write-Host "`$ $Exe $($Arguments -join ' ') -> exit $($result.Code)"
        if ($result.Output.Trim()) { Write-Host $result.Output.TrimEnd() }
        return $result
    }
    finally {
        Remove-Job $job -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-ZapWip([string[]] $Arguments, [int] $TimeoutSeconds = 120) {
    Invoke-ZapBounded -Exe $wipPath -Arguments (@('--config', $configPath) + $Arguments) -TimeoutSeconds $TimeoutSeconds
}

function Assert-ZapExit($Result, [int] $Expected, [string] $What) {
    if ($Result.Code -ne $Expected) { throw "${What}: expected exit $Expected, got $($Result.Code): $($Result.Output.Trim())" }
}

try {
    # The image is about 1.12 GiB compressed. Bound the one real download separately from
    # execution; later Prepare still performs its own availability check outside isolation.
    Assert-ZapExit (Invoke-ZapBounded -Exe $wslcPath -Arguments @('pull', $zapImage) -TimeoutSeconds 600) 0 'pull the immutable ZAP image'
    $builtImages.Add($derivedTarget)
    Assert-ZapExit (Invoke-ZapBounded -Exe $wslcPath -Arguments @('build', '-t', $derivedTarget, $targetBuildDirectory) -TimeoutSeconds 300) 0 'build the HTTP target with httpd available'
    $sandboxes.Add('zap-target')
    Assert-ZapExit (Invoke-ZapWip @('sandbox', 'create', 'zap-target')) 0 'create the unauthenticated HTTP target'
    $address = Invoke-ZapWip @('sandbox', 'exec', 'zap-target', '--', 'ip', '-4', '-o', 'addr', 'show', 'eth0')
    Assert-ZapExit $address 0 'read the target address'
    $match = [regex]::Match($address.Stdout, 'inet (\d+\.\d+\.\d+\.\d+)')
    if (-not $match.Success) { throw "cannot read target address: $($address.Output.Trim())" }
    $target = 'http://' + $match.Groups[1].Value + ':8080/'
    Assert-ZapExit (Invoke-ZapWip @('sandbox', 'exec', 'zap-target', '--', 'sh', '-c',
        'printf "%s" "<html><head><title>ZAP fixture</title></head><body>scan target</body></html>" > /srv/index.html')) 0 'write a single page without outbound links'
    Assert-ZapExit (Invoke-ZapWip @('sandbox', 'exec', 'zap-target', '--', 'wget', '-q', '-O', '-', $target)) 0 'the target serves HTTP'

    $sandboxes.Add('zap-scanner')
    Assert-ZapExit (Invoke-ZapWip @('sandbox', 'create', 'zap-scanner')) 0 'create the resident nonroot ZAP sandbox'
    $scan = Invoke-ZapWip -Arguments @('security', 'scan', 'zap-scanner', '--tool', 'zap', '--target', $target, '--timeout', '180') -TimeoutSeconds 360
    # ZAP AF documents 0 for completion, 1 for errors and 2 for warnings:
    # https://www.zaproxy.org/docs/automate/automation-framework/#exit-value
    # Warnings may still produce a complete report; an error or timeout must fail this test.
    if ($scan.Code -notin @(0, 2)) { throw "ZAP did not finish: exit $($scan.Code): $($scan.Output.Trim())" }
    if ($scan.Stderr -notmatch '(?m)^zap\tzap-scanner\t.+\texit=[02]\tstatus=exited\s*$') {
        throw "ZAP provenance is missing: $($scan.Stderr.Trim())"
    }
    if ([string]::IsNullOrWhiteSpace($scan.Stdout)) { throw 'ZAP returned no raw report' }
    if ($scan.Stderr -notmatch 'Unperformed checks \(raw\):' -or $scan.Stderr -notmatch 'not performed') {
        throw "ZAP omitted-check information is missing: $($scan.Stderr.Trim())"
    }
    $report = $scan.Stdout | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $report -or @($report.site).Count -eq 0 -or $null -eq $report.site) {
        throw 'the raw ZAP report contains no scanned site'
    }
    $status = Invoke-ZapWip @('sandbox', 'status', 'zap-scanner')
    Assert-ZapExit $status 0 'read scanner state after cleanup'
    if ($status.Stdout -notmatch '(?m)^zap-scanner\s+exited\s+') {
        throw "scanner did not stop after the scan: $($status.Output.Trim())"
    }
}
catch {
    $failed = $true
    Write-Error $_ -ErrorAction Continue
}
finally {
    for ($index = $sandboxes.Count - 1; $index -ge 0; $index--) {
        $sandbox = $sandboxes[$index]
        try {
            $cleanup = Invoke-ZapWip @('sandbox', 'destroy', $sandbox)
            if ($cleanup.Code -ne 0) {
                $failed = $true
                Write-Warning "ZAP fixture cleanup failed (${sandbox}): $($cleanup.Output.Trim())"
            }
        }
        catch {
            $failed = $true
            Write-Warning "ZAP fixture cleanup failed (${sandbox}): $_"
        }
    }
    # Only tags this run built. The pinned ZAP image is left in the runner's store: it is
    # immutable by digest, and re-downloading 1.12 GiB on the next scheduled run buys nothing.
    foreach ($tag in $builtImages) {
        try {
            $removed = Invoke-ZapBounded -Exe $wslcPath -Arguments @('rmi', $tag) -TimeoutSeconds 120
            # A tag is recorded before its build so a partial build is still cleaned up, which
            # means a build that failed outright leaves nothing to remove. Reporting that as a
            # cleanup failure buries the real one -- the dispatched run printed both.
            if ($removed.Code -ne 0 -and $removed.Output -notmatch 'WSLC_E_IMAGE_NOT_FOUND|No such image') {
                $failed = $true
                Write-Warning "ZAP fixture image cleanup failed (${tag}): $($removed.Output.Trim())"
            }
        }
        catch {
            $failed = $true
            Write-Warning "ZAP fixture image cleanup failed (${tag}): $_"
        }
    }
    if ($failed) { Write-Host "ZAP fixture workspace retained: $workspace" }
    else { Remove-Item -LiteralPath $workspace -Recurse -Force }
}
if ($failed) { exit 1 }
