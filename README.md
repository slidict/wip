# wip

[![Tests](https://github.com/slidict/wip/actions/workflows/test.yml/badge.svg)](https://github.com/slidict/wip/actions/workflows/test.yml)
[![Gem Version](https://img.shields.io/gem/v/wslc-wip.svg)](https://rubygems.org/gems/wslc-wip)
[![License: MIT](https://img.shields.io/github/license/slidict/wip.svg)](LICENSE)
[![Ruby](https://img.shields.io/badge/ruby-%3E%3D%203.2-red.svg)](wslc-wip.gemspec)

Homepage: https://wslc-wip.slidict.com/

`wip` is a Ruby-built OSS CLI wrapper that brings a [`dip`](https://github.com/bibendi/dip)-like
workflow to Microsoft WSLC. It collects a project's container, image, environment variables, and
commands into a single `wip.yml`, and forwards them to `wslc.exe` / `wslc` as safe argument arrays
(no shell interpolation).

![wip demo](https://raw.githubusercontent.com/slidict/wip/main/docs/demo.gif)

> **Status:** early release. Expect to track WSLC's own interface as it evolves.

## Requirements & installation

Ruby 3.2+, WSL2, and Microsoft WSLC.

```bash
gem install wslc-wip
```

From source: `bundle install && bundle exec exe/wip version`.

## Quick start

```bash
gem install wslc-wip
cd my-project
wip doctor
wip build
wip up -d
wip rails console
```

## Full configuration example

Put a `wip.yml` in your project root. Running from a subdirectory walks up to find it, or pass
`--config PATH` to point at one explicitly.

```yaml
version: 1
wslc:
  command: auto # tries wslc.exe, wslc, then System32; an absolute path also works
defaults:
  container: app
  image: slidict/slidict:development
  workdir: /app
  interactive: false
  remove: true
  network: app-tier # optional; shared with `dependencies` so containers can resolve each other by name
  env:
    RAILS_ENV: development
    PORT: "3000"
  ports:
    - "3000:3000"
  volumes:
    - ".:/app"
up:
  command: server # command `wip up` passes to the image when it creates the container
                   # (omit to use the image's default CMD)
dependencies:
  redis:
    image: redis:latest
  development.mysql:
    image: mysql:8.0
    command: --default-authentication-plugin=mysql_native_password
    env:
      MYSQL_ROOT_PASSWORD: password
      MYSQL_DATABASE: development
commands:
  rails:
    type: exec
    command: bin/rails
    container: app
    interactive: true
    workdir: /app
    env:
      RAILS_ENV: development
  bundle:
    command: bundle
  rspec:
    command: bundle exec rspec
  shell:
    command: bash
    interactive: true
  migrate:
    type: run
    command: bundle exec rails db:migrate
    image: slidict/slidict:development
    remove: true
  build:
    type: build
    context: .
    tag: slidict/slidict:development
sync: # optional; mirror the source into a named volume instead of bind-mounting it live
  exclude:
    - .git
    - tmp/
    - node_modules/
```

`env` values are stringified. `wip config` masks any key matching token, password, secret,
credential, or auth. Keep real secrets out of the config file and in your runtime environment
instead.

### .env

Like `docker compose`, `wip` automatically loads a `.env` file next to `wip.yml` (one `KEY=VALUE`
per line; `#` comments, blank lines, `export` prefixes, and quoted values are all supported) and
passes its keys through as container environment variables on `build`, `up`, `run`, `exec`, and
custom commands. `.env` only fills in keys that aren't already set by `defaults.env` or a
command/dependency's own `env` — those always win on conflict. Pass `--env-file PATH` to load a
different file instead.

### .dockerignore

`wip build` reads `.dockerignore` from the build context and stages a filtered copy of the
context (skipping anything it matches) before handing it to `wslc build`, since `wslc` sends the
context as-is otherwise. If there's no `.dockerignore`, the original context directory is used
directly with no copying.

### Dependency containers

If your app needs sidecar services (a database, Redis, ...), declare them under `dependencies`
and set `defaults.network`. `wip up` creates the network first (if it doesn't exist), then brings
up each dependency by name before the main container — so `bin/rails c` (or anything else run
inside the main container) can reach `development.mysql`/`redis`/etc. by their dependency name,
the same way Compose's service names resolve. `wip down` tears the main container and all
dependencies down (the network itself is left in place). Each dependency entry accepts `image`
(required), `command`, `env`, `ports`, `volumes`, and `workdir` — the same shape as `defaults`.

### Source sync

Bind-mounting the app directory (`.:/app`) is what usually makes a container boot crawl under
wslc — see [Slow boot when the app directory is
bind-mounted](#slow-boot-when-the-app-directory-is-bind-mounted) for why. A `sync:` block hands
that problem to `wip`: the source is mounted read-only, the app runs off a named volume, and wip
mirrors one into the other with `rsync`.

```yaml
sync:
  source: .          # host path, relative to wip.yml (default: the wip.yml directory)
  target: /app       # container path served by the volume (default: defaults.workdir, else /app)
  volume: app-src    # named volume holding the mirror (default: "<defaults.container>-src")
  mount: /host-src   # where the source is bind-mounted read-only (default: /host-src)
  exclude:           # rsync --exclude patterns
    - .git
    - tmp/
    - node_modules/
  delete: true       # rsync --delete (default: true)
  command: rsync     # binary that does the mirroring (default: rsync)
  options: []        # extra flags appended to the rsync invocation
  interval: 2        # seconds between syncs for `wip sync --watch` (default: 2)
```

Everything below `sync:` is optional — `sync: {}` alone already works. With it in place:

- Any `defaults.volumes` entry mounting `target` (the usual `.:/app`) is replaced by
  `<source>:/host-src:ro` plus `app-src:/app`, so the running app only ever touches the volume.
  Other volumes (`bundle:/usr/local/bundle`, ...) are passed through untouched, and
  `dependencies:` keep mounting whatever they declare.
- `wip up` mirrors the source into the volume before the container boots; `wip up --no-sync`
  skips that step.
- `wip sync` mirrors on demand — `wslc exec`ing rsync inside the container when it's running, and
  falling back to a throwaway container with the same mounts when it isn't.
- `wip sync --watch [--interval N]` keeps re-syncing until Ctrl-C, so host edits reach the
  container with a short delay. Run it in a second terminal alongside `wip up -d`.
- `wip doctor` reports the resolved source, volume, and target, and fails if the source is missing.

Like every built-in command, `wip sync` takes precedence over a `commands:` entry of the same
name; wip says so and points at `wip dispatch sync`, which still runs yours.

Two things to keep in mind. The mirror runs `rsync` *inside* the container, so the image needs it
(`RUN apt-get update && apt-get install -y rsync`) — or point `sync.command` at a copy tool the
image already has. And the mirror is one-way (host → volume): anything the app writes under
`target` is removed by the next `--delete` pass unless you `exclude` it, give it its own volume,
or set `delete: false`.

`sync:` is mutually exclusive with `compose:` — in compose mode the compose file owns the volume
layout.

### Compose mode

If your project already has a real `compose.yml`, don't duplicate it in `dependencies:` — point
`wip` at it instead:

```yaml
version: 1
compose:
  service: app             # required: which compose service wip run/exec/NAME target
  command: wslc-compose    # required: the compose-for-wslc binary/path you have installed
  file: compose.yml        # optional; auto-detected next to wip.yml otherwise
  project: myapp            # optional; omitted lets the compose tool pick its own default
```

`compose:` is mutually exclusive with `dependencies:`/`defaults.network` — pick one orchestration
path per project. In compose mode, `wip` becomes a thin bridge to an external compose-for-`wslc`
CLI rather than reimplementing Compose itself.

`wslc` itself has no native Compose support yet (tracked upstream in
[microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)), and until it does,
independent third-party tools fill the gap — for example
[bacarndiaye/wslc-compose](https://github.com/bacarndiaye/wslc-compose) (Python) and
[inuyume/wslc-compose](https://github.com/inuyume/wslc-compose) (Go), among others. `wslc` is new
and still evolving, so expect more of these to show up (and existing ones to change) over time.
`wip` deliberately doesn't pick a winner or default to any of them (unlike `wslc.command`, which
defaults to `auto` and searches for `wslc.exe`/`wslc`): `compose.command` is required and treats
every implementation equally — set it to whichever binary name or absolute path you've installed.
Whichever one you use needs to understand `-f FILE [-p PROJECT] up|down|exec|logs`, the subset of
the Compose CLI vocabulary `wip` drives. `wip doctor` reports whether the configured command is
found, its version, and which compose file `wip` resolved.

- `wip up`/`wip down` delegate straight to `<compose command> up -d`/`down`.
- `wip exec`/`wip NAME` (custom `commands:`) run inside `compose.service`.
- `wip shell` also goes through the bridge: unless `commands.shell` is defined in `wip.yml`, it
  `exec`s `bash` against `compose.service`, falling back to `sh`.
- `wip logs [-f] [SERVICE...]` is only available in compose mode.
- `wip run` has no ephemeral-container equivalent in this exec-only vocabulary, so it falls back
  to `exec` against the already-running `compose.service` (wip warns when this happens).
- `commands:` entries with `type: run`/`type: build` aren't supported in compose mode — use your
  compose tool's own `build`/`up --build` directly; compose owns builds for its own services.

## Commands

| Command | Description |
|---|---|
| `wip version` | wip's version, plus WSLC's if it can be detected |
| `wip doctor` | Diagnose WSL2, interop, WSLC, config, architecture, and Git |
| `wip config` | Print the effective configuration (secrets masked) |
| `wip build -- --no-cache` | Build the image from the `build` definition |
| `wip up [-d] [--no-sync]` | Start `defaults.container` and its `dependencies` (creating any that are missing, on `defaults.network` if set). `-d` runs the main container in the background; with `sync:` configured, the source is mirrored into the volume first unless `--no-sync` |
| `wip down` | Stop and remove `defaults.container` and its `dependencies` |
| `wip exec [--no-interactive] COMMAND...` | Run a command in the existing container |
| `wip run [--no-interactive] COMMAND...` | Run a command in a new `--rm` container (compose mode: `exec`s into `compose.service` instead) |
| `wip shell` | Open the configured shell, falling back to `bash` then `sh` |
| `wip logs [-f] [SERVICE...]` | Follow compose service logs (compose mode only) |
| `wip sync [-w] [--interval N]` | Mirror the source into the sync volume once, or keep re-syncing with `--watch` (needs `sync:`) |
| `wip NAME ARGS...` | Run `commands.NAME`, appending any extra arguments |

TTY allocation is decided by combining the command's config, the CLI option, and whether both
stdin and stdout are real TTYs.

Pass `--debug` (or set `WIP_DEBUG=1`) to see where time is going: wip prints each step it takes —
checking for an existing network/container/dependency, and running the resolved `wslc`/`docker`
command — along with how long that step took, e.g.:

```console
$ wip rails c --debug
wip: [debug] running: wslc.exe exec -it -w /app app bin/rails c
+ wslc.exe exec -it -w /app app bin/rails c
...
wip: [debug] done in 4.32s: running: wslc.exe exec -it -w /app app bin/rails c
```

For long-running interactive commands (like `rails c`), the "done" line only prints after you
exit, but the timestamp of the `+ ...` line tells you when wip finished its own setup and handed
off to `wslc`/`docker` — useful for telling wip-side overhead apart from time spent booting inside
the container.

While a step is still running, wip also prints a host resource snapshot (load average, memory,
disk I/O, and the top CPU-consuming processes) every 5 seconds, so a hang is visible even before
the command has produced any output of its own:

```console
wip: [debug] still running (load 3.42 2.10 1.05 | mem 6.1G/15.6G | io read 12000KB/s write 400KB/s | top: wslc.exe(8842) cpu 61.0%/mem 3.2%, ...): running: wslc.exe exec -it -w /app app bin/rails c
```

The disk I/O figure is worth watching first if the host's CPU and memory look idle — a slow
`bundle`/`rails` boot is often WSL2's bind-mounted (`.:/app`-style) volumes doing a lot of small
reads, not the container starving for CPU.

For commands that hand the real terminal to the child (`-it`, e.g. `rails c`), these periodic
snapshots go to a log file instead of your terminal — wip prints the path once at the start —
since writing into a terminal the child controls in raw mode would garble both outputs. Commands
that don't need a TTY still get the snapshots printed live.

Override that choice with `--debug-log`:

- `--debug-log=-` forces snapshots inline even for `-it` commands (only useful if you know your
  terminal/pager can tolerate the interleaving).
- `--debug-log=PATH` always writes snapshots to `PATH`, including for non-TTY commands, e.g. to
  keep every run's snapshots in one place: `wip rails c --debug --debug-log=/tmp/wip-debug.log`.

## doctor

Each check prints as `[OK]`, `[WARN]`, or `[FAIL]`. Warnings alone exit 0; a WSL2, interop, WSLC,
or config problem that blocks execution exits 1. Git being unreachable from the real build
environment is only a warning.

## Common errors

### WSLC not found

Install or update the WSL container tooling, then run `wip doctor`. `auto` looks for `wslc.exe`,
`wslc`, then `/mnt/c/Windows/System32/wslc.exe`, in that order.

### Docker Hub authentication

When `pull access denied` (or similar) is detected, wip suggests how to log in:

```bash
wslc registry login -u <username> docker.io
```

### Slow boot when the app directory is bind-mounted

wslc containers run in their own VM, so a bind-mounted host directory (`.:/app`) is always shared
in over virtiofs, even when the host path is already on WSL's native filesystem. Frameworks that
scan large directory trees at startup (Ruby's Zeitwerk autoloader, for example) issue many small
per-file stat/open calls, and each one is a round trip through that layer — CPU on the Windows
side can look busy while almost no data is actually transferred, and the process can appear hung
for minutes with barely any resource usage to show for it.

If a debug log shows a boot-time command "stuck" with low CPU/mem/IO in `resource_monitor`'s
output, this is worth checking before assuming the app itself is broken. The fix is to stop
bind-mounting the source live and mirror it into a named volume instead, so the app only ever
touches fast native storage once it's running. `wip` does that for you — add a `sync:` block and
it rewrites the mounts, mirrors before boot, and re-syncs on demand. See [Source
sync](#source-sync).

### CPU architecture mismatch

Check the image and publish a multi-arch (amd64/arm64) image:

```bash
docker buildx imagetools inspect <image>
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t <image> \
  --push .
```

## Development

```bash
git clone https://github.com/slidict/wip.git
cd wip
bundle install
bundle exec rspec
bundle exec rubocop
bundle exec rake
```

The test suite doesn't need WSLC — the resolution, build, and execution layers are all
swappable. GitHub Actions runs RSpec and RuboCop on Ruby 3.2, 3.3, 3.4, and 4.0.

## Contributing

Bug reports and pull requests are welcome on [GitHub](https://github.com/slidict/wip). See
[CONTRIBUTING.md](CONTRIBUTING.md) for commit conventions, versioning policy, and the PR
checklist.

## Not in the initial release

Full Compose compatibility isn't reimplemented in `wip` itself, but is available by delegating to
a third-party compose-for-`wslc` tool — see [Compose mode](#compose-mode). A resident/daemon
process, a GUI, PowerShell-specific tuning, direct registry API/manifest parsing, self-update, and
plugins are all unimplemented.

## Roadmap

`wip` already covers most of what [`dip`](https://github.com/bibendi/dip) adds on top of Compose —
named commands (`commands:`), `run`/`exec` hidden behind a single verb, `.env` passthrough, and
sidecar services via `dependencies:` + `defaults.network`. Fuller Compose semantics
(`depends_on` ordering/health checks, log aggregation, named volumes, profiles, scaling) are now
handled by delegating to a separate compose-for-`wslc` tool in [compose mode](#compose-mode)
rather than being reimplemented in `wip` itself. Which of those actually work is entirely up to
the external tool you point `compose.command` at — `wip` only forwards
`-f FILE [-p PROJECT] up|down|exec|logs`, so treat that list as what Compose offers, not as
something `wip` guarantees. See that section for what compose mode covers
and its current limitations (`run`, and `commands:` of type `run`/`build`). What's still planned
for `wip`, roughly in priority order:

1. **`wip provision`** — a dip-style one-shot bootstrap hook (build → up deps → install deps →
   create/migrate/seed DB) so a new contributor can go from `git clone` to a working environment
   in two commands (`wip provision && wip up`).
2. **Config file merging** — `--config` currently accepts one file; support layering
   (`wip.yml` + `wip.override.yml`, or a `WIP_CONFIG` list) for dev/CI/debug variants without
   duplicating the whole file, plus a `wip config --resolved` view of the merged result.
3. **Bind-mount boot time (`rails c`, `bundle`, ...)** — commands like `wip rails c` still start
   noticeably slower than the equivalent under `docker compose`, mostly from WSL2 bind-mounted
   (`.:/app`-style) volumes doing many small reads for gems/`node_modules` (use `--debug` to
   confirm it's disk I/O and not `wip`'s own overhead). [Source sync](#source-sync) works around
   this today by running the app off a named volume and mirroring the host tree into it, at the
   cost of a one-way sync with a short delay. A tighter loop (host-side file watching instead of
   interval polling, two-way sync) is the natural next step. We're also hoping for improvements on
   the `wslc` side itself (faster bind-mount/cache behavior); `wip` will pick those up for free as
   soon as they land.

Each of these should stay additive to the existing `wip.yml` shape — no breaking changes to
`defaults`, `commands`, `dependencies`, or `compose` are planned. A resident daemon and a GUI
remain out of scope; see "Not in the initial release" above.

## License

[MIT License](LICENSE)
