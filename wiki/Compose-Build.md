# Compose Build

How [`mode: compose-native`](Compose-Native-Mode) handles services that declare `build:` instead of
(or alongside) `image:`.

## Accepted forms

Short form — just a context:

```yaml
services:
  app:
    build: .
```

Long form:

```yaml
services:
  app:
    build:
      context: .
      dockerfile: Dockerfile.dev
      args:
        RUBY_VERSION: "3.4"
      shadow_context: /mnt/c/Users/me/AppData/Local/wip/build-contexts
```

Only those four keys are supported. Anything else:

```
compose.yml: services.app.build has unsupported key(s): target, cache_from
```

`build:` must be a string or a mapping — a list is an error.

## Path resolution

- **`context`** is resolved relative to `compose.yml`'s own directory (Compose's rule), **not**
  wherever `wip` was invoked from. It defaults to `.`.
- **`dockerfile`** stays relative to `context`, and is passed through as `-f`. It is deliberately
  *not* resolved against the context, so `-f` still finds it after wip changes into a staged or
  shadowed copy of that context.

## Tagging

| Service declares | Resulting tag |
|---|---|
| `build:` only | `wip-compose-<service>:latest` (auto-generated) |
| `build:` **and** `image:` | the `image:` value |
| `image:` only | the `image:` value; nothing is built |

The `build:` + `image:` combination follows real Compose's own rule: build via the former, tag the
result with the latter.

```yaml
services:
  app:
    build: .
    image: myapp:dev     # → wslc build -t myapp:dev .
```

## When images are built

At the start of `wip up`, **before** the network is created and any container is started:

```console
$ wip up -d
wip: building service 'app' (tag: myapp:dev) from /home/me/app
wip: staging build context (/home/me/app)
wip: copying build context files: 812/812
```

Every service with a `build:` is built, once per `wip up` invocation, in `depends_on` order.
Profile-gated services are skipped entirely — see [Compose Profiles](Compose-Profiles).

`wip up --no-cache` adds `--no-cache` to each of those builds.

There is no separate "build only these services" command; `wip build` runs the `build` *interaction*
from `wip.yml`, which is a different thing — see [wip build](wip-build).

## Build args

```yaml
build:
  args:
    RUBY_VERSION: "3.4"
    BUNDLER_VERSION: "2.5.6"
```

Each becomes `--build-arg KEY=VALUE`. Both mapping and `KEY=VALUE` array forms are accepted, with
the same rules as `environment:`: a null value or a bare key is an error, since host pass-through
isn't supported.

## Context staging

Each service's build context goes through the same staging path `wip build` uses:

- its own `.dockerignore`, read from the context root — see [Dockerignore](Dockerignore)
- an optional persistent Windows-side shadow copy via `shadow_context:` — see
  [Shadow Build Context](Shadow-Build-Context)
- progress reporting while files are copied

The build then runs from **inside** the staged directory with `.` as the context, because
`wslc build` crashes when handed an absolute context path.

## Full example

```yaml
# compose.yml
services:
  app:
    build:
      context: .
      dockerfile: Dockerfile.dev
      args:
        RUBY_VERSION: "3.4"
      shadow_context: /mnt/c/Users/me/AppData/Local/wip/build-contexts
    image: myapp:dev
    working_dir: /app
    ports:
      - "3000:3000"
    depends_on:
      - db
  db:
    image: postgres:16
    environment:
      POSTGRES_PASSWORD: password
```

```yaml
# wip.yml
version: 1
mode: compose-native
compose:
  service: app
  project: myapp
```

```bash
wip up -d      # builds myapp:dev, creates network myapp, starts db, then app
```

## Related

- [Compose File Support](Compose-File-Support)
- [Dockerignore](Dockerignore)
- [Shadow Build Context](Shadow-Build-Context)
- [wip up](wip-up)
