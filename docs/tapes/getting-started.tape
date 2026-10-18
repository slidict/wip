# Prepare this take as described in README.md.
Output docs/tapes/getting-started.gif

Require wip
Require bash

Set Shell "bash"
Set FontSize 18
Set Width 1200
Set Height 680
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 60ms
Env PS1 "$ "
Env PROMPT_COMMAND ""

Hide
Type@0ms "export PS1='$ '; unset PROMPT_COMMAND; clear"
Enter
Sleep 500ms
Show

Type "wip init"
Enter
Sleep 500ms

Hide
Type@0ms "cp recording.yml wip.yml; clear"
Enter
Sleep 500ms
Show

Type "wip doctor"
Enter
Sleep 2s

Type "wip build"
Enter
Sleep 3s

Type "wip up -d"
Enter
Sleep 5s

Type "wip hello"
Enter
Sleep 1s

Type "wip down"
Enter
Sleep 1500ms
