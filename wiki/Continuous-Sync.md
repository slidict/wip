# Continuous Sync

Keeping the container's copy of your source current while you edit, with `wip sync --watch`.

Prerequisite: a `sync:` block. See [Source Sync](Source-Sync) and, for *why*,
[Fixing a Slow Boot](Fixing-a-Slow-Boot).

## The two-terminal workflow

```bash
# terminal 1 — the stack
wip up -d

# terminal 2 — the watcher
wip sync --watch
```

```console
$ wip sync --watch
wip: syncing /home/me/app -> app-src:/app every 2s (Ctrl-C to stop)
```

Edit on the host as usual; changes land in the container within one interval.

This is a **foreground loop, not a daemon** — wip has no background service by design. Keep the
terminal open; closing it stops the watcher. See [Concepts](Concepts#design-stance).

## Tuning the interval

```bash
wip sync --watch --interval 5
```

or permanently:

```yaml
sync:
  interval: 5
```

Default is `2` seconds. Considerations:

- Each tick is a full `rsync` pass. With good `exclude` patterns, that's a quick metadata scan; on
  a huge unexcluded tree it isn't.
- Shorter intervals mean shorter edit-to-container latency but more constant scanning.
- `2`–`5` seconds suits most projects. If a pass takes longer than the interval, raise it.

Must be positive:

```
--interval must be a positive number
```

## Failures don't stop the loop

Unlike a one-shot `wip sync`, a failed mirror inside the watch loop prints the error and retries on
the next tick. Restarting the app container mid-loop won't kill your watcher.

Stop with `Ctrl-C`:

```console
^C
wip: sync stopped
```

## The one-way gotcha

The mirror is host → volume, with `--delete` on by default. **Anything the app writes under the
target is removed on the next pass.** With a 2-second watcher, that means "almost immediately."

Concretely, these get wiped unless handled:

| Written by the container | Fix |
|---|---|
| `tmp/`, `log/` | add to `exclude` |
| `node_modules/` (installed inside the container) | add to `exclude` |
| Installed gems under `vendor/bundle` | `exclude`, or give it its own volume |
| Compiled assets (`public/assets`) | add to `exclude` |
| Generated files you want on the host (scaffolds, migrations) | copy them out, or see below |

```yaml
sync:
  exclude:
    - .git
    - log/
    - tmp/
    - node_modules/
    - vendor/bundle/
    - public/assets/
```

Or, if excluding isn't practical:

```yaml
sync:
  delete: false      # never remove anything from the volume
```

The cost of `delete: false` is drift: files you delete on the host stay in the volume forever,
which for a Ruby or JS project means deleted classes and modules can still be autoloaded. Prefer
`exclude` where you can.

### Recovering generated files

`wip sync` only goes one way. To get a generator's output back onto the host, copy it out of the
container:

```bash
wip exec cat db/migrate/20260810120000_add_index.rb > db/migrate/20260810120000_add_index.rb
```

Or run generators through [`wip run`](wip-run) with a normal bind mount for that path.

## Which container runs the mirror

| `sync.mode` | Where the watcher's rsync runs | Requires |
|---|---|---|
| `exec` (default, container / compose-native) | inside the running primary container | that container up, with rsync in its image |
| `run` (default and only option under `mode: compose`) | a throwaway container per tick | `sync.image` or `sync.build` |

Under `exec`, start the stack **before** the watcher. Under `run`, order doesn't matter. See
[Sync Modes](Sync-Modes).

`sync.build`'s image is built **once** before the loop starts, not on every tick.

## Do I need the watcher at all?

No. `wip sync` on demand is often enough:

```bash
# edit files…
wip sync && wip rspec
```

Or wire it into an interaction so you can't forget:

```yaml
interaction:
  test:
    command: bundle exec rspec
```

```bash
wip sync && wip test
```

The watcher is worth it when you're iterating quickly, or when a file-watching dev server inside
the container needs to see changes without you thinking about it.

## Related

- [wip sync](wip-sync)
- [Source Sync](Source-Sync)
- [Sync Modes](Sync-Modes)
- [Fixing a Slow Boot](Fixing-a-Slow-Boot)
