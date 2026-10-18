# Prepare this take as described in README.md.
Output docs/tapes/getting-started.gif

Require wip

Set Shell "powershell"
Set FontSize 18
Set Width 1200
Set Height 680
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 60ms

Hide
Type@0ms "function global:prompt { '$ ' }; Clear-Host"
Enter
Sleep 500ms
Show

Type "wip init"
Enter
Sleep 250ms

Hide
Type@0ms "Copy-Item -LiteralPath recording.yml -Destination wip.yml -Force; Clear-Host"
Enter
Sleep 500ms
Show

Type "wip doctor"
Enter
Sleep 750ms

Type "wip build"
Enter
Sleep 3s

Type "wip up -d"
Enter
Sleep 8s

Type "wip hello"
Enter
Sleep 2s

Type "wip down"
Enter
Sleep 2s
