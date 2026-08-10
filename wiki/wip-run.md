# wip run

Runs a command in a **fresh, ephemeral container**.

```
wip run [--no-interactive] COMMAND...
```

```bash
wip run bundle exec rails db:migrate
wip run bundle install
```

## What it builds

```
wslc run [--rm] [-it] [-w WORKDIR] [-u USER] [-e KEY=VALUE …] [-p PORT …] [-v VOLUME …] IMAGE command…
```

Everything comes from the primary `dependencies:` entry (or `compose.service`'s definition under
compose-native): `image`, `workdir`, `user`, `env`, `ports`, `volumes`, `remove`, plus `.env`.

Unlike [`wip exec`](wip-exec), **ports and volumes are applied**, since this container is being
created. `--rm` is added when the entry's `remove` is true (the default).

## No running container needed

This is the reason to use it. `wip run` works before `wip up`, or after `wip down` — useful for
setup steps that must happen before the app can boot:

```bash
wip run bundle install
wip run bin/rails db:create db:migrate
wip up -d
```

## Flags

### `--no-interactive`

Skips `-it`. See [TTY Allocation](TTY-Allocation).

## Caveats

- **Nothing is shared with the running app** beyond declared volumes. Files written outside a
  volume vanish with the container.
- **Ports may conflict.** If the primary container is up and publishing `3000:3000`, a `wip run`
  that also publishes it will fail. Either stop the app first or declare a `type: run` interaction
  with `ports: []`.
- **With `sync:` configured**, the run container gets the rewritten mounts (read-only source +
  named volume), same as `wip up` — so it sees the mirrored tree, not your live host edits, unless
  you've synced recently.

## Under compose mode: falls back to exec

The compose bridge's vocabulary is exec-only — there's no ephemeral-container equivalent — so
`wip run` execs into the already-running service instead, and says so:

```console
$ wip run bundle install
wip: compose mode has no ephemeral 'run'; executing in the running 'app' service instead
```

Which means under [`mode: compose`](Compose-Mode), `wip run` **does** require the service to be up.
This is one of the concrete reasons to prefer [`mode: compose-native`](Compose-Native-Mode), where
`wip run` gets a real `wslc run --rm`.

## As an interaction

Declare frequently-run one-offs instead of typing them:

```yaml
interaction:
  migrate:
    type: run
    command: bundle exec rails db:migrate
    remove: true
```

```bash
wip migrate
```

`type: run` interactions are rejected under `mode: compose`. See [Interactions](Interactions).

## Related

- [wip exec](wip-exec) — the comparison table lives there
- [Interactions](Interactions)
- [Compose Mode](Compose-Mode)
