# Prepare this take as described in README.md.
Output docs/tapes/wip-security-scan.gif

Require wip

Set Shell "powershell"
Set FontSize 18
Set Width 1200
Set Height 680
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 60ms

Hide
Type@0ms "function global:prompt { '$ ' }; $target = (Get-Content -LiteralPath .target-url -Raw).Trim(); Remove-Item -LiteralPath scan-out -Recurse -Force -ErrorAction SilentlyContinue; Clear-Host"
Enter
Sleep 500ms
Show

Type "wip security tools"
Enter
Sleep 1s

Type "wip sandbox create scanner"
Enter
Sleep 3s

Type "wip security scan scanner --tool zap --target $target --output scan-out"
Enter
# Measured at about 35s against the fixture; 75s leaves margin on a slower host so the
# take cannot end mid-scan, which the first recording did at 25s.
# Wait+Screen /Automation plan succeeded!/ would remove the guess, but VHS 0.12.1 ends the
# recording with "recording failed" on this tape, so the margin stays explicit.
Sleep 75s

Type "wip sandbox status scanner"
Enter
Sleep 1s

Type "wip sandbox destroy scanner"
Enter
Sleep 3s
