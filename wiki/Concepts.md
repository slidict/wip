# Concepts

What `wip` is, what it deliberately isn't, and the ideas the rest of the wiki assumes you know.

## What `wip` is

`wip` is a project-local workflow CLI for Microsoft WSLC, in the spirit of
[`dip`](https://github.com/bibendi/dip) for Docker. It collects a project's container, image,
environment variables, mounts, and everyday commands into a single `wip.yml`, and forwards them to
`wslc.exe` / `wslc`.

The point is that `wip rails console` should work on a freshly cloned machine without anyone
remembering the twelve-flag `wslc exec` line behind it.

## What `wip` is not

- **Not a container runtime.** Every operation ends in a `wslc` invocation.
- **Not a Compose implementation.** `mode: compose-native` implements a deliberately small subset,
  as a stopgap until `wslc` ships Compose support of its own
  ([microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)). See
  [Compose File Support](Compose-File-Support).
- **Not a daemon.** There is no background service. Every `--watch` variant is a foreground poll
  loop tied to an open terminal (see [Design stance](#design-stance) below).

## The three modes

`mode:` is the central concept. See [Choosing a Mode](Choosing-a-Mode) for the decision, and:

- [Container Mode](Container-Mode) — wip declares containers itself in `dependencies:`
- [Compose Native Mode](Compose-Native-Mode) — wip parses `compose.yml` and drives `wslc`
- [Compose Mode](Compose-Mode) — wip bridges to an external compose-for-`wslc` binary

```
mode: container
  wip ──► wslc ──► containers declared in wip.yml

mode: compose-native
  wip ──► compose.yml parser ──► wslc ──► containers declared in compose.yml

mode: compose
  wip ──► external compose-for-wslc binary ──► wslc ──► containers
```

## Design stance

### Arguments are arrays, never shell strings

Every command wip runs is built as an argument array and handed to the process spawner directly —
there is no intermediate shell, so a value containing spaces, quotes, `;`, or `$(...)` is passed
through as one literal argument instead of being re-interpreted. This is why `env` values, ports,
and volume specs from `wip.yml` are safe even when they come from a `.env` file you didn't write.

The one place a string *is* split is a command definition (`command: bundle exec rspec`), which is
split with shell-word rules so the familiar spelling keeps working.

### No resident daemon

`wip up --watch` and `wip sync --watch` are foreground loops. They print what they're watching,
poll on an interval, and stop on `Ctrl-C`. Nothing survives closing the terminal. This is a
deliberate choice, not a missing feature: a background supervisor would need its own lifecycle,
logs, and failure modes, and `wslc` offers no event stream to build one on top of.

The consequences show up in [Restart Policies](Restart-Policies) (status-based, not event-based)
and [Continuous Sync](Continuous-Sync) (keep a second terminal open).

### Explicit over inferred

- `mode:` is declared, not guessed from whether a `compose:` block exists.
- `container:` has no default — a project with `dependencies:` must say which entry is primary.
- `sync.mode` is fixed by config, not probed from whether a container happens to be running.
- `compose.command` has no default, because picking a third-party implementation isn't wip's call.

### Fail at load time, with the offending key named

Configuration problems raise a `ConfigError` when `wip.yml` (and, under compose-native,
`compose.yml`) is loaded — before any container is created — and the message names the key. The
one deliberate exception is compose.yml's top-level sections (`networks:`, `volumes:`, `configs:`,
`secrets:`), which belong to real Compose tools and are ignored rather than rejected. See
[Configuration Errors](Configuration-Errors).

## Layers inside wip

Roughly, a command flows through:

```
ConfigLoader ──► Config ──► CommandBuilder ──► CommandRunner ──► wslc
   finds &        validated    builds the        spawns it,
   parses         accessors    argument array    pumps I/O
   wip.yml
```

with `CommandResolver` picking the `wslc` binary, `DebugReporter`/`ResourceMonitor` narrating it
under `--debug`, and `ErrorInterpreter` translating known failure output into hints. Contributor
detail: [Architecture](Architecture).

## Glossary

Terms used throughout this wiki are defined once on [Glossary](Glossary) — primary container,
sidecar, interaction, sync volume, shadow context, and WSLC's container states.
