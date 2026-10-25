# Dot-source from run-e2e.ps1. Real engine execution uses an explicit test-only harness;
# no fixture is registered in the production scanner table.
function Invoke-SecurityScanE2E([string] $Namespace, [string] $Image, [string] $BaseImage, [string] $Harness) {
    $sandboxes = New-Object 'Collections.Generic.List[string]'
    $targetImage = "wip-e2e-scan-${Namespace}:latest"
    $imageBuildAttempted = $false
    $configPath = Join-Path $script:Workspace 'security-scan.yml'
    $outcomePath = Join-Path $script:Workspace 'security-scan-outcome.json'
    $buildDirectory = Join-Path $script:Workspace 'security-scan-build'
    $fixtureScript = Join-Path $PSScriptRoot 'security-scan-fixture.sh'
    $wslcConfig = $script:WslcPath.Replace("'", "''")
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
    $dockerfile = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Dockerfile.security-scan'))
    [IO.File]::WriteAllText((Join-Path $buildDirectory 'Dockerfile'), $dockerfile.Replace('FROM wip-e2e:latest', "FROM $Image"))

    function Invoke-ScanBounded([string] $Exe, [string[]] $Arguments, [int] $TimeoutSeconds = 180) {
        $job = Start-Job -ScriptBlock {
            param($exe, $argv, $cwd)
            $ErrorActionPreference = 'Stop'
            $PSNativeCommandUseErrorActionPreference = $false
            Set-Location $cwd
            $text = & $exe @argv 2>&1 | Out-String
            [pscustomobject]@{ Code = $LASTEXITCODE; Output = $text }
        } -ArgumentList $Exe, $Arguments, $script:Workspace
        try {
            if (-not (Wait-Job $job -Timeout $TimeoutSeconds)) {
                throw "timed out after ${TimeoutSeconds}s: $Exe $($Arguments -join ' ')"
            }
            $result = Receive-Job $job -ErrorAction SilentlyContinue
            if ($null -eq $result -or $null -eq $result.Code) {
                throw "no exit status from: $Exe $($Arguments -join ' ')"
            }
            Write-Host "`$ $Exe $($Arguments -join ' ')  -> exit $($result.Code)"
            if ($result.Output.Trim()) { Write-Host $result.Output.TrimEnd() }
            return $result
        }
        finally { Remove-Job $job -Force -ErrorAction SilentlyContinue }
    }

    function Invoke-ScanWip([string[]] $Arguments) {
        Invoke-ScanBounded -Exe $script:WipPath -Arguments (@('--config', $configPath) + $Arguments)
    }

    try {
        Write-Step 'a lightweight scanner exercises the real engine inside an internal network'
        # A locally built image has no repository digest. Use the already pulled upstream
        # base for the scanner, retaining real image verification rather than inventing a pin.
        $inspection = Invoke-ScanBounded -Exe $script:WslcPath -Arguments @('inspect', '--type', 'image', '--format', 'json', $BaseImage)
        Assert-Exit $inspection 0 'inspect the upstream scanner fixture image'
        $records = @(Read-WslcRecords $inspection.Output)
        if ($records.Count -ne 1) { throw 'expected one upstream image inspection record' }
        $digests = @($records[0].RepoDigests | Where-Object { $_ -match '^.+@sha256:[a-fA-F0-9]{64}$' })
        if ($digests.Count -eq 0) { throw 'the upstream scanner fixture image has no repository digest' }
        $scannerImage = $digests[0]

        $imageBuildAttempted = $true
        Assert-Exit (Invoke-ScanBounded -Exe $script:WslcPath -Arguments @('build', '-t', $targetImage, $buildDirectory) -TimeoutSeconds 300) 0 'build the namespaced HTTP target fixture'
        [IO.File]::WriteAllText($configPath, @"
version: 1
resource_namespace: $Namespace
networks:
  - name: engine-scan-isolated
    internal: true
sandboxes:
  - name: engine-scan-target
    image: $targetImage
    network: engine-scan-isolated
    command: [httpd, '-f', '-p', '8080', '-h', /srv]
  - name: engine-scan-scanner
    image: $scannerImage
    network: engine-scan-isolated
    command: [sleep, '600']
mode: container
wslc:
  command: '$wslcConfig'
container: unused-engine-scan
# The existing project-level command field is a shell string; sandbox commands are argv.
dependencies:
  unused-engine-scan:
    image: $targetImage
    workdir: /srv
    interactive: false
    remove: true
    command: sleep 600
"@)

        foreach ($sandbox in @('engine-scan-target', 'engine-scan-scanner')) {
            $sandboxes.Add($sandbox)
            Assert-Exit (Invoke-ScanWip @('sandbox', 'create', $sandbox)) 0 "create $sandbox"
        }
        $address = Invoke-ScanWip @('sandbox', 'exec', 'engine-scan-target', '--', 'ip', '-4', '-o', 'addr', 'show', 'eth0')
        Assert-Exit $address 0 'read the internal HTTP target address'
        $matched = [regex]::Match($address.Output, 'inet (\d+\.\d+\.\d+\.\d+)')
        if (-not $matched.Success) { throw 'the HTTP target has no readable IPv4 address' }
        $target = "http://$($matched.Groups[1].Value):8080/marker.txt"

        $scan = Invoke-ScanBounded -Exe $Harness -Arguments @($configPath, $scannerImage, $target, $fixtureScript, $outcomePath) -TimeoutSeconds 300
        Assert-Exit $scan 7 'the engine forwards the fixture scanner status'
        if (-not (Test-Path -LiteralPath $outcomePath)) { throw 'the engine harness wrote no outcome' }
        $outcome = [IO.File]::ReadAllText($outcomePath) | ConvertFrom-Json
        if ($outcome.ExitCode -ne 7 -or $outcome.TimedOut -or $outcome.ReportError) {
            throw 'the fixture scan did not complete with its original exit status'
        }
        if (-not $outcome.Report) { throw 'ReadReport returned no raw report' }
        $report = $outcome.Report | ConvertFrom-Json
        if ($report.target_http_completed -ne $true) { throw 'the scanner did not complete its internal HTTP request' }
        if ($report.external_attempted -ne 6 -or $report.external_successful -ne 0) {
            throw 'an external probe succeeded, or a required probe was not attempted during the scan'
        }
        if (@($report.unperformed_checks).Count -ne 1 -or $report.unperformed_checks[0].id -ne 'authenticated-pages') {
            throw 'the scanner raw report lost its unperformed-check information'
        }
        if (-not $outcome.UnperformedChecks -or $outcome.UnperformedChecksError) { throw 'the engine did not recover the raw unperformed-check information' }
        $unperformed = $outcome.UnperformedChecks | ConvertFrom-Json
        if (@($unperformed).Count -ne 1 -or $unperformed[0].id -ne 'authenticated-pages') {
            throw 'the recovered unperformed checks differ from the raw report'
        }
        $scannerStatus = Invoke-ScanWip @('sandbox', 'status', 'engine-scan-scanner')
        Assert-Exit $scannerStatus 0 'status after the engine scan'
        Assert-Match $scannerStatus '(?m)^engine-scan-scanner\s+exited\s' 'the engine requested scanner stop'
    }
    finally {
        foreach ($sandbox in $sandboxes) {
            try {
                $cleanup = Invoke-ScanWip @('sandbox', 'destroy', $sandbox)
                if ($cleanup.Code -ne 0) { throw $cleanup.Output.Trim() }
            }
            catch {
                $script:Failed = $true
                Write-Warning "scan fixture cleanup failed (${sandbox}): $_"
            }
        }
        if ($imageBuildAttempted) {
            try {
                $cleanup = Invoke-ScanBounded -Exe $script:WslcPath -Arguments @('rmi', $targetImage) -TimeoutSeconds 120
                if ($cleanup.Code -ne 0) { throw $cleanup.Output.Trim() }
            }
            catch {
                $script:Failed = $true
                Write-Warning "scan fixture image cleanup failed (${targetImage}): $_"
            }
        }
    }
}
