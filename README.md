<img src="docs/logo.png" alt="wip logo" width="120" align="left">

# wip

[![Tests](https://github.com/slidict/wip/actions/workflows/test.yml/badge.svg)](https://github.com/slidict/wip/actions/workflows/test.yml)
[![License: MIT](https://img.shields.io/github/license/slidict/wip.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-windows%20x64-blue.svg)](#requirements--installation)

**Run containerized tools like local commands â€” on WSL Containers.**

wip is a native, standalone CLI for Microsoft WSL Containers (WSLC). Define your environment
and project commands in `wip.yml`, then use commands like `wip rails console` or `wip rspec`
from your terminal. Inspired by [dip](https://github.com/bibendi/dip), powered by `wslc`.

**[Documentation / Wiki](https://github.com/slidict/wip/wiki)** Â·
[Homepage](https://wslc-wip.slidict.com/) Â·
[Examples](examples) Â·
[Releases](https://github.com/slidict/wip/releases)

![wip demo](https://raw.githubusercontent.com/slidict/wip/main/docs/demo.gif)

> Early release: wip tracks WSLC as its interface evolves.

## Highlights

- **Native, standalone executable.** A single C# Native AOT binary; no Ruby or .NET runtime to install.
- **Your commands, fewer flags.** Map project commands to `wip <name>` with arguments, environment,
  and working directory configured once. [Interactions â†’](https://github.com/slidict/wip/wiki/Interactions)
- **Reuse your Compose file.** Run supported `compose.yml` configurations directly on WSLC,
  or manage containers in `wip.yml`. [Modes and compatibility â†’](https://github.com/slidict/wip/wiki/Choosing-a-Mode)
- **Local AI setup.** Generate or update `wip.yml` with `wip init --ai` using Ollama or LM Studio;
  review and confirm before saving. [AI setup â†’](https://github.com/slidict/wip/wiki/AI-Assisted-Initialization)
- **Source sync and stack management.** Mirror source into a named volume, keep it synced with
  `wip sync --watch`, and start dependencies with health checks.
  [Source sync â†’](https://github.com/slidict/wip/wiki/Source-Sync) Â·
  [Dependencies â†’](https://github.com/slidict/wip/wiki/Dependencies)

## Requirements & installation

Requires **Windows x64, WSL2, and Microsoft WSLC**. Install wip with WinGet:

```powershell
winget install Slidict.Wip
```

Or use [Scoop](https://scoop.sh/):

```powershell
scoop install https://raw.githubusercontent.com/slidict/wip/main/wip.json
```

For manual installation, extract the Windows x64 ZIP from
[Releases](https://github.com/slidict/wip/releases) and add its directory to `PATH`.

### Running it from a WSL2 shell

Use `wip.exe` instead of `wip` in bash; the Windows executable must be on `PATH`.
Keep projects on the Windows filesystem (for example, `C:\src\my-project`, accessed as
`/mnt/c/src/my-project` from WSL). WSL-filesystem paths have mount limitations.

## Quick start

Run these commands from your project directory:

```powershell
wip init        # Generate wip.yml, then fill in its TODOs before continuing
wip doctor
wip up -d
wip shell
```

`wip init` selects `compose-native` when it finds a Compose file, or `container` otherwise.
If your configuration defines an image build, run `wip build` before `wip up -d`.

Add project commands to the generated `wip.yml`. For example, in a Rails container with
`bin/rails` available in its configured working directory:

```yaml
interaction:
  rails:
    command: bin/rails
    interactive: true
  rspec:
    command: bundle exec rspec
```

```powershell
wip rails console
wip rspec
```

For complete configurations, see [Rails + Postgres + Redis](examples/rails) or
[Node.js + MySQL + Redis](examples/node).

## Documentation

Detailed configuration, command flags, and guides live in the **[Wiki](https://github.com/slidict/wip/wiki)**.

| What you need | Where to go |
|---|---|
| Choose a mode | [Choosing a Mode](https://github.com/slidict/wip/wiki/Choosing-a-Mode) |
| Check Compose compatibility | [Compose Native Mode](https://github.com/slidict/wip/wiki/Compose-Native-Mode) |
| Configure `wip.yml` | [Configuration Reference](https://github.com/slidict/wip/wiki/Configuration-Reference) |
| Find commands and flags | [CLI Command Reference](https://github.com/slidict/wip/wiki/CLI-Command-Reference) |
| Diagnose a problem | [Troubleshooting & FAQ](https://github.com/slidict/wip/wiki/Troubleshooting-and-FAQ) |

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for development
setup, testing, and contribution guidelines.

## License

[MIT](LICENSE)
