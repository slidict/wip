# Prepare this take as described in README.md.
Output docs/tapes/wip-logs.gif

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

Type "wip logs -f"
Enter
Sleep 5s

Ctrl+C
Sleep 1s
