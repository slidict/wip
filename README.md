<img src="docs/logo.png" alt="wip logo" width="120" align="left">

# wip

[![Tests](https://github.com/slidict/wip/actions/workflows/test.yml/badge.svg)](https://github.com/slidict/wip/actions/workflows/test.yml)
[![License: MIT](https://img.shields.io/github/license/slidict/wip.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-windows%20x64-blue.svg)](#requirements--installation)

Homepage: https://wslc-wip.slidict.com/ · **[Full documentation: wip Wiki](https://github.com/slidict/wip/wiki)**

`wip` is an OSS CLI wrapper that brings a [`dip`](https://github.com/bibendi/dip)-like
workflow to Microsoft WSLC. It collects a project's container, image, environment variables, and
commands into a single `wip.yml`, and forwards them to `wslc.exe` / `wslc` as safe argument arrays
(no shell interpolation).

![wip demo](https://raw.githubusercontent.com/slidict/wip/main/docs/demo.gif)

> **Status:** early release. Expect to track WSLC's own interface as it evolves.

> **v2 switched from Ruby to a C# Native AOT binary.** Through v1, wip was a Ruby gem, which
> meant a Ruby installation and a gem to keep in step with it. From v2 there is neither: `wip.exe`
> is a single self-contained executable with no runtime to install, and no interpreter to start
> before a command runs, so it should be noticeably quicker off the mark.
>
> The Ruby implementation has no further updates planned, but it is not gone — it stays available
> at [v1.1.4](https://github.com/slidict/wip/releases/tag/v1.1.4) and as the
> [`wslc-wip` gem](https://rubygems.org/gems/wslc-wip) on RubyGems.

This README covers the fastest path to a running `wip.yml`. For everything else — every config
key, every command's flags, guides, and troubleshooting — see the **[wip Wiki](https://github.com/slidict/wip/wiki)**.

## Contents

- [Which mode should you use?](#which-mode-should-you-use)
- [Requirements & installation](#requirements--installation)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Commands](#commands)
- [Common errors](#common-errors)
- [Known gaps & TODO](#known-gaps--todo)
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

Windows with WSL2 and Microsoft WSLC. There is no runtime to install: `wip.exe` is a
self-contained Native AOT binary.

```powershell
winget install Slidict.Wip
```

Or download `wip-<version>-win-x64.zip` from
[Releases](https://github.com/slidict/wip/releases) and put `wip.exe` on your PATH.

From source: `dotnet publish src/Wip.Cli/Wip.Cli.csproj -c Release -r win-x64`.

### Running it from a WSL2 shell

wip.exe runs on the Windows side and drives `wslc.exe` directly, but it is meant to be typed
from wherever you work — including a WSL2 shell, which reaches it over Windows interop:

```bash
$ cd ~/myproject && wip.exe up -d
```

**The `.exe` is required in bash**, which does not consult `PATHEXT` the way PowerShell and
cmd do. Add `alias wip=wip.exe` to your shell profile if you would rather not type it.

> **Note on project location.** A project on the WSL filesystem reaches wip as a UNC path
> (`\\wsl.localhost\...`), which changes what `sync.source` and `volumes:` hand to wslc. What
> wslc accepts there is still being measured — see
> [the migration plan](docs/csharp-migration-plan.md) §3. Projects on the Windows filesystem
> are unaffected.

## Quick start

This walks through `mode: container` (the default). Already have a `compose.yml`? See
[Which mode should you use?](#which-mode-should-you-use) first, or read the wiki's
[Getting Started](https://github.com/slidict/wip/wiki/Getting-Started) guide.

```powershell
winget install Slidict.Wip
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
- [Dockerignore](https://github.com/slidict/wip/wiki/Dockerignore) — build context filtering (the build context is now always staged to a local cache, so the old `shadow_context:` key is gone)
- [Source Sync](https://github.com/slidict/wip/wiki/Source-Sync) / [Sync Modes](https://github.com/slidict/wip/wiki/Sync-Modes) / [Continuous Sync](https://github.com/slidict/wip/wiki/Continuous-Sync)

## Commands

| Command | Description |
|---|---|
| `wip init [--force] [--template NAME]` | Write a starter `wip.yml`: `mode: compose-native` if a `compose.yml`, `compose.yaml`, `docker-compose.yml`, or `docker-compose.yaml` is found next to it, `mode: container` otherwise |
| `wip version` | wip's version, plus WSLC's if it can be detected |
| `wip doctor` | Diagnose WSL2, WSLC, config, architecture, and Git |
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

## Known gaps & TODO

wip was rewritten from Ruby to C# and now ships as a Native AOT `wip.exe`. The port is
complete and covered by tests, but some questions cannot be settled without a real Windows
machine with WSL2 and WSLC on it, and a few decisions were deliberately deferred. They are
listed here rather than left implicit — see
[docs/csharp-migration-plan.md](docs/csharp-migration-plan.md) for the reasoning behind each.

### Needs verification on real hardware

- [ ] **Which host paths `wslc` accepts** — the one open design question. A project on the WSL
      filesystem reaches `wip.exe` as a UNC path (`\\wsl.localhost\...`), which changes what
      `sync.source` and `volumes:` hand to `wslc`. Measure `wslc run -v` against a Linux path,
      a Windows-local path, and a UNC path. The translation lives in one function,
      `Platform/WslPath.ForWslc`, which currently assumes Linux paths are accepted; if that is
      wrong, that function is the only thing that changes. See the plan §3.
- [ ] **Interactive TTY from a WSL2 shell** — confirm `wslc exec -it`, Ctrl-C, and terminal
      resizing behave when `wip.exe` is launched from bash rather than PowerShell.
- [ ] **UNC walk performance** — staging a large build context reads every file over 9p.
      Measure it against a real project before assuming it is usable.
- [ ] **Executable bits on staged files** — the build context is copied to a Windows-local
      cache, and NTFS has no Unix mode. Whether `wslc` restores modes decides if a
      `RUN ./script` in a Dockerfile still works.
- [ ] **Telling WSL1 from WSL2** — `wsl.exe --status` exiting zero proves WSL is installed, not
      that the default version is 2, so `wip doctor` currently reports "WSL2 is available" on a
      WSL1-only machine. Fixing it means parsing localised, UTF-16 output, which is worth
      getting right rather than guessing at.

### Before the first WinGet release

- [ ] **Confirm the package identifier** — `Slidict.Wip` is assumed throughout. Changing it
      after publication creates a new package rather than renaming the existing one.
- [ ] **Decide whether to automate the WinGet submission at all** — it means storing a
      classic PAT in a public repository's secrets, and classic scopes cannot be narrowed to
      one repository. Fork PRs cannot reach it and the actions are SHA-pinned, but if the
      remaining exposure is not worth it, leave `WINGET_TOKEN` unset and submit by hand with
      `wingetcreate`: the job skips itself and releases are unaffected. The trade-off is laid
      out in [packaging/winget/README.md](packaging/winget/README.md).
- [ ] **If automating: bot account, environment, expiry** — put the fork on a dedicated
      account (`WINGET_FORK_USER`), store the token on the `winget` environment with required
      reviewers, and give it an expiry.
- [ ] **Validate the manifest locally** — the first submission is human-reviewed, and a
      rejection costs days.
- [ ] **Copy the Ruby implementation to its own repository** — it was removed here, so recover
      it from git history if that has not happened yet.

### Deferred by choice

- [ ] **Code signing** — `wip.exe` ships unsigned today, which is an accepted risk rather than
      one avoided by ZIP packaging: SmartScreen judges the file and its publisher reputation.
      If it is adopted, sign before packaging, hashing, and attesting.
- [ ] **arm64** — win-x64 only for now. The artifact naming and the manifest already have room
      for an arm64 sibling; it needs one more publish job.
- [ ] **Error hints for interactive commands** — interactive commands inherit the console, so
      wip cannot read their output to interpret failures. Recovering that means ConPTY.
- [ ] **`wip` without the extension in WSL** — bash does not consult PATHEXT, so `wip.exe` has
      to be typed. Either document the alias or ship a shim that writes `/usr/local/bin/wip`.

## Development

```bash
git clone https://github.com/slidict/wip.git
cd wip
dotnet build wip.slnx
dotnet test tests/Wip.Tests/Wip.Tests.csproj
dotnet publish src/Wip.Cli/Wip.Cli.csproj -c Release -r win-x64
```

Requires the .NET 10 SDK. The test suite doesn't need WSLC — the resolution, build, and
execution layers are all swappable — and it runs on Linux and macOS as well as Windows, though
only Windows can produce the shipping binary (Native AOT cannot cross-compile between
operating systems).

The last line needs one more thing. Native AOT compiles to native code and then links it with
**MSVC**, so the .NET SDK alone gets as far as `wip.dll` and stops with `Platform linker not
found`:

```powershell
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

If you already have Visual Studio 2022, add the **Desktop development with C++** workload
through the Visual Studio Installer instead. Either way an ordinary shell works afterwards —
the build locates MSVC itself, so no developer command prompt is needed.

Only publishing needs it. `dotnet build` and `dotnet test` do not, and neither does running the
CLI during development:

```powershell
dotnet run --project src/Wip.Cli/Wip.Cli.csproj -- up -d
```

Much of the suite replays a corpus generated by the Ruby implementation this replaced, so a
behaviour change shows up as a failing expectation rather than as a surprise in the field; see
[tests/golden/README.md](tests/golden/README.md). See
[Development](https://github.com/slidict/wip/wiki/Development) and
[Architecture](https://github.com/slidict/wip/wiki/Architecture) on the wiki for more.

## Contributing

Bug reports and pull requests are welcome on [GitHub](https://github.com/slidict/wip). See
[CONTRIBUTING.md](CONTRIBUTING.md) for commit conventions, versioning policy, and the PR
checklist.

## License

[MIT License](LICENSE)
