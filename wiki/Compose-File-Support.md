# Compose File Support

What [`mode: compose-native`](Compose-Native-Mode) understands when it parses your `compose.yml`.

The subset is deliberately small — it exists to close the gap until `wslc` ships Compose support
of its own ([microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)) — but it's
actively extended, not a frozen ceiling. If your file needs more than this, use
[`mode: compose`](Compose-Mode) and let a dedicated tool handle it.

## Which file is read

`compose.file` if set (resolved relative to `wip.yml`), otherwise the first of these found next to
`wip.yml`:

```
compose.yml → compose.yaml → docker-compose.yml → docker-compose.yaml
```

Parsed with YAML **aliases enabled**, so merge keys and anchors work:

```yaml
x-common: &common
  image: myapp:dev
  working_dir: /app

services:
  app:
    <<: *common
  worker:
    <<: *common
    command: bundle exec sidekiq
```

(`wip.yml` itself is parsed with aliases *disabled* — see
[Config File Discovery](Config-File-Discovery).)

`${VAR}` references are substituted before anything else — see
[Compose Variable Interpolation](Compose-Variable-Interpolation).

## Supported per-service keys

| Key | Support |
|---|---|
| `image` | full |
| `build` | string, or `{context, dockerfile, args, shadow_context}` — see [Compose Build](Compose-Build) |
| `command` | shell form (`"bin/rails s"`) or exec form (`["bin/rails", "s"]`) |
| `environment` | mapping, or `KEY=VALUE` array. No host pass-through (a null value / bare `KEY` is an error) |
| `ports` | **short syntax only** (`"3000:3000"`) |
| `volumes` | **short syntax only** (`".:/app"`, `"app-src:/app"`) |
| `working_dir` | full → `-w` |
| `user` | full → `-u` |
| `restart` | stored as-is; acted on by [`wip up --watch`](Restart-Policies) |
| `depends_on` | ordering only — see [Compose Depends On](Compose-Depends-On) |
| `profiles` | parsed; profile-gated services are skipped — see [Compose Profiles](Compose-Profiles) |

Either `image` or `build` is required per service:

```
services.app must set image or build
```

## Accepted but ignored

| Key | Why it's ignored |
|---|---|
| `tty` | TTY allocation is decided per invocation, not per service — see [TTY Allocation](TTY-Allocation) |
| `stdin_open` | same |
| `networks` | every service already shares the one project network — see [Networking](Networking) |
| `cap_add` | `wslc run`/`exec` has no capability flag to forward it to |

These don't fail; they simply have no effect.

## Ignored top-level sections

Everything outside `services:` — `networks:`, `volumes:`, `configs:`, `secrets:`, `x-*` extensions,
and so on — is read by real Compose tools, not by wip, so it's silently ignored rather than
rejected. This is the one place wip deliberately doesn't complain about what it can't handle:
failing here would reject an otherwise perfectly valid `compose.yml` over sections wip never needs
to look at.

`services:` itself must be a mapping:

```
compose.yml: services: must be a mapping
```

## Anything else in a service is an error

Inside `services.<name>:`, an unrecognized key fails at load time and names itself:

```
compose.yml: services.app has unsupported key(s): healthcheck, deploy
```

That's intentional: silently dropping `healthcheck:` would mean the file says one thing and wip
does another.

Commonly-hit unsupported keys, and what to do:

| Key | Alternative |
|---|---|
| `healthcheck` | none — `depends_on` conditions other than `service_started` are rejected; use `mode: compose` |
| `deploy` (incl. `replicas`) | no scaling support; use `mode: compose` |
| `env_file` | move values into `.env` next to `wip.yml` ([Env Files](Env-Files)) or `environment:` |
| `extends` | inline it, or use YAML anchors (aliases are enabled) |
| `entrypoint` | fold it into `command:`, or bake it into the image |
| `labels` | no equivalent |

Long-syntax `ports:` / `volumes:` fail with a specific hint:

```
compose.yml: services.app.volumes only supports short syntax ("host:container"), not long-syntax mappings
```

## `environment` rules

```yaml
environment:
  RAILS_ENV: development     # ok
  PORT: 3000                 # ok — stringified
```

```yaml
environment:
  - RAILS_ENV=development    # ok
  - PORT=3000                # ok
```

Not supported:

```yaml
environment:
  RAILS_ENV:                 # null → error
  - RAILS_ENV                # bare KEY → error
```

```
compose.yml: services.app.environment.RAILS_ENV must have a value (host environment pass-through is not supported)
compose.yml: services.app.environment entries must be KEY=VALUE
```

Pass the value through `.env` instead — see [Env Files](Env-Files).

## Validating your file

```bash
wip doctor
```

reports whether the compose file was found, whether it parsed, and whether `compose.service` names
a service it actually defines:

```console
[OK] Found compose file /home/me/app/compose.yml
[OK] Parsed compose file
```

Then check what wip made of it:

```bash
wip config
```

which prints each service as a resolved `dependencies:` entry.

## Related

- [Compose Native Mode](Compose-Native-Mode)
- [Compose Build](Compose-Build)
- [Compose Depends On](Compose-Depends-On)
- [Compose Profiles](Compose-Profiles)
- [Compose Variable Interpolation](Compose-Variable-Interpolation)
- [Reusing an Existing compose.yml](Reusing-an-Existing-compose-yml)
