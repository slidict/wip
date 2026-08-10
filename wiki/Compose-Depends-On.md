# Compose Depends On

`depends_on:` controls **start ordering** under [`mode: compose-native`](Compose-Native-Mode).
That's all it controls — there is no health-check support, so it cannot mean "wait until ready".

## Accepted forms

Array form:

```yaml
services:
  app:
    image: myapp:dev
    depends_on:
      - db
      - redis
```

Mapping form, with an explicit condition:

```yaml
services:
  app:
    image: myapp:dev
    depends_on:
      db:
        condition: service_started
```

Anything else (a string, a number) is an error:

```
compose.yml: services.app.depends_on must be an array or a mapping
```

## Supported conditions

| Condition | Support |
|---|---|
| `service_started` | ✔ (and the default when no condition is given) |
| `service_healthy` | ✘ |
| `service_completed_successfully` | ✘ |

```
compose.yml: services.app.depends_on.db: condition 'service_healthy' is not supported
(only service_started — no health checks)
```

Why: `healthcheck:` is not in the [supported subset](Compose-File-Support), so there is no health
state for wip to wait on. If your stack genuinely needs readiness gating, either use
[`mode: compose`](Compose-Mode) with a tool that implements it, or handle it in the app (retry the
DB connection on boot — which most frameworks do already, and which is more robust anyway).

## What ordering actually means

Services are sorted into a topological order and started in that order. "Started" means the
container process has been launched — not that the service inside it is accepting connections.

```yaml
services:
  app:
    depends_on: [db]
  db:
    image: postgres:16
```

```console
$ wip up -d
wip: dependency 'db' not found, creating it
wip: container 'app' not found, creating it
```

The primary service (`compose.service`) is always started **last**, after every sidecar, regardless
of what `depends_on` says — same as [Container Mode](Container-Mode)'s primary/sidecar split. See
[Dependencies](Dependencies).

The same order is used when building `build:` services at the start of `wip up` — see
[Compose Build](Compose-Build).

## Validation

**Unknown target:**

```
compose.yml: services.app depends_on unknown service 'databse'
```

**Cycles** are detected and rejected rather than looping forever:

```
compose.yml: services.app is part of a depends_on cycle
```

**Depending on a profile-gated service** is rejected, because wip has no way to activate a profile,
so that dependency would silently never start:

```
compose.yml: services.app depends_on 'debug-tools', gated behind profiles: (debug) wip never
activates (no --profile flag)
```

A profile-gated service is allowed to depend on another profile-gated service — neither is started
anyway. See [Compose Profiles](Compose-Profiles).

## Checking the resolved order

```bash
wip config
```

prints the services as `dependencies:` entries in dependency order, so you can read off exactly
what `wip up` will do.

## Related

- [Compose File Support](Compose-File-Support)
- [Compose Profiles](Compose-Profiles)
- [wip up](wip-up)
