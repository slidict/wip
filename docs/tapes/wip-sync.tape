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

# Wait for the edit job to initialize before starting its three-second countdown.
Hide
Type@0ms "$ready = Join-Path $PWD '.edit-ready'; $go = Join-Path $PWD '.edit-go'; Remove-Item -LiteralPath $ready,$go -Force -ErrorAction SilentlyContinue; $edit = Start-Job -ArgumentList (Join-Path $PWD 'source/message.txt'),$ready,$go -ScriptBlock { param($path,$ready,$go) Set-Content -LiteralPath $ready -Value ready; while (-not (Test-Path -LiteralPath $go)) { Start-Sleep -Milliseconds 50 }; Start-Sleep -Seconds 3; Set-Content -LiteralPath $path -Value 'updated on host' -Encoding ascii }; $deadline = (Get-Date).AddSeconds(10); while (-not (Test-Path -LiteralPath $ready)) { if ((Get-Date) -gt $deadline) { throw 'Edit job did not start' }; Start-Sleep -Milliseconds 50 }; Clear-Host"
Enter
Sleep 12s

Type@0ms "Set-Content -LiteralPath $go -Value go; Clear-Host"
Enter
Sleep 500ms
Show

Type "wip sync --watch"
Enter
Sleep 8s

Ctrl+C
Sleep 500ms

Type "wip exec -- cat /app/message.txt"
Enter
Sleep 3s
