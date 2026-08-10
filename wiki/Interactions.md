# Interactions

Project commands declared in `wip.yml` and run as `wip <name> [args...]`. This is the feature that
turns a twelve-flag `wslc exec` line into `wip rspec`.

## Two spellings, one feature

```yaml
interaction:      # the primary key — same name dip uses
  rspec:
    command: bundle exec rspec
```

```yaml
commands:         # accepted alias
  rspec:
    command: bundle exec rspec
```

Pick one. Declaring both in the same file is a `ConfigError`:

```
commands is mutually exclusive with interaction — pick one
```

`wip config` always prints the effective block under the name `commands:`, whichever you wrote.

## Entry keys

```yaml
interaction:
  rails:
    type: exec            # exec (default) | run | build
    command: bin/rails
    container: app        # override which container to exec into
    interactive: true
    workdir: /app
    user: "1000:1000"
    env:
      RAILS_ENV: development
```

| Key | Default | Notes |
|---|---|---|
| `type` | `exec` (`build` for an entry literally named `build`) | see below |
| `command` | — | split with shell-word rules; extra CLI args are appended |
| `interactive` | `false` | combined with real-TTY detection — see [TTY Allocation](TTY-Allocation) |
| `container` | the primary container | `exec` target override |
| `workdir`, `user`, `env`, `ports`, `volumes`, `image`, `remove` | inherited from the primary entry | any of them can be overridden per command |

Every entry starts from the primary `dependencies:` entry's values and merges its own on top. That
inheritance is why a minimal entry works:

```yaml
interaction:
  bundle:
    command: bundle       # inherits image, workdir, env, … from dependencies.app
```

## The three types

### `type: exec` (default)

Runs inside the already-running primary container:

```
wslc exec [-it] [-w WORKDIR] [-u USER] [-e KEY=VALUE …] CONTAINER command…
```

Ports and volumes are **not** applied — the container already exists. Requires the container to be
up (`wip up -d` first).

### `type: run`

Runs in a fresh, ephemeral container:

```yaml
migrate:
  type: run
  command: bundle exec rails db:migrate
  image: slidict/slidict:development
  remove: true
```

```
wslc run [--rm] [-it] [-w] [-u] [-e …] [-p …] [-v …] IMAGE command…
```

Unlike `exec`, this *does* apply ports and volumes, and needs an `image` (inherited from the
primary entry unless overridden).

Not supported under [`mode: compose`](Compose-Mode).

### `type: build`

Builds an image:

```yaml
build:
  type: build
  context: .
  tag: slidict/slidict:development
  shadow_context: /mnt/c/Users/me/AppData/Local/wip/build-contexts
```

```
wslc build -t TAG [extra args…] CONTEXT
```

An entry named `build` defaults to `type: build` even without the key. `tag` falls back to the
inherited `image`; an empty one is a `ConfigError`. Arguments you pass on the command line become
extra `wslc build` flags. See [wip build](wip-build), [Dockerignore](Dockerignore), and
[Shadow Build Context](Shadow-Build-Context).

Not supported under [`mode: compose`](Compose-Mode).

Any other `type` value:

```
Invalid command type for migrate: exectue
```

## Passing arguments

Extra CLI arguments are appended to the configured command:

```yaml
interaction:
  rails:
    command: bin/rails
```

```bash
wip rails console          # → bin/rails console
wip rails db:migrate       # → bin/rails db:migrate
wip rails g model User     # → bin/rails g model User
```

## Name collisions with built-ins

Built-in commands always win. If you declare an interaction named `sync`, `build`, `up`, `config`,
… then `wip sync` runs the built-in. wip says so where it can:

```console
$ wip sync
wip: commands.sync in wip.yml is shadowed by the built-in `wip sync`; run it with `wip dispatch sync`
```

Run yours explicitly:

```bash
wip dispatch sync
```

See [wip dispatch](wip-dispatch). Reserved names to avoid: `init`, `version`, `doctor`, `config`,
`build`, `up`, `stop`, `down`, `exec`, `run`, `shell`, `logs`, `sync`, `dispatch`, `help`.

Note `build` is a special case: it's both a built-in *and* the conventional name for the build
definition — `wip build` uses `interaction.build`'s `context`/`tag`, so that collision is by design.

## Unknown names

```console
$ wip nope
Unknown command: nope
```

Any first argument wip doesn't recognize as a built-in is routed to `dispatch`, which raises this
if there's no matching entry.

## Under compose mode

Only `type: exec` is supported:

```
commands.migrate: type 'run' is not supported in compose mode (use `wslc-compose build`/`up --build` directly)
```

The command runs via the bridge's `exec` against `compose.service`.

## Related

- [wip dispatch](wip-dispatch)
- [TTY Allocation](TTY-Allocation)
- [Env Files](Env-Files)
- [Migrating from dip](Migrating-from-dip)
