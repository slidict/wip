# wip sync

Mirrors the host source tree into the sync volume, once or continuously.

```
wip sync [-w|--watch] [--interval N]
```

Requires a `sync:` block:

```
`wip sync` needs a sync: block in wip.yml
```

See [Source Sync](Source-Sync) for the configuration, and [Sync Modes](Sync-Modes) for `exec` vs.
`run`.

## One-shot

```console
$ wip sync
```

Runs a single `rsync` pass from the read-only source mount into the volume. Where it runs depends
on `sync.mode`:

| `sync.mode` | Runs in |
|---|---|
| `exec` (default under container / compose-native) | the already-running primary container |
| `run` (default and only option under compose) | a throwaway `wslc run --rm` container |

Under `exec`, the container must be up — start it with `wip up -d` first.

Fails loudly: a non-zero exit from `rsync` exits wip with the same code.

## Watch mode

```console
$ wip sync --watch
wip: syncing /home/me/app -> app-src:/app every 2s (Ctrl-C to stop)
```

Re-mirrors every `interval` seconds until interrupted:

```console
^C
wip: sync stopped
```

Unlike the one-shot form, a failed mirror inside the loop does **not** exit — it prints the error
and tries again on the next tick, so a container restarting mid-loop doesn't kill your watcher.

### `--interval N`

Overrides `sync.interval` (default `2`) for this run:

```bash
wip sync --watch --interval 5
```

Must be positive:

```
--interval must be a positive number
```

### The two-terminal workflow

```bash
# terminal 1
wip up -d

# terminal 2
wip sync --watch
```

This is a foreground loop, not a daemon — keep the terminal open. See
[Continuous Sync](Continuous-Sync) for the full workflow and its gotchas.

## `sync.build` is built once per invocation

If `sync.build` is configured, wip builds that image before mirroring — once per `wip sync`
invocation, including once before a `--watch` loop starts, **not** on every tick. See
[Sync Modes](Sync-Modes).

## Name collision

If you also declare an interaction named `sync`, the built-in wins and wip tells you:

```console
$ wip sync
wip: commands.sync in wip.yml is shadowed by the built-in `wip sync`; run it with `wip dispatch sync`
```

See [wip dispatch](wip-dispatch).

## The one-way gotcha

The mirror is host → volume only, with `--delete` on by default. Anything the app writes under the
target is removed by the next pass:

- generated files (scaffolds, migrations) don't come back to the host
- `tmp/`, `log/`, `node_modules/` installed inside the container get wiped

Fix by excluding the path, giving it its own volume, or setting `delete: false`. See
[Source Sync](Source-Sync).

## Common problems

| Symptom | Cause |
|---|---|
| `rsync: not found` | the image running the mirror lacks rsync — [rsync Not Found](rsync-Not-Found) |
| `wip sync` says the container isn't running | `sync.mode: exec` needs `wip up -d` first |
| Changes don't appear in the container | the watcher isn't running, or the path is in `exclude` |
| Files the app wrote keep disappearing | `--delete` — see the gotcha above |

## Related

- [Source Sync](Source-Sync)
- [Sync Modes](Sync-Modes)
- [Continuous Sync](Continuous-Sync)
- [Fixing a Slow Boot](Fixing-a-Slow-Boot)
