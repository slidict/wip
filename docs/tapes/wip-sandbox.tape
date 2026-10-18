# Prepare this take as described in README.md.
Output docs/tapes/wip-sandbox.gif

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

Type "wip sandbox create demo"
Enter
Sleep 3s

Type "wip sandbox status demo"
Enter
Sleep 500ms

Type "wip sandbox list"
Enter
Sleep 500ms

Type "wip sandbox exec --interactive demo -- sh"
Enter
Sleep 1500ms

Type "echo hello"
Enter
Sleep 500ms

Type "exit"
Enter
Sleep 500ms

Type "wip sandbox stop demo"
Enter
Sleep 1500ms

Type "wip sandbox destroy demo"
Enter
Sleep 2s
