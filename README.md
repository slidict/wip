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
- [Sandboxes](#sandboxes)
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

WSLC is generally available starting with [WSL 3.0.1](https://github.com/microsoft/WSL/releases/tag/3.0.1).
Update using the normal release channel, then check the installed version and tooling:

```powershell
wsl --update
wsl --version
wip doctor
```

If Microsoft Store updates are unavailable, download the stable release directly
from GitHub with `wsl --update --web-download`, then run `wip doctor` again.

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

### Starter templates

`wip init --template NAME` preloads the `sync.exclude` list in the generated `wip.yml` with the
directories that stack regenerates inside the container, or that are too large to mirror (and
names the preset in the comment above the list):

| `--template` | `sync.exclude` patterns written |
|---|---|
| `rails` | `.git`, `log/`, `tmp/`, `storage/`, `public/assets/`, `public/packs/`, `.bundle/`, `vendor/bundle/`, `coverage/`, `node_modules/` |
| `ruby` | `.git`, `log/`, `tmp/`, `.bundle/`, `vendor/bundle/`, `coverage/` |
| `node` | `.git`, `node_modules/`, `dist/`, `build/`, `.next/`, `.cache/`, `coverage/` |
| `rust` | `.git`, `target/` |
| `csharp` | `.git`, `bin/`, `obj/`, `.vs/`, `packages/` |

```powershell
wip init --template csharp
```

That is the whole of it — the preset touches no other key. `mode:` is still decided by whether a
compose file sits next to the target, every other key is still written as the same placeholder or
`TODO`, and omitting `--template` just starts `sync.exclude` at `.git`, `tmp/`, `node_modules/`
instead. An unknown name is rejected with the valid list, and `--template` cannot be combined with
`--ai` (which generates the whole file from a natural-language request instead).

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
| `wip security scan <sandbox> --tool <zap|nuclei> --target <url>` | Run one pinned scanner against a target on its isolated network |
| `wip logs [-f]` | Follow logs |
| `wip NAME ARGS...` | Run `interaction.NAME`, appending any extra arguments |

Every command's flags, per-mode behavior, and examples are on the
**[CLI Command Reference](https://github.com/slidict/wip/wiki/CLI-Command-Reference)**.

## Sandboxes

Besides your app's stack, `wip.yml` can declare **named sandboxes** — long-lived containers with
their own image and their own storage, for running a tool somewhere isolated from the project's
own containers. `wip up`, `wip down` and `wip exec` never touch a sandbox, and `wip sandbox` never
touches the stack.

```yaml
version: 1
resource_namespace: example-project

volumes:
  - name: state
    persistent: true # kept when the sandbox is destroyed; `false` means ephemeral
    mount: /home/user/state

sandboxes:
  - name: agent
    image: example/tool:1
    command: [sleep, infinity] # optional argv list; the image must provide this command
    volumes: [state]
```

```powershell
wip sandbox create agent                     # run it, mounting the declared volumes
wip sandbox exec agent --interactive -- sh   # a session you drive; `--` keeps sh's own flags
wip sandbox stop agent                       # pause it — `create` resumes the same container
wip sandbox destroy agent                    # remove it; persistent volumes survive
```

The optional `command` list replaces the image CMD without shell splitting; omitting it
preserves the image CMD. The optional `entrypoint` executable replaces ENTRYPOINT; its
arguments belong in `command`. For Nuclei, use `entrypoint: sleep` with `command: [infinity]`.
Flags and arguments containing spaces remain separate list elements, such
as `[nginx, -g, 'daemon off;']`; the first list element must not contain whitespace.
Changing a declared command or entrypoint requires destroying and recreating the sandbox.

`create` / `list` / `status` / `exec` / `attach` / `stop` / `destroy` are the whole set. `exec`
runs non-interactively with a 300-second deadline unless you pass `--interactive`, which has no
deadline and cannot be combined with `--timeout`. Anything that has to survive `destroy` belongs
in the image or on a persistent volume — and installing or signing into whatever CLI the image
ships is outside wip's scope.

For the operations and their guarantees, see [wip sandbox](https://github.com/slidict/wip/wiki/wip-sandbox); for
the configuration and ownership model, [Sandboxes](https://github.com/slidict/wip/wiki/Sandboxes);
for declared volumes on their own, [volume storage](docs/volume-lifecycle.md); for putting a
sandbox behind a network it cannot reach past,
[network isolation](docs/sandbox-network-isolation.md); for running one built-in diagnostic
tool behind that boundary, [the security scan engine](docs/security-scan-engine.md).

**Sandbox networking is IPv4 only, because WSLC is.** Measured against WSLC 3.0.3: a network
created with an IPv6 subnet and gateway is accepted and the subnet is recorded in its IPAM
configuration, but `EnableIPv6` stays false, an IPv4 subnet is assigned alongside it, and a
container on that network gets no IPv6 address at all. The docker driver option
`com.docker.network.enable_ipv6=true` is stored among the network's options and ignored. So
an IPv6 declaration does not fail — it silently produces an IPv4-only network, which is the
part worth knowing. Anything that depends on IPv6, including diagnosing a target's IPv6
exposure, is out of reach until WSLC supports it.

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

A Docker Desktop vs. Wip/WSLC benchmark workflow lives under
[`benchmark/`](benchmark), driven by the
[`wip-benchmark`](.claude/skills/wip-benchmark/SKILL.md) skill. Latest run (2026-09-16, single
Windows 11 PC): wip served ~40% higher throughput and about half the p50 latency versus Docker
Desktop, at roughly half the host CPU under load — see the
[full report](benchmark/results/20260915-2252/report.md) for the numbers and caveats.

## Contributing

Bug reports and pull requests are welcome on [GitHub](https://github.com/slidict/wip). See
[CONTRIBUTING.md](CONTRIBUTING.md) for commit conventions, versioning policy, and the PR
checklist.

## License

[MIT License](LICENSE)
