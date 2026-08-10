# wip up

Brings the stack up: builds what needs building, creates the network, starts sidecars, mirrors the
source, and starts the primary container.

```
wip up [-d] [--no-sync] [--no-cache] [--watch] [--interval N]
```

## What it does, in order

Under `mode: container` and `mode: compose-native`:

1. **Validate `--interval`** (only when `--watch` is given) — before any side effect, so a bad
   value doesn't leave a half-started stack behind.
2. **Build compose-native `build:` services** — once per invocation, in `depends_on` order. No-op
   in container mode. See [Compose Build](Compose-Build).
3. **Create the network** if `network:` is set and missing. See [Networking](Networking).
4. **Start every sidecar**, by name. Existing ones are `start`ed, missing ones created.
5. **Mirror the source** into the sync volume, if `sync:` is configured and `--no-sync` wasn't
   passed. See [Source Sync](Source-Sync).
6. **Start the primary container** — `start` if it exists, `run --name …` if not.
7. **Enter the watch loop**, if `--watch` was given.

```console
$ wip up -d
wip: creating network 'app-tier'
wip: dependency 'redis' not found, creating it
wip: starting existing dependency 'development.mysql'
wip: syncing /home/me/app -> app-src:/app
wip: run `wip sync --watch` in another terminal to keep /app up to date
wip: container 'app' not found, creating it
```

Under [`mode: compose`](Compose-Mode) it's much shorter: mirror the source (unless `--no-sync`),
then delegate to `<compose command> -f FILE [-p PROJECT] up [-d]`.

## Flags

### `-d` / `--detach`

Run the primary container in the background. Without it, wip attaches to the container and your
terminal is held until it exits.

Sidecars are **always** started detached, regardless of this flag.

### `--no-sync`

Skip step 5. Useful when you know the volume is already current and want a faster boot, or when
you're about to run `wip sync --watch` anyway.

Has no effect without a `sync:` block.

### `--no-cache`

Passes `--no-cache` to the compose-native image builds in step 2. No effect in container mode
(nothing is built there) or compose mode.

### `--watch` / `-w` and `--interval N`

Poll every `N` seconds (default `5`) and restart any dependency that has exited and whose
`restart:` allows it:

```console
$ wip up --watch
wip: watching app, mysql for exited restart: containers every 5s (running detached; Ctrl-C to stop)
wip: 'mysql' has exited, restarting it (restart: unless-stopped)
```

Key points:

- **Implies `-d`.** The primary container can't hold an attached TTY while the loop polls on the
  same thread.
- **Foreground loop.** `Ctrl-C` stops supervision; closing the terminal does too.
- **Not available under `mode: compose`** — there's no service list to poll.
- **Status-based, not event-based** — it can't distinguish a crash from a `wip stop` you ran
  elsewhere.

Full semantics and limitations: [Restart Policies](Restart-Policies).

`--interval` must be positive:

```
--interval must be a positive number
```

## Idempotence

Re-running `wip up` against a running stack is safe. Existing containers are `start`ed, which is a
no-op when they're already running. Nothing is recreated.

That also means **config changes don't apply to existing containers**. New ports, volumes, or env
vars require:

```bash
wip down && wip up -d
```

## Common problems

| Symptom | Likely cause |
|---|---|
| App can't resolve `redis` / `db` by name | `network:` unset — see [Networking](Networking) |
| New `ports:` entry isn't listening | container predates the change; `wip down && wip up -d` |
| `container: must be set…` | [Dependencies](Dependencies) |
| `pull access denied` | [Registry Authentication](Registry-Authentication) |
| `no matching manifest for linux/…` | [Architecture Mismatch](Architecture-Mismatch) |
| Boot hangs with low CPU | bind-mount overhead — [Fixing a Slow Boot](Fixing-a-Slow-Boot) |
| `0x8007000e` / too many mounted volumes | [Volume Limit Reached](Volume-Limit-Reached) |

## Related

- [wip stop](wip-stop) / [wip down](wip-down)
- [Restart Policies](Restart-Policies)
- [Source Sync](Source-Sync)
- [Debug Output](Debug-Output) — `wip up --debug` when boot is slow
