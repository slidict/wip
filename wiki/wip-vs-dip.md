# wip vs dip

[`dip`](https://github.com/bibendi/dip) is the tool `wip` is modeled on. If you know dip, most of
wip will feel familiar — the differences come almost entirely from the runtime underneath.

## Shared ground

| | Both |
|---|---|
| Named project commands | `interaction:` — same key, same idea |
| Alias for it | `commands:` |
| A single verb hiding run-vs-exec | ✔ |
| `.env` loading | ✔ |
| Sidecar services alongside the app | ✔ |
| Run from any subdirectory | ✔ (walks up to find the config) |
| Extra CLI args appended to the configured command | ✔ |

The design goal is the same: `dip rspec` / `wip rspec` should work on a fresh clone without anyone
remembering the underlying invocation.

## Differences

| | `wip` | `dip` |
|---|---|---|
| Runtime | Microsoft WSLC (`wslc.exe` / `wslc`) | Docker |
| Config file | `wip.yml` | `dip.yml` |
| Orchestration | explicit `mode:` — `container` / `compose` / `compose-native` | assumes Docker Compose |
| Declaring services without Compose | ✔ `dependencies:` | ✘ |
| Primary container | `container:` — **no default** | conventionally a named service per command |
| Source-sync for slow mounts | ✔ `sync:` | ✘ (Docker Desktop's own mount tuning instead) |
| Restart policies | approximated by `wip up --watch` polling | via Compose |
| Build-context filtering | wip applies `.dockerignore` itself | Docker does it |
| Persistent Windows-side build context | ✔ `shadow_context:` | n/a |
| `provision:` | ✘ not implemented | ✔ |
| `dip ssh` / agent forwarding | ✘ | ✔ |
| Shell completion | ✘ | ✔ |

## Why the extra concepts exist

Most of what wip has and dip doesn't traces back to WSLC:

- **`sync:`** — WSLC bind mounts always cross a VM boundary over virtiofs, so a framework that
  stats thousands of files at boot is unusably slow. Docker Desktop has its own mitigations; WSLC
  doesn't. See [Fixing a Slow Boot](Fixing-a-Slow-Boot).
- **`.dockerignore` handling** — `wslc build` sends the context as-is, with no ignore support of
  its own. See [Dockerignore](Dockerignore).
- **`shadow_context:`** — the same boundary makes re-sending a large build context expensive. See
  [Shadow Build Context](Shadow-Build-Context).
- **`mode: compose-native`** — `wslc` has no native Compose support
  ([microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)), so someone has to parse
  `compose.yml`. dip could always assume Compose was there.
- **`--watch` restarts** — no restart policy in the runtime, and no event stream to build one on.
  See [Restart Policies](Restart-Policies).

## What dip has that wip doesn't

- **`provision:`** — a declared list of setup commands. Express it as an interaction and run it
  yourself:
  ```yaml
  interaction:
    setup:
      type: run
      command: bin/setup
  ```
- **`dip ssh`** and SSH-agent forwarding.
- **Shell completion.**
- **Nested `subcommands:`** — declare separate top-level entries instead.

## Migrating

Step-by-step: [Migrating from dip](Migrating-from-dip).

## Which should you use?

Straightforward: **whichever matches your runtime.** wip has no Docker backend and dip has no WSLC
backend. If you're running both (a Docker CI and a WSLC laptop, say), keeping both config files is
normal — the `interaction:` blocks look nearly identical.
