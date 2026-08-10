# CLI Command Reference

Index of every `wip` command. Each has its own page with flags, behavior per mode, and examples.

## Commands

| Command | Description | Page |
|---|---|---|
| `wip init [--force] [--template NAME]` | Write a starter `wip.yml` | [wip init](wip-init) |
| `wip version` | wip's version, plus WSLC's if detectable | [wip version](wip-version) |
| `wip doctor` | Diagnose WSL2, interop, WSLC, config, architecture, Git | [wip doctor](wip-doctor) |
| `wip config` | Print the effective configuration (secrets masked) | [wip config](wip-config) |
| `wip build [--no-cache] [-- OPTIONS]` | Build the configured image | [wip build](wip-build) |
| `wip up [-d] [--no-sync] [--no-cache] [--watch] [--interval N]` | Start the stack | [wip up](wip-up) |
| `wip stop` | Stop containers without removing them | [wip stop](wip-stop) |
| `wip down` | Stop and remove containers | [wip down](wip-down) |
| `wip exec [--no-interactive] COMMAND...` | Run a command in the running container | [wip exec](wip-exec) |
| `wip run [--no-interactive] COMMAND...` | Run a command in a fresh container | [wip run](wip-run) |
| `wip shell` | Open a shell, falling back `bash` → `sh` | [wip shell](wip-shell) |
| `wip logs [-f] [SERVICE...]` | Follow logs (compose modes only) | [wip logs](wip-logs) |
| `wip sync [-w] [--interval N]` | Mirror the source into the sync volume | [wip sync](wip-sync) |
| `wip dispatch NAME [ARGS...]` | Run an interaction explicitly | [wip dispatch](wip-dispatch) |
| `wip NAME ARGS...` | Run `interaction.NAME` | [Interactions](Interactions) |

## Cross-cutting

- [Global Options](Global-Options) — `--config`, `--env-file`, `--debug`, `--debug-log`, and where they may appear
- [Debug Output](Debug-Output) — reading `--debug`, resource snapshots, telling wip overhead from container time
- [TTY Allocation](TTY-Allocation) — how `interactive:`, `--no-interactive`, and real-TTY detection combine

## Availability by mode

| Command | `container` | `compose-native` | `compose` |
|---|---|---|---|
| `init` `version` `doctor` `config` | ✔ | ✔ | ✔ |
| `build` | ✔ | ✔ | ✔ (interaction must be `type: exec`) |
| `up` | ✔ | ✔ | ✔ (no `--watch`) |
| `stop` `down` | ✔ | ✔ | ✔ (bridged) |
| `exec` `shell` | ✔ | ✔ | ✔ (bridged) |
| `run` | ✔ real `--rm` | ✔ real `--rm` | falls back to `exec` |
| `logs` | ✘ | ✔ (one service) | ✔ (multi-service) |
| `sync` | ✔ | ✔ | ✔ (`mode: run` only) |
| `dispatch` | ✔ | ✔ | ✔ (`type: exec` only) |

## Exit codes

| Code | Meaning |
|---|---|
| `0` | success |
| `1` | a wip-level failure (`ConfigError`, `wip doctor` found a `[FAIL]`) |
| `127` | the resolved binary couldn't be executed |
| `130` | interrupted (`Ctrl-C`) |
| `128 + N` | the child was killed by signal `N` |
| anything else | passed through from `wslc` / the child process |

`wip` exits with the child's exit code for the command-running paths, so `wip rspec` fails your CI
step exactly when `rspec` does.

## Command name resolution

An unrecognized first argument is routed to `dispatch`, which is what makes `wip rspec` work
without a `dispatch` prefix. Built-in names always win over an interaction of the same name — see
[wip dispatch](wip-dispatch).
