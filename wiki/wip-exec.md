# wip exec

Runs a command inside the **already-running** primary container.

```
wip exec [--no-interactive] COMMAND...
```

```bash
wip exec bundle install
wip exec bin/rails db:migrate
wip exec ls -la /app
```

## What it builds

```
wslc exec [-it] [-w WORKDIR] [-u USER] [-e KEY=VALUE …] CONTAINER command…
```

Values come from the primary `dependencies:` entry (or `compose.service`'s definition under
compose-native): `workdir`, `user`, `env`, plus everything from `.env`.

**Ports and volumes are not applied.** The container already exists with whatever it was created
with; `exec` attaches to it. If you added a port or a mount to `wip.yml`, you need
`wip down && wip up -d`, not `exec`.

## Flags

### `--no-interactive`

Skips the `-it` flags, so nothing is attached to stdin:

```bash
wip exec --no-interactive bundle exec rspec
```

Use it in scripts and CI, or when the command reads from a pipe. Note that even without this flag,
wip only allocates a TTY when stdin **and** stdout are both real TTYs — so piped output already
degrades gracefully. See [TTY Allocation](TTY-Allocation).

## Requires a running container

```console
$ wip exec ls
Error: container "app" is not running
```

(The exact message comes from `wslc`.) Start it first:

```bash
wip up -d
```

If you want a command that doesn't need a running container, use [`wip run`](wip-run) instead.

## `exec` vs. `run`

| | `wip exec` | [`wip run`](wip-run) |
|---|---|---|
| Container | the existing one | a fresh `--rm` one |
| Requires `wip up` first | yes | no |
| Shares state with the running app | yes | no |
| Ports / volumes applied | no | yes |
| Startup cost | none | a container start |
| Under `mode: compose` | bridged `exec` | falls back to `exec` |

Reach for `exec` by default — it's faster and sees the same filesystem the app does.

## Under compose mode

Bridged to the external tool:

```
<compose command> -f FILE [-p PROJECT] exec [-T] <compose.service> command…
```

`-T` is added when non-interactive.

## Prefer named interactions

Typing `wip exec bundle exec rspec` every time is what `interaction:` exists to avoid:

```yaml
interaction:
  rspec:
    command: bundle exec rspec
```

```bash
wip rspec --fail-fast
```

See [Interactions](Interactions).

## Related

- [wip run](wip-run)
- [wip shell](wip-shell)
- [TTY Allocation](TTY-Allocation)
- [Interactions](Interactions)
