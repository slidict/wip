# Global Options

Four options apply to every command.

| Option | Meaning |
|---|---|
| `--config PATH` | Use this `wip.yml` instead of searching |
| `--env-file PATH` | Load this dotenv file instead of `.env` next to `wip.yml` |
| `--debug` | Print each step wip takes, with timings |
| `--debug-log PATH` \| `-` | Where `--debug` resource snapshots go |

## They may appear before or after the command

```bash
wip up --config other/wip.yml
wip --config other/wip.yml up
```

Both work. wip pulls a leading run of global switches off the front of the arguments and reinserts
them after the command name, since the underlying CLI framework only recognizes them there. Both
`--config PATH` and `--config=PATH` spellings are handled.

This matters most for interactions, where trailing arguments belong to *your* command:

```bash
wip --debug rails console        # --debug is wip's
wip rails console --debug        # also wip's — it's a known global switch
wip rails console -- --debug     # after --, it belongs to bin/rails
```

## `--config PATH`

```bash
wip --config /path/to/wip.yml up
```

Skips the upward directory search. The path is expanded against your current directory. Also
respected by `wip init`, which writes there (and detects a compose file next to *that* path).

See [Config File Discovery](Config-File-Discovery).

## `--env-file PATH`

```bash
wip --env-file config/dev.env up
```

Replaces the default `.env` next to `wip.yml`. Missing files are not an error.

The same file is used for compose.yml `${VAR}` interpolation under
[compose-native mode](Compose-Native-Mode), so both uses always agree. See [Env Files](Env-Files).

## `--debug`

```bash
wip rails c --debug
WIP_DEBUG=1 wip rails c      # equivalent
```

Prints every step — existence probes, the resolved `wslc` invocation, elapsed time — plus periodic
host resource snapshots. Environment values in logged commands are masked as `KEY=***`.

Full explanation of the output: [Debug Output](Debug-Output).

## `--debug-log`

Controls where the periodic resource snapshots go. By default wip decides:

| Command type | Default destination |
|---|---|
| Interactive (`-it`, e.g. `rails console`) | a temp log file, whose path is printed once |
| Non-interactive | inline on stderr |

Overrides:

| Value | Effect |
|---|---|
| `--debug-log=-` | always inline, even for interactive commands |
| `--debug-log=PATH` | always to `PATH` (appended), even for non-interactive commands |

```bash
wip rails c --debug --debug-log=/tmp/wip-debug.log
```

Only the snapshots are redirected — step start/finish lines always go to stderr.

## Environment variables

| Variable | Effect |
|---|---|
| `WIP_DEBUG` | any non-empty value enables `--debug` |

## Related

- [Debug Output](Debug-Output)
- [Config File Discovery](Config-File-Discovery)
- [Env Files](Env-Files)
