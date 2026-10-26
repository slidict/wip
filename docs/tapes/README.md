# Wiki terminal recordings

These eight VHS tapes record real commands with a shared fixture on Windows,
wip's shipping target. The tape shell is PowerShell. GIF rendering runs on the
recording host; these revised tapes have not been rendered in this environment.

## Tools

The host-tested versions are VHS 0.12.1, ttyd 1.7.7, and FFmpeg 9.0.2,
on Windows 11 with wip 2.12.0 and WSLC 3.0.3.0. Install them from the
[VHS instructions](https://github.com/charmbracelet/vhs#installation),
[ttyd releases](https://github.com/tsl0922/ttyd/releases), and
[FFmpeg downloads](https://ffmpeg.org/download.html).
The VHS README requires ttyd and FFmpeg on `PATH` but specifies no minimum
versions for them. Record the actual versions with `vhs --version`,
`ttyd --version`, and `ffmpeg -version` when handing off a recording.
For example, install the recording tools with Scoop: `scoop install vhs ttyd ffmpeg`.

Also install `wip` itself and a working WSLC backend. Check `wip version` and
`wip doctor` before recording. PowerShell must be available for the tapes.
Run `prepare.sh` and the command blocks below from Git Bash or another Bash
with standard utilities (`cp`, `sed`, `date`, `mkdir`, `mv`, `touch`).
Use an English terminal and backend.

## Prepare the recording directory

Use a dedicated directory outside the checkout and outside a user profile.
For example, use `C:/wip-demo` from Git Bash on a Windows recording host.
Bind-mount sources must be accessible to the backend; avoid a WSL filesystem
path when WSLC expects a Windows filesystem path.

From the repository root, copy the assets:

```bash
mkdir -p C:/wip-demo/docs
cp -R docs/tapes C:/wip-demo/docs/
cd C:/wip-demo
touch .wip-tapes-recording
```

Run every command below from this recording directory. `prepare.sh` copies
`docs/tapes/wip.yml` to `wip.yml`, gives each take a fresh network name, creates
`context/` and `source/message.txt`, and warms the build cache. It removes only
the fixture's containers and declared sandbox from the previous take, continuing
if cleanup fails (including when those resources are absent). Build and startup
failures still stop preparation. Reserve
the `wip-tapes` resource names for this fixture. The marker guards against
accidentally preparing a normal project directory.

The app and sandbox use `wip-tapes-app:latest`, built locally from the public
`ubuntu:latest` image. Its `CMD` traps SIGTERM/INT, terminates its child sleep,
and waits for it before exiting, so the sandbox stays alive and stops promptly;
the app overrides that command with its heartbeat loop. The sidecar uses Ubuntu
directly. Preparation builds the image with rsync, then runs `wip up -d` and
`wip down` to warm dependency images before recording. Warmup reuses the fixed
`wip-tapes-warm` network. It selects one fresh network per take afterward so
visible startup still shows network creation. No registry
credentials or private images are required. A nonempty `.dockerignore` enables context-copy
progress even on a local filesystem, and a changed payload forces the visible
build's one-second final step to run again.

## Generate

Prepare again before **each** take, including a retry:

```bash
bash docs/tapes/prepare.sh getting-started
vhs docs/tapes/getting-started.tape
bash docs/tapes/prepare.sh wip-up
vhs docs/tapes/wip-up.tape
bash docs/tapes/prepare.sh wip-build
vhs docs/tapes/wip-build.tape
bash docs/tapes/prepare.sh wip-shell
vhs docs/tapes/wip-shell.tape
bash docs/tapes/prepare.sh wip-logs
vhs docs/tapes/wip-logs.tape
bash docs/tapes/prepare.sh wip-sync
vhs docs/tapes/wip-sync.tape
bash docs/tapes/prepare.sh wip-sandbox
vhs docs/tapes/wip-sandbox.tape
bash docs/tapes/prepare.sh wip-security-scan
vhs docs/tapes/wip-security-scan.tape
```

Or generate all eight sequentially, stopping on failure:

```bash
for slug in getting-started wip-up wip-build wip-shell wip-logs wip-sync wip-sandbox wip-security-scan; do
  bash docs/tapes/prepare.sh "$slug" || break
  vhs "docs/tapes/$slug.tape" || break
done
```

The getting-started take begins without `wip.yml`. After showing `wip init`,
it installs `recording.yml` while hidden: the generated starter contains a
placeholder image and must be customized before use. Other tapes start with
the fixture installed; shell, logs, and sync start with the app already running.
The security-scan take appends an internal `scan` network plus `target` and
`scanner` sandboxes using the digest-pinned ZAP image, warms the image by
creating `target` (which serves `/tmp/index.html` over HTTP on port 8080), and
writes `target`'s internal IPv4 URL to `.target-url` before recording.
The sync take uses PowerShell `Start-Job` with absolute file paths because jobs
use a separate working directory. A hidden readiness marker confirms startup
before a second marker starts the three-second edit countdown; a 12-second
hidden pause covers job startup and its ten-second readiness timeout. The watcher
runs for eight seconds while the job edits the file, then the tape
reads `/app/message.txt` inside the container after Ctrl-C to show the result.

Outputs are `docs/tapes/<slug>.gif` in the recording directory. After inspecting
them, copy all eight GIFs into the checkout's `docs/tapes/` and commit them with
any changed source assets. Wiki pages reference the committed GIFs through
`raw.githubusercontent.com`; after the commit reaches main, use:

`![wip up demo](https://raw.githubusercontent.com/slidict/wip/main/docs/tapes/wip-up.gif)`

After recording, run `wip down` and `wip sandbox destroy demo` (and
`wip sandbox destroy scanner` / `wip sandbox destroy target` after the security
scan take) with the fixture installed. Remove the unused `wip-tapes-src` volume
with the backend's volume management commands once all fixture containers have
been removed.

`wip down` leaves networks behind. Warmup now reuses one network, but each take
still adds one: eight takes leave nine networks on a clean host. Remove unused
fixture networks after each batch and before retrying if older batches remain.
Accumulated networks exhaust the default IPv4 address pools: startup fails with
"could not find an available, non-overlapping IPv4 address pool".

From the recording host, run `wslc network list`, then `wslc network rm NAME`
for each unused `wip-tapes-*` network, replacing `NAME` with its exact listed
name. Include older warmup networks and take networks. After the final batch,
remove the shared warmup network with `wslc network rm wip-tapes-warm` too.
Only remove fixture networks after their containers have been removed; keep
unrelated networks. The command follows the
[WSLC network cleanup example](https://github.com/microsoft/WSL/blob/master/test/windows/wslc/e2e/WSLCE2EHelpers.cpp).

## Retake after a behavior change

| Tape / GIF slug | Wiki page | Recorded behavior |
| --- | --- | --- |
| getting-started | Getting Started | init, customize, doctor, build, up -d, hello, down |
| wip-up | wip up | network, sidecar, healthcheck wait, app, completion |
| wip-build | wip build | context-copy progress and completed build |
| wip-shell | wip shell | fixture's interactive shell, container$ prompt, one-line echo, exit |
| wip-logs | wip logs | live heartbeats with -f, Ctrl-C |
| wip-sync | wip sync | --watch, host edit, mirrored content, Ctrl-C |
| wip-sandbox | wip sandbox | create, status, list, exec --interactive, stop, destroy |
| wip-security-scan | wip security scan | security tools, sandbox create, security scan --tool zap --output, status, destroy |

When a command or output changes, edit the corresponding tape in this table.
Check flags in `src/Wip.Cli/Program.cs` and behavior in
`src/Wip.Cli/CliContext.cs`, `CliContext.Sandbox.cs`, or
`CliContext.Security.cs`. Update the shared fixture
and preparation script if inputs change, then regenerate every affected tape
using its prepare/render pair above. Keep the six visual settings aligned
with `docs/demo.tape`, except for the required PowerShell shell.
Inspect each retake, copy the regenerated GIF back into `docs/tapes/`, and
commit the updated GIF with its tape or fixture changes. Keep the same slug so
the Wiki's raw URL continues to work.

The fixture's `commands.shell` overrides the built-in bash/sh fallback.
It explicitly sets `interactive: true`: custom commands otherwise inherit
`interactive: false`, causing the shell to exit without accepting input.
`CliContext.Dispatch` and `CommandBuilder.Custom` use this setting; both require
`WindowsEnvironment.IsInteractive` (stdin and stdout must not be redirected)
to inherit the terminal and pass `-it` to the backend. The exec CLI's
`--interactive` default does not apply to a custom shell command. Record directly
in VHS's ttyd terminal without piping wip. The fixture uses bash `-i` without
profile files so `container$ ` remains distinct from the host's `$ ` prompt.
Accept the shell take only if `echo inside the container` prints one line
inside that container prompt, then `exit` returns to `$ `.

On the Windows recording host, `Set Shell "bash"` produced a successful VHS
exit and an empty GIF with only the background, even for a minimal echo tape.
`Set Shell "powershell"` rendered that minimal tape correctly. Keep PowerShell
when retaking these recordings; Bash is used only for preparation outside VHS.

Visible typing and sleeps budget roughly 6-20 seconds per take before VHS key
overhead. The sidecar waits two seconds; health probes poll once per second.
The watcher syncs once per second. Getting-started allows eight seconds for
startup, two seconds for hello, and two seconds for down; init and doctor have
shorter display times. Sync allows three seconds for the final cat output.
The sandbox allows two seconds for creation and three seconds each for stop
and destroy. Status and list use 30ms typing and 250ms display pauses to keep
the nominal visible duration under 20 seconds. The shell has a 2.5-second entry
pause. These fixed `Sleep` budgets, without `Wait`, still need verification on
the recording host for the revised shell and sandbox takes.
Preview each GIF: increase its `Sleep` if a command is unfinished, and reduce
idle time if needed to aim for 20 seconds. A longer host operation takes
precedence over the duration target; never hide an unfinished command.
For visible copy progress on fast disks, enlarge `context/payload/` after
preparation; the first and final counters may otherwise appear almost together.

Before delivery, inspect prompts and all output for personal paths or names.
The host prompt is fixed to `$ ` and the container prompt to `container$ `;
choose a neutral recording path because build and sync print absolute paths.
Use a clean recording account if diagnostics print machine-specific details.

## Recording notes from the Windows run

All seven tapes were attempted on the host: up, build, and logs rendered
successfully. Shell needed a Type string without escaped nested quotes. The
sandbox needed an image with a persistent CMD. Getting-started needed warm
dependency images and more startup time; sync needed job-startup coordination
and more time for both the second sync line and final cat output. These four
revisions were recorded again: getting-started reached hello and down without
garbled pull output, sync reached `updated on host`, and sandbox reached its
interactive exec and exit. Up, build, and logs remained successful. Shell still
ran echo on the host because its custom command lacked `interactive: true`;
the fixture now enables it, and the next host run confirmed `container$ `,
one-line echo output, and return to the host prompt. Sandbox still cut off at
stop: measured create/stop/destroy times were 0.87/10.36/0.90 seconds. Its
previous PID 1 sleep ignored SIGTERM until the ten-second kill timeout; the
revised CMD traps the signal and terminates and reaps its child. Keep the
three-second stop and destroy pauses and re-record sandbox to verify the fix.
Repeated batches also exhausted network pools; warmup now reuses one network,
and the cleanup steps above cover networks retained after recording. Avoid escaped
double quotes inside VHS Type strings; use plain text or a different outer
quote delimiter.

Record with a Windows account whose UI display language is English. wip's
error display follows the Windows UI language: `DisplayLanguage.cs` reads
`GetUserDefaultUILanguage`, so environment variables cannot switch it to English.
Never accept a GIF containing errors. Warm dependency images during preparation:
a pull during recording can introduce backend output, including garbled text on
a host with a different display language. Confirm that getting-started reaches
down, sync shows a second sync line and `updated on host`, and sandbox reaches
destroy before delivering the revised recordings.
