# Prepare this take as described in README.md.
Output docs/tapes/wip-ps.gif

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

Type "wip ps"
Enter
Sleep 4s

Hide
Type@0ms "wip down; wslc volume remove wip-tapes-src"
Enter
Sleep 15s
