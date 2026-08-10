# Architecture

How the codebase is laid out, for contributors. User-facing behavior lives elsewhere in this wiki;
this page is about the code.

## The flow of a command

```
exe/wip
  └─ Wip::CLI (Thor)
       ├─ ConfigLoader ──► Config ──► SyncSettings
       │                      └────► ComposeFile ──► VariableInterpolation
       ├─ CommandResolver ──► the wslc binary
       ├─ CommandBuilder ──► an argument array
       │      └─ DotenvLoader supplies -e defaults
       ├─ BuildContext ──► DockerIgnore, StagingProgress   (build paths only)
       └─ CommandRunner ──► spawns it
              ├─ DebugReporter ──► ResourceMonitor
              ├─ CommandDisplay  (masks -e values)
              └─ ErrorInterpreter (hints on failure)
```

## Modules

### Configuration

| File | Responsibility |
|---|---|
| `config_loader.rb` | Finds `wip.yml` (walking up from the cwd, or `--config`), parses it safely (no aliases, no custom classes) |
| `config.rb` | Validated, defaulted access to the parsed document. Every `ConfigError` for `wip.yml` originates here |
| `sync_settings.rb` | The `sync:` block: defaults, path validation, mode rules, and the rsync invocation |
| `compose_file.rb` | Parses `compose.yml` into the shape `Config#dependencies` expects; owns the supported-subset rules and topological ordering |
| `variable_interpolation.rb` | `${VAR}` substitution over an already-parsed YAML tree |
| `dotenv_loader.rb` | `.env` parsing |

`Config` is the single source of truth for "what does this project want?" Under compose-native it
delegates to `ComposeFile` and presents the result through the same accessors, which is why the
rest of the codebase barely knows which mode it's in.

### Command construction

| File | Responsibility |
|---|---|
| `command_builder.rb` | Builds `wslc` argument arrays for exec/run/up/start/stop/remove/build/logs/network/sync |
| `compose_bridge.rb` | Builds argument arrays for an external compose-for-`wslc` binary, and resolves the compose file path |
| `command_resolver.rb` | Locates an executable from a candidate list; raises `CommandNotFoundError` listing what it tried |
| `command_display.rb` | Renders a command array for logs, masking `-e KEY=value` |

Nothing here executes anything. That separation is what makes the suite runnable without WSLC.

### Execution

| File | Responsibility |
|---|---|
| `command_runner.rb` | Spawns a command and returns its exit status. Piped (`Open3.popen3`) or behind a pty (`PTY.spawn`), with a `Process.spawn` fallback on Windows |
| `environment.rb` | Host facts: WSL2, Windows interop, architecture, whether stdio is a TTY |
| `error_interpreter.rb` | Pattern-matches known failure output into a friendlier hint |

`CommandRunner` is the fiddliest file in the repo. Its complexity is all in service of one goal:
give the child a *real* terminal (job control, `Ctrl-C`, `isatty`) while still routing output
through wip so hints can be generated. Hence the pty rather than inherited fds, raw-mode stdin,
and `SIGWINCH` re-syncing.

### Build context

| File | Responsibility |
|---|---|
| `build_context.rb` | Stages a context: `.dockerignore` filtering, temp staging, and the persistent Windows-side shadow with its manifest, locking, and atomic copies |
| `docker_ignore.rb` | gitignore-style pattern matching, including negation and prune-safety |
| `staging_progress.rb` | The self-overwriting "copying N/total" line, on its own thread |

### Diagnostics

| File | Responsibility |
|---|---|
| `doctor.rb` | Runs the checks and returns `Result(level, message)` values |
| `debug_reporter.rb` | `--debug` step narration, timings, and where snapshots go |
| `resource_monitor.rb` | Periodic load/memory/disk-IO/top-process snapshots on a background thread |

### Everything else

| File | Responsibility |
|---|---|
| `cli.rb` | Thor command definitions, orchestration, and the global-switch reordering that lets `wip --config X up` work |
| `initializer.rb` | Generates `wip.yml` (mode detection + templates) |
| `errors.rb` | `Error` → `ConfigError`, `CommandNotFoundError` |
| `version.rb` | `Wip::VERSION` |

## Conventions that hold across the codebase

**Argument arrays, never shell strings.** Values from config reach the process spawner as discrete
arguments. The only intentional splitting is a configured `command:`, split with shell-word rules.

**Validate at load time, name the key.** Every `ConfigError` says which key is wrong, and is raised
before any container side effect. The one deliberate exception is compose.yml's top-level sections,
which are ignored rather than rejected.

**Explicit over inferred.** `mode:` is declared, `container:` has no default, `sync.mode` is
configured rather than probed, `compose.command` has no default.

**Isolate assumptions about `wslc`'s output.** Anything that depends on wslc's JSON shape lives in
one method, so a future wslc change is a one-line fix. `CLI#container_status` is the model: it
parses `State` in one place and logs the raw entry under `--debug`.

**Comments carry the *why*.** The codebase leans heavily on comments that record non-obvious
constraints — why an absolute build context crashes `wslc`, why `restart: no` is a YAML boolean,
why the shadow root can't be inside the context, why a pty rather than inherited fds. Preserve them
when you touch the code.

## Deliberately temporary code

`compose_file.rb` carries a note saying so: it exists only because `wslc` has no native Compose
support ([microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)). When that lands, it
and its hooks in `config.rb`, `cli.rb`, `doctor.rb`, and `initializer.rb` come out together.

## RuboCop exemptions

`.rubocop.yml` raises `Metrics/ClassLength` for `cli.rb`, `compose_file.rb`, and `config.rb`. Those
are acknowledged as large; treat the exemption as a debt marker, not a licence to grow them
further. `Layout/LineLength` is 120.

## Related

- [Development](Development)
- [Testing and Linting](Testing-and-Linting)
- [Concepts](Concepts)
