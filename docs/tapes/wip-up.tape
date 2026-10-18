# Prepare this take as described in README.md.
Output docs/tapes/wip-up.gif

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

# The sidecar takes two seconds to become healthy.
Type "wip up -d"
Enter
Sleep 7s
