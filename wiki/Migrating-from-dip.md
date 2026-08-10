# Migrating from dip

`wip` is deliberately shaped like [`dip`](https://github.com/bibendi/dip), so most of a `dip.yml`
carries over with renames rather than rewrites.

## The short version

1. `cp dip.yml wip.yml`
2. Rename `docker_compose`-oriented keys to wip's `mode:` + `dependencies:` / `compose:` shape.
3. Leave `interaction:` alone — it's the same key, with the same meaning.
4. `wip doctor`, then `wip up -d`.

## What carries over unchanged

### `interaction:`

This is the headline: no rename needed.

```yaml
# dip.yml                          # wip.yml
interaction:                       interaction:
  rails:                             rails:
    service: app                       command: bin/rails
    command: bundle exec rails         interactive: true
```

`interaction:` is wip's **primary** spelling, same as in dip. `commands:` is accepted as an alias
if you prefer that name — but declaring both in one file is a `ConfigError`. See
[Interactions](Interactions).

Per-entry, the mapping is:

| dip | wip |
|---|---|
| `command:` | `command:` |
| `service:` | `container:` (only needed to override the primary one) |
| `environment:` | `env:` |
| `compose.run_options` | — (use `type: run` plus the entry's own keys) |
| `subcommands:` | — (declare separate top-level entries) |

### `.env`

Loaded the same way, with the same syntax. Precedence differs slightly: in wip, `env:` in `wip.yml`
always wins over `.env`. See [Env Files](Env-Files).

## What changes

### There's no `docker_compose:` key

dip assumes Compose. wip makes orchestration explicit through `mode:`:

| Your dip setup | wip equivalent |
|---|---|
| `dip.yml` + `docker-compose.yml` | [`mode: compose-native`](Compose-Native-Mode) — wip parses it directly |
| `dip.yml` + `docker-compose.yml`, and you want a real compose tool | [`mode: compose`](Compose-Mode) |
| No compose file; dip driving a single container | [`mode: container`](Container-Mode) |

### Services become `dependencies:`

Under `mode: container`, what Compose called services you declare directly:

```yaml
container: app
dependencies:
  app:
    image: myapp:dev
    workdir: /app
  postgres:
    image: postgres:16
    env:
      POSTGRES_PASSWORD: password
```

`container:` names the primary one — the target of `exec`/`run`/`build` and every interaction. It
has **no default**, unlike dip's convention of assuming a `app`-ish service. See
[Dependencies](Dependencies).

### `provision:` has no equivalent yet

dip's `provision:` (a list of setup commands run by `dip provision`) is not implemented. Express it
as an interaction and run it yourself:

```yaml
interaction:
  setup:
    type: run
    command: bin/setup
```

```bash
wip setup
```

### Docker → WSLC

Every command ends in `wslc`, not `docker`. Practical consequences:

- Images must exist for your architecture — see [Multi Arch Images](Multi-Arch-Images).
- Registry login is `wslc registry login`, not `docker login` — see
  [Registry Authentication](Registry-Authentication).
- Bind mounts cross a VM boundary and are slow enough to matter — see
  [Fixing a Slow Boot](Fixing-a-Slow-Boot). This is the biggest practical difference from dip on
  Docker Desktop, and the reason [`sync:`](Source-Sync) exists.
- There's no `restart:` policy in the runtime; wip approximates it by polling — see
  [Restart Policies](Restart-Policies).

## Worked example

**Before — `dip.yml` + `docker-compose.yml`:**

```yaml
# dip.yml
version: '7'
compose:
  files:
    - docker-compose.yml
interaction:
  bash:
    service: app
    command: bash
  rails:
    service: app
    command: bundle exec rails
  rspec:
    service: app
    command: bundle exec rspec
```

**After — `wip.yml`, reusing the same compose file:**

```yaml
version: 1
mode: compose-native

compose:
  service: app
  project: myapp

interaction:
  shell:
    command: bash
    interactive: true
  rails:
    command: bundle exec rails
    interactive: true
  rspec:
    command: bundle exec rspec
```

Then:

```bash
wip doctor      # confirms the compose file parses within the supported subset
wip up -d
wip rspec
```

If `wip doctor` rejects a key, check [Compose File Support](Compose-File-Support) — and if your
file needs something outside the subset, switch to [`mode: compose`](Compose-Mode).

## Feature comparison

A side-by-side table lives on [wip vs dip](wip-vs-dip).

## Related

- [Interactions](Interactions)
- [Reusing an Existing compose.yml](Reusing-an-Existing-compose-yml)
- [wip vs dip](wip-vs-dip)
