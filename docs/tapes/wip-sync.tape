# Prepare this take as described in README.md.
Output docs/tapes/wip-sync.gif

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

# Change the host file while the foreground watcher runs.
Hide
Type@0ms "$edit = Start-Job -ArgumentList (Join-Path $PWD 'source/message.txt') -ScriptBlock { param($path) Start-Sleep -Seconds 4; Set-Content -LiteralPath $path -Value 'updated on host' -Encoding ascii }; Clear-Host"
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
