# Comparison

Why `wip`, and how it relates to the tools next to it.

## The short answer

`wip` is to WSLC roughly what `dip` is to Docker Compose: a project-local workflow CLI that turns
long, flag-heavy runtime invocations into `wip rspec`. It doesn't replace `wslc`; every command
ends in one.

## Pages

- [wip vs dip](wip-vs-dip) — shared ground, differences, what isn't covered yet
- [wip vs docker compose](wip-vs-docker-compose) — what consolidating into `wip.yml` buys you over driving `wslc` by hand
- [Third Party Compose Tools](Third-Party-Compose-Tools) — the compose-for-`wslc` ecosystem, and why wip doesn't pick a winner

## At a glance

| | `wip` | `dip` | `docker compose` | plain `wslc` |
|---|---|---|---|---|
| Runtime | WSLC | Docker | Docker | WSLC |
| Named project commands | ✔ `interaction:` | ✔ `interaction:` | ✘ | ✘ |
| Declares services | ✔ `dependencies:` / reuses `compose.yml` | delegates to Compose | ✔ | ✘ |
| Reuses an existing `compose.yml` | ✔ (two modes) | ✔ | ✔ (it *is* it) | ✘ |
| `.env` support | ✔ | ✔ | ✔ | ✘ |
| `.dockerignore` honored on build | ✔ | via Docker | via Docker | ✘ |
| Source-sync for slow mounts | ✔ `sync:` | ✘ | ✘ | ✘ |
| Restart policies | approximated by polling | via Compose | ✔ native | ✘ |
| Health checks | ✘ | via Compose | ✔ | ✘ |
| Background daemon | ✘ by design | ✘ | ✔ (the engine) | — |

## The three ways to run a compose.yml under WSLC

| | wip `compose-native` | wip `compose` | a compose-for-wslc tool directly |
|---|---|---|---|
| External binary | none | required | required |
| Named project commands | ✔ | ✔ | ✘ |
| Compose coverage | a [documented subset](Compose-File-Support) | the tool's | the tool's |
| `run` (ephemeral) | ✔ real `wslc run --rm` | falls back to `exec` | the tool's |
| `logs` | one service | the tool's | the tool's |
| `--watch` restarts | ✔ | ✘ | the tool's |

## Related upstream work

`wslc` has no native Compose support yet — tracked in
[microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948). Both of wip's compose modes
exist because of that gap:

- `compose-native` is the stopgap wip maintains itself
- `compose` is the bridge to whatever the ecosystem produces in the meantime

`wslc` is new and still evolving, so expect this landscape to change. wip's stated intent is that
`wip.yml`'s shape stays stable across it.

## When not to use wip

Being honest about the boundaries:

- **You're on Docker, not WSLC.** Use `dip` and `docker compose`. wip has no Docker backend.
- **You need production orchestration.** wip is a development workflow tool: no daemon, no backoff,
  no health checks, no scaling.
- **Your `compose.yml` needs the full spec and you don't want an extra binary.** `compose-native`'s
  subset may not reach far enough; `mode: compose` plus a real tool will.
