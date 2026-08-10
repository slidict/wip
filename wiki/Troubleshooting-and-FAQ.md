# Troubleshooting & FAQ

Start here when something failed. Each error has its own page with cause, fix, and verification.

## First step, always

```bash
wip doctor
```

It checks WSL2, Windows interop, the `wslc` binary, your configuration, the compose file, and the
sync source in one pass. See [wip doctor](wip-doctor) for what every line means.

Then, if a specific command is failing:

```bash
wip <the failing command> --debug
```

See [Debug Output](Debug-Output).

## By error message

| Message (or symptom) | Page |
|---|---|
| `WSLC was not found. Checked: …` | [WSLC Not Found](WSLC-Not-Found) |
| `pull access denied`, `insufficient_scope`, `authorization failed` | [Registry Authentication](Registry-Authentication) |
| `no matching manifest for linux/amd64` (or `arm64`) | [Architecture Mismatch](Architecture-Mismatch) |
| `0x8007000e`, "too many mounted volumes" | [Volume Limit Reached](Volume-Limit-Reached) |
| `rsync: not found`, `executable file not found … rsync` | [rsync Not Found](rsync-Not-Found) |
| `wip.yml was not found (searched from … to the filesystem root)` | [Config File Discovery](Config-File-Discovery) |
| Anything mentioning a `wip.yml` / `compose.yml` key | [Configuration Errors](Configuration-Errors) |
| `Unknown command: NAME` | [wip dispatch](wip-dispatch) |
| `` `wip logs` is only available in compose mode `` | [wip logs](wip-logs) |

## By symptom

| Symptom | Where to look |
|---|---|
| Boot takes minutes; CPU/memory/IO all idle | [Fixing a Slow Boot](Fixing-a-Slow-Boot) |
| Every build re-sends the whole source tree | [Shadow Build Context](Shadow-Build-Context), [Dockerignore](Dockerignore) |
| The app can't resolve `db` / `redis` by hostname | [Networking](Networking) |
| A new `ports:` / `volumes:` / `env:` entry has no effect | `wip down && wip up -d` — see [wip down](wip-down) |
| Files the container writes keep disappearing | [Source Sync](Source-Sync) — one-way mirror with `--delete` |
| Host edits don't reach the container | [Continuous Sync](Continuous-Sync) |
| `rails console` exits immediately with an EOF error | [TTY Allocation](TTY-Allocation) |
| `wip <name>` runs a built-in instead of my command | [wip dispatch](wip-dispatch) |
| `wip up --watch` never restarts anything | [Restart Policies](Restart-Policies) |
| A compose service is missing from `wip config` | [Compose Profiles](Compose-Profiles) |
| A `${VAR}` in compose.yml came out empty | [Compose Variable Interpolation](Compose-Variable-Interpolation) |
| `wip run` says it's exec'ing instead | [Compose Mode](Compose-Mode) — expected there |

## Environment problems

### `[FAIL] Not running on WSL2`

wip couldn't find WSL2 markers in `/proc/version` (or, on native Windows, `wsl.exe --status`
failed). Check your distro is WSL2, not WSL1:

```powershell
wsl -l -v
wsl --set-version <distro> 2
```

### `[FAIL] Windows executable interoperability is disabled`

WSL can't launch `wslc.exe` at all. Usually `/etc/wsl.conf`:

```ini
[interop]
enabled = true
appendWindowsPath = true
```

Then `wsl --shutdown` from Windows and reopen the shell.

## Questions rather than errors

See [FAQ](FAQ).

## Nothing here covers it

See [Reporting Issues](Reporting-Issues) for what to include in a bug report.
