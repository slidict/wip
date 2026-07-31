# wip

[![Tests](https://github.com/slidict/wip/actions/workflows/test.yml/badge.svg)](https://github.com/slidict/wip/actions/workflows/test.yml)
[![Gem Version](https://img.shields.io/gem/v/wslc-wip.svg)](https://rubygems.org/gems/wslc-wip)
[![License: MIT](https://img.shields.io/github/license/slidict/wip.svg)](LICENSE)
[![Ruby](https://img.shields.io/badge/ruby-%3E%3D%203.2-red.svg)](wslc-wip.gemspec)

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

## Commands

| Command | Description |
|---|---|
| `wip version` | wip's version, plus WSLC's if it can be detected |
| `wip doctor` | Diagnose WSL2, interop, WSLC, config, architecture, and Git |
| `wip config` | Print the effective configuration (secrets masked) |
| `wip build -- --no-cache` | Build the image from the `build` definition |
| `wip up [-d]` | Start `defaults.container` (creating it with `up.command` if missing). `-d` runs it in the background |
| `wip down` | Stop and remove `defaults.container` |
| `wip exec [--no-interactive] COMMAND...` | Run a command in the existing container |
| `wip run [--no-interactive] COMMAND...` | Run a command in a new `--rm` container |
| `wip shell` | Open the configured shell, falling back to `bash` then `sh` |
| `wip NAME ARGS...` | Run `commands.NAME`, appending any extra arguments |

TTY allocation is decided by combining the command's config, the CLI option, and whether both
stdin and stdout are real TTYs. Set `WIP_DEBUG=1` to print the `Shellwords`-joined command before
running it.

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

Compose compatibility, a resident/daemon process, a GUI, PowerShell-specific tuning, direct
registry API/manifest parsing, self-update, and plugins are all unimplemented. Likely future
additions: a richer config schema, lifecycle hooks, multi-container support, platform selection,
and more detailed diagnostics.

## License

[MIT License](LICENSE)
