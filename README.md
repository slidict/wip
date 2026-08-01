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

## Commands

| Command | Description |
|---|---|
| `wip version` | wip's version, plus WSLC's if it can be detected |
| `wip doctor` | Diagnose WSL2, interop, WSLC, config, architecture, and Git |
| `wip config` | Print the effective configuration (secrets masked) |
| `wip build -- --no-cache` | Build the image from the `build` definition |
| `wip up [-d]` | Start `defaults.container` and its `dependencies` (creating any that are missing, on `defaults.network` if set). `-d` runs the main container in the background |
| `wip down` | Stop and remove `defaults.container` and its `dependencies` |
| `wip exec [--no-interactive] COMMAND...` | Run a command in the existing container |
| `wip run [--no-interactive] COMMAND...` | Run a command in a new `--rm` container |
| `wip shell` | Open the configured shell, falling back to `bash` then `sh` |
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

Full Compose compatibility (multiple networks, `depends_on` ordering/health checks, profiles), a
resident/daemon process, a GUI, PowerShell-specific tuning, direct registry API/manifest parsing,
self-update, and plugins are all unimplemented.

## Roadmap

`wip` already covers most of what [`dip`](https://github.com/bibendi/dip) adds on top of Compose —
named commands (`commands:`), `run`/`exec` hidden behind a single verb, `.env` passthrough, and
sidecar services via `dependencies:` + `defaults.network`. The gaps that remain are mostly on the
Compose side: multi-service orchestration is still per-container rather than declarative and
project-scoped. Planned next, roughly in priority order:

1. **`depends_on`-style startup ordering & health checks** — `wip up` currently starts
   `dependencies` in declaration order with no readiness gate. Add an optional `depends_on` /
   `healthcheck` per dependency (e.g. poll a TCP port or exec a command) so `wip up` can wait for
   `mysql` before starting `app`, instead of racing.
2. **`wip logs [-f] [NAME...]`** — aggregate output from the main container and any dependencies,
   the most-missed single Compose command when working with sidecars.
3. **`wip provision`** — a dip-style one-shot bootstrap hook (build → up deps → install deps →
   create/migrate/seed DB) so a new contributor can go from `git clone` to a working environment
   in two commands (`wip provision && wip up`).
4. **Profiles** — an optional `profiles:` tag on `dependencies` entries plus `wip up --profile
   NAME`, so debug-only sidecars (mailpit, a queue UI, ...) don't start by default.
5. **Config file merging** — `--config` currently accepts one file; support layering
   (`wip.yml` + `wip.override.yml`, or a `WIP_CONFIG` list) for dev/CI/debug variants without
   duplicating the whole file, plus a `wip config --resolved` view of the merged result.
6. **Named volume helpers** — `wip volumes ls|rm` for volumes declared under `dependencies`, so
   they're discoverable/removable without dropping to raw `wslc`/`docker` commands.
7. **Service scaling** — a `--scale NAME=N` flag for `wip up`, mirroring `docker compose up
   --scale`, for dependencies that can safely run more than one instance.

Each of these should stay additive to the existing `wip.yml` shape — no breaking changes to
`defaults`, `commands`, or `dependencies` are planned. Full N-network topologies, a resident
daemon, and a GUI remain out of scope; see "Not in the initial release" above.

## License

[MIT License](LICENSE)
