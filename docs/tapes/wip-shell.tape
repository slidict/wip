# Prepare this take as described in README.md.
Output docs/tapes/wip-shell.gif

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

Type "wip shell"
Enter
Sleep 1500ms

Type "echo inside the container"
Enter
Sleep 1s

Type "exit"
Enter
Sleep 1s
