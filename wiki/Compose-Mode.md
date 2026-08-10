# Compose Mode

`mode: compose` makes wip a thin bridge to a third-party compose-for-`wslc` binary that you
install and name yourself. wip builds `<command> -f FILE [-p PROJECT] up|down|stop|exec|logs`
argument arrays; the external tool does the orchestration.

Prefer this when you already run such a tool, or when your `compose.yml` uses features outside
[what compose-native supports](Compose-File-Support). Otherwise see
[Compose Native Mode](Compose-Native-Mode).

## Config

```yaml
version: 1
mode: compose              # required; a compose: block without it is an error
compose:
  service: app             # required: which service wip run/exec/NAME target
  command: wslc-compose    # required: the compose-for-wslc binary or path you installed
  file: compose.yml        # optional; auto-detected next to wip.yml otherwise
  project: myapp           # optional; omitted lets the compose tool pick its default
```

### Key reference

| Key | Required | Default | Notes |
|---|---|---|---|
| `compose.service` | yes | — | must not be empty |
| `compose.command` | yes | — | no default: wip doesn't favor any implementation |
| `compose.file` | no | auto-detected | relative paths resolve against `wip.yml`, not the cwd |
| `compose.project` | no | unset | passed as `-p` only when set |

Auto-detection looks for, in order: `compose.yml`, `compose.yaml`, `docker-compose.yml`,
`docker-compose.yaml`, next to `wip.yml`. None found is a `ConfigError`.

`compose:` is mutually exclusive with `dependencies:` and `network:`.

## Why `compose.command` has no default

`wslc` has no native Compose support yet (tracked upstream in
[microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)), and independent third-party
tools fill the gap. wip deliberately doesn't pick a winner — unlike `wslc.command`, which defaults
to `auto` and searches. Set `compose.command` to whichever binary name or absolute path you have.
Candidates: [Third Party Compose Tools](Third-Party-Compose-Tools).

Whatever you point it at must understand `-f FILE [-p PROJECT] up|down|stop|exec|logs` — the
subset of the Compose CLI vocabulary wip drives.

## Command surface

| Command | Bridged to | Notes |
|---|---|---|
| [`wip up`](wip-up) | `<cmd> up [-d]` | `--watch` is rejected in this mode |
| [`wip stop`](wip-stop) | `<cmd> stop` | |
| [`wip down`](wip-down) | `<cmd> down` | |
| [`wip exec`](wip-exec) | `<cmd> exec [-T] <service> …` | `-T` when non-interactive |
| [`wip run`](wip-run) | `<cmd> exec …` | **falls back** to exec; wip warns |
| [`wip shell`](wip-shell) | `<cmd> exec <service> bash`, then `sh` | |
| [`wip logs`](wip-logs) | `<cmd> logs [-f] [SERVICE…]` | multi-service, unlike compose-native |
| [`wip NAME`](wip-dispatch) | `<cmd> exec <service> …` | only `type: exec` is supported |

### Limitations specific to this mode

- **No ephemeral `run`.** The exec-only vocabulary has no equivalent, so `wip run` execs into the
  already-running `compose.service` and warns that it did.
- **No `type: run` / `type: build` interactions.** Both raise a `ConfigError` pointing you at your
  compose tool's own `build` / `up --build`. Compose owns builds for its own services.
- **No `wip up --watch`.** wip never parses a service list in this mode, so there's nothing to
  poll. Use whatever restart support your compose tool offers.
- **`sync:` behaves differently.** `sync.mode` defaults to `run` and cannot be `exec`, and one of
  `sync.image` / `sync.build` is required. See [Sync Modes](Sync-Modes).

## Source sync under this mode

Compose owns the volume layout, so wip does **not** rewrite any mounts for you. Your compose
service must itself declare a named volume matching `sync.volume` (default `<service>-src`):

```yaml
# compose.yml
services:
  app:
    volumes:
      - app-src:/app
volumes:
  app-src:
```

wip's mirror writes into that volume from a separate, disposable container; it never touches the
compose service directly. `wip up`'s pre-boot mirror (and `--no-sync`) work the same as elsewhere.
Full explanation: [Sync Modes](Sync-Modes).

## Diagnostics

`wip doctor` in this mode additionally reports:

- whether `compose.command` resolves to an executable (and its `version` output)
- which compose file wip resolved, and whether it exists

It does **not** parse the compose file here — that's the external tool's job. See
[wip doctor](wip-doctor).
