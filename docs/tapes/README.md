# Wiki terminal recordings

These seven VHS tapes record real commands with a shared fixture on Windows,
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

The app image `wip-tapes-app:latest` is built locally from the public
`ubuntu:latest` image; the sidecar and sandbox use that public image directly.
The warm build installs rsync before recording. No registry credentials or
private images are required. A nonempty `.dockerignore` enables context-copy
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
```

Or generate all seven sequentially, stopping on failure:

```bash
for slug in getting-started wip-up wip-build wip-shell wip-logs wip-sync wip-sandbox; do
  bash docs/tapes/prepare.sh "$slug" || break
  vhs "docs/tapes/$slug.tape" || break
done
```

The getting-started take begins without `wip.yml`. After showing `wip init`,
it installs `recording.yml` while hidden: the generated starter contains a
placeholder image and must be customized before use. Other tapes start with
the fixture installed; shell, logs, and sync start with the app already running.
The sync take uses PowerShell `Start-Job` to schedule a host edit after four
seconds. It passes an absolute file path because jobs use a separate working
directory. The foreground watcher runs while the job edits the file, then the tape
reads `/app/message.txt` inside the container after Ctrl-C to show the result.

Outputs are `docs/tapes/<slug>.gif` in the recording directory. Copy them to
the separate delivery destination. Do not commit GIFs; `.gitignore` excludes
them. Only copy changed source assets back into the checkout.

After recording, run `wip down` and `wip sandbox destroy demo` with the fixture
installed. The shared sync volume and the per-take networks remain; remove the
unused `wip-tapes-src` volume and `wip-tapes-*` networks with the backend's
resource management commands once all fixture containers have been removed.

## Retake after a behavior change

| Tape / GIF slug | Wiki page | Recorded behavior |
| --- | --- | --- |
| getting-started | Getting Started | init, customize, doctor, build, up -d, hello, down |
| wip-up | wip up | network, sidecar, healthcheck wait, app, completion |
| wip-build | wip build | context-copy progress and completed build |
| wip-shell | wip shell | interactive shell, command, exit |
| wip-logs | wip logs | live heartbeats with -f, Ctrl-C |
| wip-sync | wip sync | --watch, host edit, mirrored content, Ctrl-C |
| wip-sandbox | wip sandbox | create, status, list, exec --interactive, stop, destroy |

When a command or output changes, edit the corresponding tape in this table.
Check flags in `src/Wip.Cli/Program.cs` and behavior in
`src/Wip.Cli/CliContext.cs` or `CliContext.Sandbox.cs`. Update the shared fixture
and preparation script if inputs change, then regenerate every affected tape
using its prepare/render pair above. Keep the seven visual settings aligned
with `docs/demo.tape`, except for the required PowerShell shell.

On the Windows recording host, `Set Shell "bash"` produced a successful VHS
exit and an empty GIF with only the background, even for a minimal echo tape.
`Set Shell "powershell"` rendered that minimal tape correctly. Keep PowerShell
when retaking these recordings; Bash is used only for preparation outside VHS.

Visible typing and sleeps budget roughly 6-18 seconds per take before VHS key
overhead. The sidecar waits two seconds; health probes poll once per second.
The watcher syncs once per second and observes a host edit after four seconds.
Build and startup use fixed `Sleep` budgets of 3-7 seconds, not `Wait`.
These budgets are source-derived and remain unverified on the recording host.
Preview each GIF: increase its `Sleep` if a command is unfinished, and reduce
idle time if needed to aim for 20 seconds. A longer host operation takes
precedence over the duration target; never hide an unfinished command.
For visible copy progress on fast disks, enlarge `context/payload/` after
preparation; the first and final counters may otherwise appear almost together.

Before delivery, inspect prompts and all output for personal paths or names.
The host prompt is fixed to `$ ` and the container prompt to `container$ `;
choose a neutral recording path because build and sync print absolute paths.
Use a clean recording account if diagnostics print machine-specific details.
