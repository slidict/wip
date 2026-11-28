# Prepare this take as described in README.md.
Output docs/tapes/wip-init.gif

Require wip

Set Shell "powershell"
Set FontSize 18
Set Width 1200
Set Height 680
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 60ms

Hide
Type@0ms "function global:prompt { '$ ' }; Set-Location -LiteralPath ((Get-Content -LiteralPath .init-directory -Raw).Trim()); Clear-Host"
Enter
Sleep 500ms
Show

Type "wip init"
Enter
Sleep 1s

Type "Get-Content -Encoding UTF8 wip.yml"
Enter
Sleep 5s

Type@30ms "Get-Content -Encoding UTF8 wip.yml -TotalCount 22"
Enter
Sleep 4s
