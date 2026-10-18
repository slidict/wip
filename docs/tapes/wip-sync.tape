# Prepare this take as described in README.md.
Output docs/tapes/wip-sync.gif

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

# Change the host file while the foreground watcher runs.
Hide
Type@0ms "(sleep 4; printf 'updated on host\\n' > source/message.txt) >/dev/null 2>&1 & clear"
Enter
Sleep 500ms
Show

Type "wip sync --watch"
Enter
Sleep 6s

Ctrl+C
Sleep 500ms

Type "wip exec -- cat /app/message.txt"
Enter
Sleep 1500ms
