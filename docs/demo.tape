# VHS tape for the wip README demo.
# Install VHS (https://github.com/charmbracelet/vhs) and run:
#   vhs docs/demo.tape
# from a directory containing the wip.yml shown in docs/demo-wip.yml.

Output docs/demo.gif

Set Shell "bash"
Set FontSize 18
Set Width 1200
Set Height 680
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 60ms

Type "wip doctor"
Enter
Sleep 1500ms

Type "wip up -d"
Enter
Sleep 2500ms

Type "wip hello"
Enter
Sleep 1500ms

Type "wip exec -- cat /etc/os-release"
Enter
Sleep 1500ms

Type "wip down"
Enter
Sleep 1500ms
