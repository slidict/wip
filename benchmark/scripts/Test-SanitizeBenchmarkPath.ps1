<#
.SYNOPSIS
  Unit and integration tests for benchmark path sanitization (Issue slidict/workspace#383).
#>

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$passed = 0
$failed = 0

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    if ($Actual -eq $Expected) {
        $script:passed++
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        $script:failed++
        Write-Host "FAIL: $Message" -ForegroundColor Red
        Write-Host "  Expected: '$Expected'"
        Write-Host "  Actual:   '$Actual'"
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        $script:passed++
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        $script:failed++
        Write-Host "FAIL: $Message" -ForegroundColor Red
    }
}

function Test-NoPrivatePaths {
    param([string]$Text, [string]$RepoRoot)
    # Accept the explicit neutral placeholder, never an arbitrary account segment.
    $profile = '(?i)(?:\b[A-Z]:[\\/]+Users[\\/]+|/mnt/[a-z]/Users/)(?!<user>(?:[\\/\s"'']|$))[^\\/\s"'']+'
    if ($Text -match $profile) { return $false }
    $root = $RepoRoot.TrimEnd('\', '/')
    $variants = @($root, $root.Replace('\', '\\'), $root.Replace('\', '/'))
    if ($root -match '^([A-Za-z]):[\\/](.*)$') {
        $variants += '/mnt/' + $matches[1].ToLower() + '/' + $matches[2].Replace('\', '/')
    }
    foreach ($variant in $variants) {
        if ($Text -match [regex]::Escape($variant)) { return $false }
    }
    return $true
}

Write-Host "Running Benchmark Path Sanitization Tests..." -ForegroundColor Cyan

# Test 1: Windows repository path normalization
$dummyRepo = 'C:\Users\Alice\projects\wip'
$inputWin = 'C:\Users\Alice\projects\wip\benchmark\app'
$resultWin = Get-SanitizedPath -Path $inputWin -RepoRoot $dummyRepo
Assert-Equal $resultWin '<repo-root>\benchmark\app' 'Windows repo path replaced with <repo-root>'

# Test 2: WSL repository path normalization
$inputWsl = '/mnt/c/Users/Alice/projects/wip/benchmark/app'
$resultWsl = Get-SanitizedPath -Path $inputWsl -RepoRoot $dummyRepo
Assert-Equal $resultWsl '/mnt/c/<repo-root>/benchmark/app' 'WSL repo path replaced with /mnt/c/<repo-root>'

# Test 3: JSON-escaped Windows repository path normalization
$inputJson = 'C:\\Users\\Alice\\projects\\wip\\benchmark\\app'
$resultJson = Get-SanitizedPath -Path $inputJson -RepoRoot $dummyRepo
Assert-Equal $resultJson '<repo-root>\\benchmark\\app' 'JSON-escaped Windows repo path replaced with <repo-root>'

# Test 4: External user profile Windows path normalization
$inputExtWin = 'C:\Users\Bob\AppData\Local\Docker\wsl\disk\docker_data.vhdx'
$resultExtWin = Get-SanitizedPath -Path $inputExtWin -RepoRoot $dummyRepo
Assert-Equal $resultExtWin 'C:\Users\<user>\AppData\Local\Docker\wsl\disk\docker_data.vhdx' 'External Windows user path replaced with C:\Users\<user>'

# Test 5: External WSL user path normalization
$inputExtWsl = '/mnt/c/Users/Charlie/external/app'
$resultExtWsl = Get-SanitizedPath -Path $inputExtWsl -RepoRoot $dummyRepo
Assert-Equal $resultExtWsl '/mnt/c/Users/<user>/external/app' 'External WSL user path replaced with /mnt/c/Users/<user>'

# Test 6: Log line path sanitization
$inputLog = 'environment.json written to C:\Users\Alice\projects\wip\benchmark\results\20261006-1200\environment.json'
$resultLog = Get-SanitizedPath -Path $inputLog -RepoRoot $dummyRepo
Assert-Equal $resultLog 'environment.json written to <repo-root>\benchmark\results\20261006-1200\environment.json' 'Log line repo path sanitized'

# Test 7: Verify committed artifacts have no username leaks
$resultsDir = Join-Path $PSScriptRoot '..\results\20260915-2252'
$envJson = Get-Content -Raw -Path (Join-Path $resultsDir 'environment.json')
$reportMd = Get-Content -Raw -Path (Join-Path $resultsDir 'report.md')
$runLog = Get-Content -Raw -Path (Join-Path $resultsDir 'run.log')

Assert-True ($envJson -notmatch '(?i)Users[\\\/][a-z0-9_-]+[\\\/]codes') 'environment.json has no user-identifying repository paths'
Assert-True (Test-NoPrivatePaths $envJson (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path) 'environment.json contains no private profile or repository path'
Assert-True ($envJson -match '<repo-root>') 'environment.json contains <repo-root> placeholder'

Assert-True ($reportMd -notmatch '(?i)Users[\\\/][a-z0-9_-]+[\\\/]codes') 'report.md has no user-identifying repository paths'
Assert-True (Test-NoPrivatePaths $reportMd (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path) 'report.md contains no private profile or repository path'
Assert-True ($reportMd -match '<repo-root>') 'report.md contains <repo-root> placeholder'

Assert-True ($runLog -notmatch '(?i)Users[\\\/][a-z0-9_-]+[\\\/]codes') 'run.log has no user-identifying repository paths'
Assert-True (Test-NoPrivatePaths $runLog (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path) 'run.log contains no private profile or repository path'
Assert-True ($runLog -match '<repo-root>') 'run.log contains <repo-root> placeholder'

# Test 8: Verify SKILL.md has no user path leaks
$skillMd = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\..\.claude\skills\wip-benchmark\SKILL.md')
Assert-True (Test-NoPrivatePaths $skillMd (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path) 'SKILL.md contains no private profile or repository path'

# Prove that the artifact assertion rejects leaks from any contributor, not one name.
$fixtureRoot = 'D:\checkout\wip'
foreach ($leak in @('C:\Users\ExampleContributor\AppData', 'D:\\Users\\OtherContributor\\data', '/mnt/d/Users/ThirdContributor/data', 'D:\checkout\wip\benchmark', 'D:\\checkout\\wip\\benchmark', '/mnt/d/checkout/wip/benchmark', 'D:/checkout/wip/benchmark')) {
    Assert-True (-not (Test-NoPrivatePaths $leak $fixtureRoot)) 'Artifact validator rejects a synthetic private path'
}
foreach ($safe in @('C:\Users\<user>\AppData', 'C:\\Users\\<user>\\AppData', '/mnt/d/Users/<user>/data', '<repo-root>\benchmark', '/mnt/d/<repo-root>/benchmark')) {
    Assert-True (Test-NoPrivatePaths $safe $fixtureRoot) 'Artifact validator accepts a neutral placeholder'
}

Write-Host "Results: $passed Passed, $failed Failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
if ($failed -gt 0) {
    exit 1
}
exit 0
