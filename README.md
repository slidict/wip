<img src="docs/logo.png" alt="wip logo" width="120" align="left">

# wip

[![Tests](https://github.com/slidict/wip/actions/workflows/test.yml/badge.svg)](https://github.com/slidict/wip/actions/workflows/test.yml)
[![Gem Version](https://img.shields.io/gem/v/wslc-wip.svg)](https://rubygems.org/gems/wslc-wip)
[![License: MIT](https://img.shields.io/github/license/slidict/wip.svg)](LICENSE)
[![Ruby](https://img.shields.io/badge/ruby-%3E%3D%203.2-red.svg)](wslc-wip.gemspec)

Homepage: https://wslc-wip.slidict.com/ · **[Full documentation: wip Wiki](https://github.com/slidict/wip/wiki)**

`wip` is a Ruby-built OSS CLI wrapper that brings a [`dip`](https://github.com/bibendi/dip)-like
workflow to Microsoft WSLC. It collects a project's container, image, environment variables, and
commands into a single `wip.yml`, and forwards them to `wslc.exe` / `wslc` as safe argument arrays
(no shell interpolation).

![wip demo](https://raw.githubusercontent.com/slidict/wip/main/docs/demo.gif)

> **Status:** early release. Expect to track WSLC's own interface as it evolves.

This README covers the fastest path to a running `wip.yml`. For everything else — every config
key, every command's flags, guides, and troubleshooting — see the **[wip Wiki](https://github.com/slidict/wip/wiki)**.

## Contents

- [Which mode should you use?](#which-mode-should-you-use)
- [Requirements & installation](#requirements--installation)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Commands](#commands)
- [Common errors](#common-errors)
- [Development](#development)
- [Contributing](#contributing)
- [License](#license)

## Which mode should you use?

`wip.yml` runs in one of three modes, set with `mode:`. Pick the one that matches your project:

| Situation | Use |
|---|---|
| No `compose.yml` — wip manages containers directly | `mode: container` (default) |
| Have `compose.yml`, don't want to install a third-party tool | `mode: compose-native` |
| Have `compose.yml` and already use/prefer a third-party compose-for-`wslc` tool | `mode: compose` |

`wip init` picks `compose-native` automatically when it finds a `compose.yml`, `compose.yaml`,
`docker-compose.yml`, or `docker-compose.yaml` next to it, and `container` otherwise — see
[Commands](#commands). For the full breakdown and trade-offs, see
[Choosing a Mode](https://github.com/slidict/wip/wiki/Choosing-a-Mode) on the wiki.

## Requirements & installation

Ruby 3.2+, WSL2, and Microsoft WSLC.

```bash
gem install wslc-wip
```

From source: `bundle install && bundle exec exe/wip version`.

## Quick start

This walks through `mode: container` (the default). Already have a `compose.yml`? See
[Which mode should you use?](#which-mode-should-you-use) first, or read the wiki's
[Getting Started](https://github.com/slidict/wip/wiki/Getting-Started) guide.

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

```yaml
version: 1
mode: container # default
wslc:
  command: auto # tries wslc.exe, wslc, then System32; an absolute path also works
container: app # required once dependencies: has entries; which one `up`/`exec`/`run`/`build`/`interaction:`
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
interaction:
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
    shadow_context: /mnt/c/Users/me/AppData/Local/wip/build-contexts
sync: # optional; mirror the source into a named volume instead of bind-mounting it live
  exclude:
    - .git
    - tmp/
    - node_modules/
```

`env` values are stringified. `wip config` masks any key matching token, password, secret,
credential, or auth. Keep real secrets out of the config file and in your runtime environment
instead — see [Secret Masking](https://github.com/slidict/wip/wiki/Secret-Masking).

`interaction:` can also be spelled `commands:` — the same block under a different name, e.g. for
projects that already use `commands:`. The two are aliases for the same feature; declaring both in
the same `wip.yml` is a `ConfigError`. See [Interactions](https://github.com/slidict/wip/wiki/Interactions).

Every key above is covered across the wiki's feature pages, with the full behavior, edge cases,
and examples — start at the
**[Configuration Reference](https://github.com/slidict/wip/wiki/Configuration-Reference)**. Notably:

- [Dependencies](https://github.com/slidict/wip/wiki/Dependencies) — the primary container vs. sidecars
- [Restart Policies](https://github.com/slidict/wip/wiki/Restart-Policies) /
  [Auto Restarting Containers](https://github.com/slidict/wip/wiki/Auto-Restarting-Containers) — `restart:` and `wip up --watch`
- [Compose Mode](https://github.com/slidict/wip/wiki/Compose-Mode) — bridging to a third-party compose-for-`wslc` tool
- [Compose Native Mode](https://github.com/slidict/wip/wiki/Compose-Native-Mode) — wip parsing `compose.yml` itself
- [Env Files](https://github.com/slidict/wip/wiki/Env-Files) — `.env` loading and precedence
- [Dockerignore](https://github.com/slidict/wip/wiki/Dockerignore) / [Shadow Build Context](https://github.com/slidict/wip/wiki/Shadow-Build-Context)
- [Source Sync](https://github.com/slidict/wip/wiki/Source-Sync) / [Sync Modes](https://github.com/slidict/wip/wiki/Sync-Modes) / [Continuous Sync](https://github.com/slidict/wip/wiki/Continuous-Sync)

## Commands

| Command | Description |
|---|---|
| `wip init [--force] [--template NAME]` | Write a starter `wip.yml`: `mode: compose-native` if a `compose.yml`, `compose.yaml`, `docker-compose.yml`, or `docker-compose.yaml` is found next to it, `mode: container` otherwise |
| `wip version` | wip's version, plus WSLC's if it can be detected |
| `wip doctor` | Diagnose WSL2, interop, WSLC, config, architecture, and Git |
| `wip config` | Print the effective configuration (secrets masked) |
| `wip build [--no-cache] [-- OPTIONS]` | Build the image from the `build` definition |
| `wip up [-d] [--no-sync] [--no-cache] [--watch] [--interval N]` | Start the configured stack, creating it if necessary |
| `wip stop` | Stop the configured stack without removing it |
| `wip down` | Stop and remove the configured stack |
| `wip exec [--no-interactive] COMMAND...` | Run a command in the existing container |
| `wip run [--no-interactive] COMMAND...` | Run a command in a new `--rm` container (`mode: compose` has no ephemeral run — falls back to `exec` in the running service, with a warning) |
| `wip shell` | Open the configured shell, falling back to `bash` then `sh` |
| `wip logs [-f] [SERVICE...]` | Follow compose service logs (compose modes only; under `mode: compose-native`, at most one `SERVICE`) |
| `wip sync [-w\|--watch] [--interval N]` | Mirror the source into the sync volume once, or keep re-syncing with `--watch` (needs `sync:`) |
| `wip NAME ARGS...` | Run `interaction.NAME`, appending any extra arguments |

Every command has flags, per-mode behavior, and examples on its own wiki page — see the
**[CLI Command Reference](https://github.com/slidict/wip/wiki/CLI-Command-Reference)**.

Pass `--debug` (or set `WIP_DEBUG=1`) to see where time is going: wip prints each step it takes,
along with a periodic host resource snapshot (load, memory, disk I/O, top processes), so a hang is
visible even before a command produces output. See
[Debug Output](https://github.com/slidict/wip/wiki/Debug-Output) for the full behavior and the
`--debug-log` option.

`wip doctor` prints `[OK]`/`[WARN]`/`[FAIL]` per check; warnings alone exit 0, a blocking problem
exits 1. See [`wip doctor`](https://github.com/slidict/wip/wiki/wip-doctor).

## Common errors

- [WSLC not found](https://github.com/slidict/wip/wiki/WSLC-Not-Found)
- [Docker Hub / registry authentication](https://github.com/slidict/wip/wiki/Registry-Authentication)
- [Slow boot when the app directory is bind-mounted](https://github.com/slidict/wip/wiki/Fixing-a-Slow-Boot)
- [CPU architecture mismatch](https://github.com/slidict/wip/wiki/Architecture-Mismatch)

More errors, causes, and fixes are indexed on the wiki's
**[Troubleshooting & FAQ](https://github.com/slidict/wip/wiki/Troubleshooting-and-FAQ)** page,
including [Configuration Errors](https://github.com/slidict/wip/wiki/Configuration-Errors) (every
`ConfigError` and what triggers it).

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
swappable. This project uses RuboCop for Ruby style and static analysis; `bundle exec rake` runs
both RSpec and RuboCop. GitHub Actions checks them on Ruby 3.2, 3.3, 3.4, and 4.0. See
[Development](https://github.com/slidict/wip/wiki/Development) and
[Architecture](https://github.com/slidict/wip/wiki/Architecture) on the wiki for more.

## Contributing

Bug reports and pull requests are welcome on [GitHub](https://github.com/slidict/wip). See
[CONTRIBUTING.md](CONTRIBUTING.md) for commit conventions, versioning policy, and the PR
checklist.

## License

[MIT License](LICENSE)
