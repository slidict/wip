Set-Location 'C:\Users\yusuk\codes\wip\benchmark\scripts'
. .\common.ps1
.\Invoke-Benchmark.ps1 -OutDir 'C:\Users\yusuk\codes\wip\benchmark\results\20260914-225724-pilot' -Configs windows-docker,windows-wip,wsl-docker,wsl-wip -Rounds 1 -Warmups 0 -BaselineWaitSec 15 -IdleWaitSec 15 -LoadDurationSec 30 -LoadConcurrency 20
