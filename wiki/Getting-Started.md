# Getting Started

From nothing to your first `wip up`. If you already know which mode you want, jump straight to
[Container Mode](Container-Mode), [Compose Native Mode](Compose-Native-Mode), or
[Compose Mode](Compose-Mode).

## Prerequisites

| Requirement | Why |
|---|---|
| Ruby 3.2+ | `wip` is a Ruby gem (`required_ruby_version >= 3.2`) |
| WSL2 | WSLC containers run in a WSL2-backed VM |
| Microsoft WSLC | `wip` shells out to `wslc.exe` / `wslc` for everything |

Confirm `wslc` itself is reachable before installing anything:

```bash
wslc.exe version || wslc version
```

`wip` looks for the binary in this order when `wslc.command` is left at `auto`:

1. `wslc.exe` (on `PATH`)
2. `wslc` (on `PATH`)
3. `/mnt/c/Windows/System32/wslc.exe`

If none of those exist, see [WSLC Not Found](WSLC-Not-Found).

## Installation

```bash
gem install wslc-wip
```

The gem is named `wslc-wip`; the command it installs is `wip`.

From source, when you want to run an unreleased revision or hack on it:

```bash
git clone https://github.com/slidict/wip.git
cd wip
bundle install
bundle exec exe/wip version
```

Check what you got:

```bash
wip version
```

That prints wip's own version and then asks `wslc` for its version — see [wip version](wip-version).

## Generate a wip.yml

```bash
cd my-project
wip init
```

`wip init` looks for `compose.yml` / `compose.yaml` / `docker-compose.yml` / `docker-compose.yaml`
next to the file it's about to write:

- **found** → writes `mode: compose-native`, pointing at that file
- **not found** → writes `mode: container`, with a `dependencies:` skeleton

It refuses to overwrite an existing `wip.yml` unless you pass `--force`, and `--template
rails|node|rust|csharp` picks a stack-appropriate default `sync.exclude` list. Full details:
[wip init](wip-init).

The generated file is deliberately full of comments and `TODO:` markers. At minimum you need to
fill in:

- **container mode** — `dependencies.app.image` (and rename `app` / `container:` if you like)
- **compose-native mode** — `compose.service`, so wip knows which service is "the app"

## Verify it works

```bash
wip doctor
```

Every check prints `[OK]`, `[WARN]`, or `[FAIL]`. Warnings alone still exit `0`; a WSL2, interop,
WSLC, config, or sync failure exits `1`. See [wip doctor](wip-doctor) for what each line means.

## First boot

```bash
wip build     # only if you have a build: / interaction.build definition
wip up -d
wip shell
```

`wip up` creates the network (if `network:` is set), starts every sidecar, mirrors the source if
you configured [Source Sync](Source-Sync), then starts the primary container. See [wip up](wip-up).

Then run your project's own commands through the interactions you declared:

```bash
wip rails console
wip rspec
```

Those come from the `interaction:` block — see [Interactions](Interactions).

## Where to go next

| Question | Page |
|---|---|
| Which mode should I use? | [Choosing a Mode](Choosing-a-Mode) |
| What does every `wip.yml` key do? | [Configuration Reference](Configuration-Reference) |
| What commands exist? | [CLI Command Reference](CLI-Command-Reference) |
| Boot feels unbearably slow | [Fixing a Slow Boot](Fixing-a-Slow-Boot) |
| I'm coming from `dip` | [Migrating from dip](Migrating-from-dip) |
| Something failed | [Troubleshooting & FAQ](Troubleshooting-and-FAQ) |
