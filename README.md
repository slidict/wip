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

## Contents

- [Which mode should you use?](#which-mode-should-you-use)
- [Requirements & installation](#requirements--installation)
- [Quick start](#quick-start)
- [Configuration](#configuration)
  - [Container mode](#container-mode)
  - [Compose mode](#compose-mode)
  - [Compose mode (native)](#compose-mode-native)
  - [.env](#env)
  - [.dockerignore](#dockerignore)
  - [Source sync](#source-sync)
- [Commands](#commands)
- [doctor](#doctor)
- [Common errors](#common-errors)
- [FAQ](#faq)
- [Development](#development)
- [Contributing](#contributing)
- [Roadmap](#roadmap)
- [License](#license)

## Which mode should you use?

`wip.yml` runs in one of three modes, set with `mode:`. Pick the one that matches your project:

| Situation | Use |
|---|---|
| No `compose.yml` — wip manages containers directly | [`mode: container`](#container-mode) (default) |
| Have `compose.yml`, don't want to install a third-party tool | [`mode: compose-native`](#compose-mode-native) |
| Have `compose.yml` and already use/prefer a third-party compose-for-`wslc` tool | [`mode: compose`](#compose-mode) |

`wip init` picks `compose-native` automatically when it finds a `compose.yml`/`docker-compose.yml`
next to it, and `container` otherwise — see [Commands](#commands).

## Requirements & installation

Ruby 3.2+, WSL2, and Microsoft WSLC.

```bash
gem install wslc-wip
```

From source: `bundle install && bundle exec exe/wip version`.

## Quick start

This walks through `mode: container` (the default). Already have a `compose.yml`? See
[Which mode should you use?](#which-mode-should-you-use) first.

```bash
gem install wslc-wip
cd my-project
wip init   # writes a starter wip.yml; edit the TODOs, then:
wip doctor
wip build
wip up -d
wip rails console
```

## Configuration

Put a `wip.yml` in your project root. Running from a subdirectory walks up to find it, or pass
`--config PATH` to point at one explicitly.

### Container mode

```yaml
version: 1
mode: container # default
wslc:
  command: auto # tries wslc.exe, wslc, then System32; an absolute path also works
container: app # required once dependencies: has entries; which one `up`/`exec`/`run`/`build`/`commands:`
               # target. No default — a project must say which entry is the primary one explicitly.
network: app-tier # optional; shared by every dependencies: entry so containers can resolve each other by name
dependencies:
  app: # container: points here — the one container wip execs into and runs commands in
    image: slidict/slidict:development
    workdir: /app
    interactive: false
    remove: true
    command: server # extra args appended when `wip up` creates the container
                     # (omit to use the image's default CMD)
    env:
      RAILS_ENV: development
      PORT: "3000"
    ports:
      - "3000:3000"
    volumes:
      - ".:/app"
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

`commands:` can also be spelled `interaction:` — dip's name for the same block — so a `dip.yml`
can become a `wip.yml` with fewer edits. The two are aliases for the same feature, not separate
ones: pick whichever name you like, but declaring both `commands:` and `interaction:` in the same
`wip.yml` is a `ConfigError`.

#### Dependency containers

`dependencies:` holds every container uniformly — the primary one `container:` points at and any
sidecar services (a database, Redis, ...) alongside it. Each entry accepts `image` (required),
`command`, `env`, `ports`, `volumes`, and `workdir`; there's no separate, differently-shaped block
for "the one you exec into." `container:` has no default; once `dependencies:` has any entries,
wip needs to be told explicitly which one is primary rather than guessing a name.

What sets the primary entry apart is operational, not structural: `wip up` brings up every other
entry by name first (creating `network:` beforehand if it doesn't exist and set), then boots or
starts the primary one — so `bin/rails c` (or anything else run inside it) can reach
`development.mysql`/`redis`/etc. by their dependency name, the same way Compose's service names
resolve. `wip down` tears the primary container and all sidecars down (the network itself is left
in place). Only the primary container is a target for `exec`/`run`/`build`/`commands:` — sidecars
are only ever started and stopped, matching Compose's own service-vs-you-exec-into-one-of-them
split.

### Compose mode

If your project already has a real `compose.yml`, don't duplicate it in `dependencies:` — point
`wip` at it instead:

```yaml
version: 1
mode: compose              # required to enable compose mode; a compose: block with no mode: compose is an error
compose:
  service: app             # required: which compose service wip run/exec/NAME target
  command: wslc-compose    # required: the compose-for-wslc binary/path you have installed
  file: compose.yml        # optional; auto-detected next to wip.yml otherwise
  project: myapp            # optional; omitted lets the compose tool pick its own default
```

`compose:` is mutually exclusive with `dependencies:`/`network` — pick one orchestration path per
project. In compose mode, `wip` becomes a thin bridge to an external compose-for-`wslc` CLI rather
than reimplementing Compose itself.

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

### Compose mode (native)

Don't want to install a third-party compose-for-`wslc` tool at all? `mode: compose-native` parses
`compose.yml` itself and drives `wslc` directly, the same way `mode: container` +`dependencies:`
already does — no external binary, and `wip run` gets a real ephemeral `wslc run --rm` instead of
the `exec` fallback above:

```yaml
version: 1
mode: compose-native

compose:
  service: app    # required: which compose service wip run/exec/NAME target
  file: compose.yml # optional; auto-detected next to wip.yml otherwise
  project: myapp    # optional; also names wip's own project network (defaults to the wip.yml
                    # directory's name) so services can reach each other by name
```

There's no `compose.command` here (no external binary to name), and no top-level `container:` —
`compose.service` already names it.

This is explicitly a stopgap for as long as `wslc` itself has no native Compose support (tracked
upstream in [microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)) and third-party
compose-for-`wslc` tools stay incomplete. It only understands a minimal subset of the Compose spec.
Within `services.<name>:`, anything outside that subset is a load-time `ConfigError` naming the
offending key, rather than silently ignored — but everything *outside* `services:` at the document's
top level (`networks:`, `volumes:`, `configs:`, `secrets:`, ...) is the one exception: it's read by
real Compose tools, not by `wip`, so `wip` silently ignores it rather than rejecting an otherwise
valid compose.yml over sections it doesn't need to look at:

- Per service: `image`, `build` (string or `{context:, dockerfile:}`; resolved relative to
  `compose.yml`, not wherever `wip` is invoked from), `command` (shell or exec form), `environment`
  (mapping or `KEY=VALUE` array — a mapping value must not be null; host environment pass-through
  isn't supported), `ports`/`volumes` (short syntax only — `"host:container"` strings, not
  long-syntax mappings), `working_dir`, `user`, `depends_on` (ordering only — a `condition:` other
  than `service_started` is rejected, since there's no health-check support). `tty`, `stdin_open`,
  and `networks` are accepted but silently ignored: TTY/stdin allocation is already decided per
  invocation (see "TTY allocation" below), not fixed per service, and every service already shares
  the one project network `compose.project` sets up.
- `wip logs` takes at most one `SERVICE` (defaulting to `compose.service`) — `wslc logs`, like
  `docker logs`, follows a single container, unlike a real compose tool's multi-service view.
- `sync:` behaves exactly like `mode: container`'s (falls back to the primary service's own image,
  defaults to `sync.mode: exec`) — none of the external bridge's `sync.image`/`sync.build`
  requirement applies, since wip itself boots every container here.

### .env

Like `docker compose`, `wip` automatically loads a `.env` file next to `wip.yml` (one `KEY=VALUE`
per line; `#` comments, blank lines, `export` prefixes, and quoted values are all supported) and
passes its keys through as container environment variables on `build`, `up`, `run`, `exec`, and
custom commands. `.env` only fills in keys that aren't already set by the primary container's
`env` or a command/dependency's own `env` — those always win on conflict. Pass `--env-file PATH`
to load a different file instead.

### .dockerignore

`wip build` reads `.dockerignore` from the build context and excludes anything it matches before
handing the context to `wslc build`, since `wslc` sends the context as-is otherwise. On WSL, a
project outside `/mnt/<drive>` is mirrored into a persistent shadow context on the Windows
filesystem. The first build copies every included file; later builds only copy added or changed
files and remove deleted or newly ignored files. Projects already on `/mnt/c` (or another mounted
Windows drive) continue to build directly. Set `WIP_SHADOW_ROOT` to override the default shadow
location (`/mnt/c/Users/Public/.wip/build-contexts`).

### Source sync

Bind-mounting the app directory (`.:/app`) is what usually makes a container boot crawl under
wslc — see [Slow boot when the app directory is
bind-mounted](#slow-boot-when-the-app-directory-is-bind-mounted) for why. A `sync:` block hands
that problem to `wip`: the source is mounted read-only, the app runs off a named volume, and wip
mirrors one into the other with `rsync`.

`wip up`'s pre-boot mirror always uses a throwaway container (the primary one isn't running yet),
so `sync.image`/`sync.build` apply there regardless of `sync.mode` — falling back to the primary
container's own image if neither is set under `mode: container`/`compose-native` (both have a
`dependencies:` entry to borrow it from); `mode: compose` has no such entry, so one of
`sync.image`/`sync.build` is required there. Where `sync.mode` actually matters is every mirror
*after* that: under `sync.mode: exec` (the default for `mode: container`/`compose-native`), `wip
sync`/`wip sync --watch` run `rsync` inside the already-running primary container instead, so
*that* image needs `rsync` installed — `sync.image`/`sync.build` are ignored for these. Under
`sync.mode: run`, every mirror (pre-boot included) uses a throwaway container, so `sync.image`/
`sync.build` (or the primary image fallback, where available) need `rsync` throughout.

```yaml
sync:
  source: .          # host path, relative to wip.yml (default: the wip.yml directory)
  target: /app       # container path served by the volume (default: the primary container's workdir, else /app)
  volume: app-src    # named volume holding the mirror (default: "<container>-src")
  mount: /host-src   # where the source is bind-mounted read-only (default: /host-src)
  exclude:           # rsync --exclude patterns
    - .git
    - tmp/
    - node_modules/
  delete: true       # rsync --delete (default: true)
  command: rsync     # binary that does the mirroring (default: rsync)
  options: []        # extra flags appended to the rsync invocation
  interval: 2        # seconds between syncs for `wip sync --watch` (default: 2)
  mode: exec         # exec (mirror inside the running container) or run (a throwaway one);
                      # default: exec for `mode: container`, run for `mode: compose`
  image: null        # image for the mirror container; unused under mode: container unless set
                      # (falls back to the primary container's own image). Under mode: compose,
                      # one of image or build is required (there's no dependencies: entry to fall
                      # back to)
  build:              # optional; has wip build the mirror image itself instead of requiring one to
                       # already exist. build.tag wins over image if both are set.
    dockerfile: |
      FROM alpine:latest
      RUN apk add --no-cache rsync
    tag: null          # optional (default: "wip-sync-<container>:latest")
```

`wip init --template NAME` writes `exclude`'s default list live, picked from that stack's own
`github/gitignore` template:

| `--template` | Stack | Default `exclude` |
|---|---|---|
| `rails` | Rails | `.git`, `log/`, `tmp/`, `storage/`, `public/assets/`, `public/packs/`, `.bundle/`, `vendor/bundle/`, `coverage/`, `node_modules/` |
| `node` | Node.js | `.git`, `node_modules/`, `dist/`, `build/`, `.next/`, `.cache/`, `coverage/` |
| `rust` | Rust | `.git`, `target/` |
| `csharp` | C# | `.git`, `bin/`, `obj/`, `.vs/`, `packages/` |
| (omitted) | — | `.git`, `tmp/`, `node_modules/` |

Everything below `sync:` is optional — `sync: {}` alone already works. With it in place:

- Any `volumes` entry on the primary container mounting `target` (the usual `.:/app`) is replaced
  by `<source>:/host-src:ro` plus `app-src:/app`, so the running app only ever touches the volume.
  Other volumes (`bundle:/usr/local/bundle`, ...) are passed through untouched, and sidecar
  `dependencies:` entries keep mounting whatever they declare.
- `wip up` mirrors the source into the volume before the container boots; `wip up --no-sync`
  skips that step.
- `wip sync` mirrors on demand: `sync.mode: exec` (the default under `mode: container`) execs
  rsync inside the already-running container; `sync.mode: run` always uses a throwaway container
  with the same mounts instead. Which one runs is fixed by config, not guessed at from whether a
  container happens to be up.
- `wip sync --watch [--interval N]` keeps re-syncing until Ctrl-C, so host edits reach the
  container with a short delay. Run it in a second terminal alongside `wip up -d`.
- `wip doctor` reports the resolved source, volume, and target, and fails if the source is missing.
- With `sync.build` configured, `wip build`s that image once per `wip up`/`wip sync` invocation
  (including once before a `--watch` loop starts, not on every tick) before mirroring with it.

Like every built-in command, `wip sync` takes precedence over a `commands:` entry of the same
name; wip says so and points at `wip dispatch sync`, which still runs yours.

Two things to keep in mind. The mirror runs `rsync` *inside* the container, so the image needs it
(`RUN apt-get update && apt-get install -y rsync`) — or point `sync.command` at a copy tool the
image already has. And the mirror is one-way (host → volume): anything the app writes under
`target` is removed by the next `--delete` pass unless you `exclude` it, give it its own volume,
or set `delete: false`.

`sync:` works alongside `mode: compose` too, but two things change:

- Compose still owns the volume layout, so wip doesn't rewrite any mounts for you: the compose
  service that runs your app must itself declare a named volume with the exact same name as
  `sync.volume` (`<container>-src` by default) mounted at the path your app expects, e.g.:
  ```yaml
  # compose.yml
  services:
    app:
      volumes:
        - app-src:/app
  volumes:
    app-src:
  ```
  wip's mirror writes into that volume from a separate, disposable container; it never touches
  the compose service directly.
- `sync.mode` defaults to `run` and can't be set to `exec` (only a container wip itself booted is
  guaranteed to have the read-only source mount attached, which compose services never do), and
  `sync.image` or `sync.build` becomes required, since that disposable container needs an image
  from somewhere — under `mode: container` it borrows the primary `dependencies:` entry's image,
  but compose mode has no such entry to borrow from.

`wip up`'s pre-boot mirror (and the `--no-sync` flag that skips it) works the same way under
`mode: compose` as it does otherwise: the source is mirrored into the volume before
`compose up` starts the service that mounts it.

Since `sync.mode: run` boots a fresh container on every mirror, reusing your app's full image here
just adds startup overhead for something that only ever runs `rsync`. A dedicated, minimal image is
worth it — `wip` doesn't publish or default to one itself (same reasoning as `compose.command`:
picking a specific third-party image for you isn't its call to make), but `sync.build` covers it
without needing to manage a separate image yourself:

```yaml
sync:
  build:
    dockerfile: |
      FROM alpine:latest
      RUN apk add --no-cache rsync
```

wip builds this once per `wip up`/`wip sync` invocation (not on every `--watch` tick — see above)
and uses the result, tagged `wip-sync-<container>:latest` by default (`build.tag` overrides it).
Prefer managing the image yourself instead? Build and tag it however you like, then set `sync.image`
to that tag directly — `sync.build`'s tag wins if both are set, so don't configure both at once.

## Commands

| Command | Description |
|---|---|
| `wip init [--force] [--template NAME]` | Write a starter `wip.yml`: `mode: compose-native` if a `compose.yml`/`docker-compose.yml` is found next to it, `mode: container` otherwise. `--template` picks `sync.exclude`'s default patterns for a stack (`rails`, `node`, `rust`, `csharp`); omitted, it falls back to `.git`/`tmp/`/`node_modules/`. Refuses to overwrite an existing `wip.yml` unless `--force` |
| `wip version` | wip's version, plus WSLC's if it can be detected |
| `wip doctor` | Diagnose WSL2, interop, WSLC, config, architecture, and Git |
| `wip config` | Print the effective configuration (secrets masked) |
| `wip build [--no-cache] [-- OPTIONS]` | Build the image from the `build` definition. `wslc build` reuses matching local layers by default; `--no-cache` disables that. |
| `wip up [-d] [--no-sync] [--no-cache]` | Start the primary `dependencies:` entry (`container:` names which one) and its sidecars (creating any that are missing, on `network:` if set). `-d` runs the main container in the background; with `sync:` configured, the source is mirrored into the volume first unless `--no-sync` |
| `wip stop` | Stop the primary container and its sidecar `dependencies:` without removing them |
| `wip down` | Stop and remove the primary container and its sidecar `dependencies:` |
| `wip exec [--no-interactive] COMMAND...` | Run a command in the existing container |
| `wip run [--no-interactive] COMMAND...` | Run a command in a new `--rm` container (mode: compose `exec`s into `compose.service` instead — see "Compose mode" above) |
| `wip shell` | Open the configured shell, falling back to `bash` then `sh` |
| `wip logs [-f] [SERVICE...]` | Follow compose service logs (compose modes only; mode: compose-native takes at most one `SERVICE`) |
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

## FAQ

**Which mode should I start with?**
Pick whichever `mode:` fits your project — see [Which mode should you use?](#which-mode-should-you-use)
for the breakdown.

**Can I use `dependencies:` and `compose:` together?**
No — `compose:` is mutually exclusive with `dependencies:`/`network`. Pick one orchestration path
per project; see [Configuration](#configuration).

**What's the difference between `mode: compose` and `mode: compose-native`?**
`compose` delegates to a third-party compose-for-`wslc` binary you install yourself
(`compose.command`); `compose-native` parses `compose.yml` itself and drives `wslc` directly, no
external tool required. `compose-native`'s Compose coverage isn't frozen at whatever it handles
today — it keeps growing until `wslc` ships native Compose support of its own. See
[Compose mode](#compose-mode) and [Compose mode (native)](#compose-mode-native).

**`dependencies:` already gives me sidecar containers — why would I need `compose-native` too?**
`dependencies:` and `compose-native` aren't really alternatives to each other — they're for two
different starting points. No `compose.yml`? Declare containers directly in `wip.yml`'s own shape
with `dependencies:`. Already have a `compose.yml`? Reusing it is where `compose-native` and
`mode: compose` both come in, so the real comparison is between those two, not against
`dependencies:`: `mode: compose` reuses it too, but only by delegating to a third-party
compose-for-`wslc` binary you install yourself; `compose-native` reuses the same `compose.yml`
without installing anything external, parsing it and driving `wslc` directly — and gets a real
`wslc run --rm` for `wip run` instead of the `exec` fallback `mode: compose` falls back to.
`mode: compose`'s coverage is whatever the external tool you point it at supports; `compose-native`'s
is maintained in this repo and actively extended, not treated as a permanent ceiling. See
[Which mode should you use?](#which-mode-should-you-use) for the full picture.

**What happens to `compose-native` once `wslc` gets official Compose support?**
`compose-native` exists to close the gap for as long as `wslc` has no native Compose support of its
own (tracked upstream in [microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)), and
we intend to keep extending its Compose coverage until that lands — see
[Compose mode (native)](#compose-mode-native) and [Roadmap](#roadmap). `wip.yml`'s shape (`mode:`,
`compose:`) isn't planned to change for existing `container`/`compose`/`compose-native` setups, so
whatever we do once `wslc` catches up won't require rewriting your config.

**Is `sync:` required?**
No, it's entirely optional. Add it if boot times feel slow with a bind-mounted app directory — see
[Slow boot when the app directory is bind-mounted](#slow-boot-when-the-app-directory-is-bind-mounted).

**How does `wip` actually fix the slow bind-mount boot problem?**
The slowness comes from `.:/app`-style bind mounts going through virtiofs, where frameworks that
stat/open many small files at startup (e.g. Ruby's Zeitwerk) pay a round trip per file. A `sync:`
block moves the app off that path entirely: the host source is mounted read-only, the app itself
runs off a named volume (fast native storage inside the VM), and `wip` mirrors the read-only
source into that volume with `rsync` — once before boot, and on demand afterward via `wip sync`
(or continuously with `wip sync --watch`). Since the app never touches the bind mount directly, its
own file access is no longer paying the virtiofs cost; the trade-off is a one-way, slightly-delayed
mirror instead of an always-live view of host edits. See [Source sync](#source-sync) for the full
config and [Slow boot when the app directory is bind-mounted](#slow-boot-when-the-app-directory-is-bind-mounted)
for the root cause.

**Is it safe to put passwords/secrets in `wip.yml`?**
`wip config` masks any key matching token/password/secret/credential/auth when printing, but the
raw file itself is not encrypted. Keep real secrets in your runtime environment or `.env` instead
of committing them in `wip.yml` — but `.env` is only safe if it's actually untracked: add `.env` to
`.gitignore` (and confirm with `git check-ignore .env`) before putting anything sensitive in it.

**`wslc.exe`/`wslc` isn't found — what do I do?**
See [WSLC not found](#wslc-not-found) under Common errors.

**I'm migrating from `dip` — do I have to rename `interaction:` to `commands:`?**
No, `wip.yml` accepts `interaction:` as an alias for `commands:` — same shape, same behavior, just
dip's original name for it. Use whichever you prefer, but not both in the same file (that's a
`ConfigError`). See [Container mode](#container-mode).

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

## Roadmap

`wip` already covers most of what [`dip`](https://github.com/bibendi/dip) adds on top of Compose —
named commands (`commands:`), `run`/`exec` hidden behind a single verb, `.env` passthrough, and
sidecar services via `dependencies:` + `network:`. Rather than waiting on `wslc`'s own Compose
support ([microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)) or an external
compose-for-`wslc` tool staying complete, `mode: compose-native` (see
[Compose mode (native)](#compose-mode-native)) parses `compose.yml` itself and drives `wslc`
directly — no external binary in the loop, `wip run` gets a real `--rm` container, and iterating
on the parser is faster than chasing a third-party tool's own bugs. It's still a deliberately
minimal subset (`depends_on` ordering but no health checks, single-container `logs`, no named
volumes/scaling), and `dependencies:` + `network:` remains the escape hatch for sidecars it
doesn't model. Fuller Compose semantics beyond that subset stay behind delegating to a separate
compose-for-`wslc` tool in [compose mode](#compose-mode) — which of those actually work is
entirely up to the external tool you point `compose.command` at; `wip` only forwards
`-f FILE [-p PROJECT] up|down|exec|logs`, so treat that list as what Compose offers, not as
something `wip` guarantees. See that section for what compose mode covers
and its current limitations (`run`, and `commands:` of type `run`/`build`).

Beyond Compose parity, a resident/daemon process, a GUI, PowerShell-specific tuning, direct
registry API/manifest parsing, self-update, and plugins are all unimplemented and not currently
planned. What's still planned for `wip`, roughly in priority order:

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
`container`, `network`, `commands`, `dependencies`, or `compose` are planned. A resident daemon
and a GUI remain out of scope.

## License

[MIT License](LICENSE)
