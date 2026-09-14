Set-Location 'C:\Users\yusuk\codes\wip\benchmark\scripts'
. .\common.ps1
.\Measure-Storage.ps1 -Backend docker -OutDir 'C:\Users\yusuk\codes\wip\benchmark\results\20260915-080029-pilot'
.\Measure-Storage.ps1 -Backend wip -OutDir 'C:\Users\yusuk\codes\wip\benchmark\results\20260915-080029-pilot'
