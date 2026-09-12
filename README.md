<img src="docs/logo.png" alt="wip logo" width="120" align="left">

# wip

[![Tests](https://github.com/slidict/wip/actions/workflows/test.yml/badge.svg)](https://github.com/slidict/wip/actions/workflows/test.yml)
[![License: MIT](https://img.shields.io/github/license/slidict/wip.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-windows%20x64-blue.svg)](#requirements--installation)

Homepage: https://wslc-wip.slidict.com/ · **[Full documentation: wip Wiki](https://github.com/slidict/wip/wiki)**

[![wip slide](https://slidict.io/slides/160/gif?v=1788619628)](https://slidict.io/slides/160)

`wip` is an OSS CLI wrapper that brings a [`dip`](https://github.com/bibendi/dip)-like
workflow to Microsoft WSLC. It collects a project's container, image, environment variables, and
commands into a single `wip.yml`, and forwards them to `wslc.exe` / `wslc` as safe argument arrays
(no shell interpolation).

![wip demo](https://raw.githubusercontent.com/slidict/wip/main/docs/demo.gif)

> **Status:** early release. Expect to track WSLC's own interface as it evolves.

This README is just the fastest path to a running `wip.yml`. For everything else — every config
key, every command's flags, guides, and troubleshooting — see the
**[wip Wiki](https://github.com/slidict/wip/wiki)**.

## Contents

- [Highlights](#highlights)
- [Requirements & installation](#requirements--installation)
- [Quick start](#quick-start)
- [Examples](#examples)
- [Configuration](#configuration)
- [Commands](#commands)
- [Development](#development)
- [Contributing](#contributing)
- [License](#license)

## Highlights

- **No orchestration engine of its own** — every mode ultimately shells out to `wslc.exe` /
  `wslc` as a safe argv array; wip never talks to Docker and never grows logic that competes with
  `wslc`. See [Architecture](https://github.com/slidict/wip/wiki/Architecture).
<a id="architecture"></a>
- **Three modes for three project shapes** — drive containers directly, parse an existing
  `compose.yml` yourself, or bridge to a third-party compose-for-`wslc` tool. See
  [Which mode should you use?](#which-mode-should-you-use) below.
- **`wip init --ai`** — describe your stack in natural language and get a validated `wip.yml`
  back, via any local OpenAI-compatible server (Ollama, LM Studio). See
  [AI-Assisted Initialization](https://github.com/slidict/wip/wiki/AI-Assisted-Initialization).
- **Healthcheck-aware startup** — `wip up` waits on a dependency's `healthcheck:` before starting
  whatever depends on it, in every mode. See
  [Restart Policies](https://github.com/slidict/wip/wiki/Restart-Policies).
- **Works from WSL2 shells, not just PowerShell** — `wip.exe` runs on Windows and drives `wslc`
  directly, but is meant to be typed from wherever you work. See
  [Running from a WSL2 Shell](https://github.com/slidict/wip/wiki/Running-from-a-WSL2-Shell).
- **Secrets stay masked** — `wip config` redacts credential-shaped values by default. See
  [Secret Masking](https://github.com/slidict/wip/wiki/Secret-Masking).
- **`--debug` shows where time is going** — a step trace plus periodic host resource snapshots.
  See [Debug Output](https://github.com/slidict/wip/wiki/Debug-Output).

> **v2 switched from Ruby to a C# Native AOT binary** — `wip.exe` is now a single self-contained
> executable with no runtime to install. The Ruby implementation still works and stays available
> at [v1.1.4](https://github.com/slidict/wip/releases/tag/v1.1.4) / the
> [`wslc-wip` gem](https://rubygems.org/gems/wslc-wip), but has no further updates planned.

### Which mode should you use?

`wip.yml` runs in one of three modes, set with `mode:`. Pick the one that matches your project:

| Situation | Use |
|---|---|
| No `compose.yml` — wip manages containers directly | `mode: container` (default) |
| Have `compose.yml`, don't want to install a third-party tool | `mode: compose-native` |
| Have `compose.yml` and already use/prefer a third-party compose-for-`wslc` tool | `mode: compose` |

`wip init` picks `compose-native` automatically when it finds a `compose.yml`/`.yaml` or
`docker-compose.yml`/`.yaml` next to it, and `container` otherwise. For the full breakdown,
trade-offs, and `compose.yml` key support, see
[Choosing a Mode](https://github.com/slidict/wip/wiki/Choosing-a-Mode) on the wiki.

## Requirements & installation

Windows with WSL2 and Microsoft WSLC. There is no runtime to install: `wip.exe` is a
self-contained Native AOT binary.

Install directly from this repository with [Scoop](https://scoop.sh/):

```powershell
scoop install https://raw.githubusercontent.com/slidict/wip/main/wip.json
```

Or with WinGet:

```powershell
winget install Slidict.Wip
```

Or manually: download and extract `wip-<version>-win-x64.zip` from
[Releases](https://github.com/slidict/wip/releases), then add the directory holding `wip.exe`
to your PATH.

Building from source requires MSVC build tools in addition to the .NET SDK — see
[Building from Source](https://github.com/slidict/wip/wiki/Building-from-Source) for the
one-time setup and common linker errors.

## Quick start

This walks through `mode: container` (the default). Already have a `compose.yml`? See
[Which mode should you use?](#which-mode-should-you-use) first, or read the wiki's
[Getting Started](https://github.com/slidict/wip/wiki/Getting-Started) guide.

```powershell
cd my-project
wip init   # writes a starter wip.yml; edit the TODOs, then:
wip doctor
wip build
wip up -d
wip rails console
```

## Examples

The quick start above is intentionally minimal. For fuller `wip.yml`/`compose.yml` pairs modeled
on real stacks — with the sidecars, healthchecks, and day-to-day commands a project actually
ends up with — see [`examples/`](examples):

- [`examples/rails`](examples/rails) — Rails + Postgres + Redis, `mode: container`
- [`examples/node`](examples/node) — Node.js + MySQL + Redis, `mode: compose-native`

Each has its own README with copy-and-adapt setup instructions.

## Configuration

Put a `wip.yml` in your project root. Running from a subdirectory walks up to find it, or pass
`--config PATH` to point at one explicitly.

```yaml
version: 1
mode: container # default
container: app # required once dependencies: has entries
dependencies:
  app:
    image: slidict/slidict:development
    workdir: /app
    command: server
    ports:
      - "3000:3000"
    volumes:
      - ".:/app"
interaction:
  rails:
    type: exec
    command: bin/rails
    container: app
    interactive: true
```

Every config key — dependencies, healthchecks, `interaction:`/`commands:`, env files,
`.dockerignore` handling, source sync, secret masking — is covered in full on the wiki, starting
at the **[Configuration Reference](https://github.com/slidict/wip/wiki/Configuration-Reference)**.

## Commands

| Command | Description |
|---|---|
| `wip init` | Write a starter `wip.yml` |
| `wip doctor` | Diagnose WSL2, WSLC, config, and the `--ai` host |
| `wip build` / `wip up [-d]` / `wip down` | Build the image / start the stack / stop and remove it |
| `wip exec` / `wip run` | Run a command in the existing container / a new ephemeral one |
| `wip shell` | Open the configured shell |
| `wip logs [-f]` | Follow logs |
| `wip NAME ARGS...` | Run `interaction.NAME`, appending any extra arguments |

Every command's flags, per-mode behavior, and examples are on the
**[CLI Command Reference](https://github.com/slidict/wip/wiki/CLI-Command-Reference)**.

## Development

```bash
git clone https://github.com/slidict/wip.git
cd wip
dotnet build wip.slnx
dotnet test tests/Wip.Tests/Wip.Tests.csproj
```

Requires the .NET 10 SDK. The test suite doesn't need WSLC and runs on Linux and macOS as well
as Windows. See [CONTRIBUTING.md](CONTRIBUTING.md) for the full setup, and
[Development](https://github.com/slidict/wip/wiki/Development) on the wiki for the e2e suite,
the golden-corpus tests, and architecture notes. Known gaps and deferred decisions are tracked in
[docs/csharp-migration-plan.md](docs/csharp-migration-plan.md).

## Contributing

Bug reports and pull requests are welcome on [GitHub](https://github.com/slidict/wip). See
[CONTRIBUTING.md](CONTRIBUTING.md) for commit conventions, versioning policy, and the PR
checklist.

## License

[MIT License](LICENSE)
