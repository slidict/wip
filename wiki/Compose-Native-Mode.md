# Compose Native Mode

`mode: compose-native` reuses your existing `compose.yml` without installing anything: wip parses
it and drives `wslc` directly, the same way [Container Mode](Container-Mode) drives
`dependencies:`.

This is explicitly a stopgap for as long as `wslc` itself has no native Compose support (tracked
upstream in [microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)) and third-party
compose-for-`wslc` tools stay incomplete. Its Compose coverage is maintained in this repo and
actively extended — it isn't frozen at whatever it handles today.

## Config

```yaml
version: 1
mode: compose-native

compose:
  service: app      # required: which compose service wip run/exec/NAME target
  file: compose.yml # optional; auto-detected next to wip.yml otherwise
  project: myapp    # optional; also names wip's project network
                    # (defaults to the wip.yml directory's name)
```

There is no `compose.command` here — naming one is a `ConfigError`, since there's no external
binary involved. There is also no top-level `container:`; `compose.service` already names it.
`compose:` stays mutually exclusive with `dependencies:` and `network:`.

### `compose.project` and the network

wip creates its own project network so services can reach each other by name, the same guarantee
real Compose's per-project network gives. The network name is `compose.project`, falling back to
the `wip.yml` directory's basename. See [Networking](Networking).

## What it understands

The supported `compose.yml` subset — per-service keys, accepted-but-ignored keys, and what happens
when you use something outside it — has its own page:
**[Compose File Support](Compose-File-Support)**.

Sub-features with their own pages:

- [Compose Build](Compose-Build) — `build:` services, auto-tagging, and when they're built
- [Compose Depends On](Compose-Depends-On) — start ordering, cycles, supported conditions
- [Compose Profiles](Compose-Profiles) — profile-gated services and why wip skips them
- [Compose Variable Interpolation](Compose-Variable-Interpolation) — `${VAR}` handling

## Command surface

| Command | Behavior |
|---|---|
| [`wip up`](wip-up) | build `build:` services → create network → start services in `depends_on` order → mirror source → start `compose.service` |
| [`wip stop`](wip-stop) / [`wip down`](wip-down) | `wslc stop` / `wslc remove -f` across every service |
| [`wip exec`](wip-exec) | `wslc exec` into `compose.service` |
| [`wip run`](wip-run) | real `wslc run --rm` — unlike `mode: compose`'s exec fallback |
| [`wip logs`](wip-logs) | at most **one** service (defaults to `compose.service`) |
| [`wip up --watch`](Restart-Policies) | available; reads each service's `restart:` |
| [`wip sync`](wip-sync) | defaults to `sync.mode: exec`, same as container mode |

## How compose.yml maps onto wip's model

wip converts each service into the same internal shape `dependencies:` produces:

| compose.yml | wip |
|---|---|
| `services.<name>` | a `dependencies:` entry named `<name>` |
| `compose.service` | `container:` (the primary container) |
| `image:` | the entry's `image` |
| `build:` | built first, then used as the entry's image |
| `command:` | the entry's `command` (shell or exec form both accepted) |
| `environment:` | the entry's `env` |
| `ports:` / `volumes:` | the entry's `ports` / `volumes` (short syntax only) |
| `working_dir:` / `user:` | `-w` / `-u` |
| `restart:` | polled by `wip up --watch` |
| `depends_on:` | start order |

That's why everything on [Dependencies](Dependencies) about primary vs. sidecar applies here too —
`compose.service` is simply the primary entry.

## Source sync

Behaves exactly like [Container Mode](Container-Mode)'s: `sync.mode` defaults to `exec`, and
`sync.image` / `sync.build` fall back to the primary service's own image. None of
[`mode: compose`](Compose-Mode)'s extra requirements apply, because wip itself boots every
container here. See [Source Sync](Source-Sync).

## Diagnostics

`wip doctor` additionally checks that the compose file exists **and parses**, and that
`compose.service` names a service the file actually defines. See [wip doctor](wip-doctor).

## When `wslc` ships Compose support

`compose-native` exists to close that gap. `wip.yml`'s shape (`mode:`, `compose:`) isn't planned
to change for existing setups, so whatever happens once `wslc` catches up won't require rewriting
your config.
