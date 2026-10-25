# Dot-source from run-e2e.ps1 after its fixture image has been built.
# The fixture deliberately uses a one-shot CMD, not a fixed ENTRYPOINT.
function Invoke-SandboxCommandE2E([string] $Namespace, [string] $Image) {
    $sandboxes = New-Object 'Collections.Generic.List[string]'
    $commandImage = 'wip-e2e-command:latest'
    $configPath = Join-Path $script:Workspace 'sandbox-command.yml'
    $buildDirectory = Join-Path $script:Workspace 'sandbox-command-build'
    $wslcConfig = $script:WslcPath.Replace("'", "''")
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
    $dockerfile = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Dockerfile.sandbox-command'))
    $dockerfile = $dockerfile.Replace('FROM wip-e2e:latest', "FROM $Image")
    [IO.File]::WriteAllText((Join-Path $buildDirectory 'Dockerfile'), $dockerfile)

    # A separate config leaves the other lifecycle fixtures unchanged. Both sandboxes use
    # the same one-shot image; only the first declares a replacement CMD.
    [IO.File]::WriteAllText($configPath, @"
version: 1
resource_namespace: $Namespace
sandboxes:
  - name: command-override
    image: $commandImage
    command: [sleep, '600']
  - name: command-default
    image: $commandImage
mode: container
wslc:
  command: '$wslcConfig'
container: unused-command-fixture
"@)

    # Preserve argv through the native invocation, but give every command a deadline,
    # including image building, failed-create diagnostics and finally-block cleanup.
    function Invoke-CommandFixture([string] $Exe, [string[]] $Arguments, [int] $TimeoutSeconds = 180) {
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
        finally {
            Remove-Job $job -Force -ErrorAction SilentlyContinue
        }
    }

    function Invoke-CommandWip([string[]] $Arguments) {
        Invoke-CommandFixture -Exe $script:WipPath -Arguments (@('--config', $configPath) + $Arguments)
    }

    try {
        Write-Step 'sandbox command replaces a one-shot image CMD'
        Assert-Exit (Invoke-CommandFixture -Exe $script:WslcPath -Arguments @('build', '-t', $commandImage, $buildDirectory) -TimeoutSeconds 300) 0 'build the one-shot CMD fixture'

        $sandboxes.Add('command-override')
        Assert-Exit (Invoke-CommandWip @('sandbox', 'create', 'command-override')) 0 'create with a declared resident command'
        $status = Invoke-CommandWip @('sandbox', 'status', 'command-override')
        Assert-Exit $status 0 'status of the command override'
        Assert-Match $status '(?m)^command-override\s+running\s+wip-s-[a-f0-9]{40}\s+\S+\s*$' 'the overridden sandbox is running'
        $mainProcess = Invoke-CommandWip @('sandbox', 'exec', 'command-override', '--', 'cat', '/proc/1/comm')
        Assert-Exit $mainProcess 0 'read the overridden main process'
        Assert-Match $mainProcess '^sleep\s*$' 'the declared command replaced the one-shot CMD'
        Assert-Exit (Invoke-CommandWip @('sandbox', 'exec', 'command-override', '--', 'sleep', '1')) 0 'the overridden sandbox stays available'
        $stillRunning = Invoke-CommandWip @('sandbox', 'status', 'command-override')
        Assert-Exit $stillRunning 0 'status after another command'
        Assert-Match $stillRunning '(?m)^command-override\s+running\s' 'the resident command remains running'

        Write-Step 'without command the same image keeps its one-shot default'
        $sandboxes.Add('command-default')
        $defaultCreate = Invoke-CommandWip @('sandbox', 'create', 'command-default')
        Assert-NonZero $defaultCreate 'create without a command override'
        Assert-Match $defaultCreate 'creation/start did not produce a running sandbox' 'failure identifies the exited one-shot CMD'
        $defaultStatus = Invoke-CommandWip @('sandbox', 'status', 'command-default')
        Assert-Exit $defaultStatus 0 'status of the default one-shot sandbox'
        Assert-Match $defaultStatus '(?m)^command-default\s+exited\s+wip-s-[a-f0-9]{40}\s+\S+\s*$' 'without command the image default exits'
    }
    finally {
        foreach ($sandbox in $sandboxes) {
            try {
                $cleanup = Invoke-CommandWip @('sandbox', 'destroy', $sandbox)
                if ($cleanup.Code -ne 0) {
                    $script:Failed = $true
                    Write-Warning "command fixture cleanup failed (${sandbox}): $($cleanup.Output.Trim())"
                }
            }
            catch {
                $script:Failed = $true
                Write-Warning "command fixture cleanup failed (${sandbox}): $_"
            }
        }
    }
}
