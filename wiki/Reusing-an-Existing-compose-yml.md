# Reusing an Existing compose.yml

You have a working `compose.yml` and don't want to duplicate it into `wip.yml`. There are two ways
to reuse it, and the choice is really about whether you want to install an external binary.

## The two options

| | [`mode: compose-native`](Compose-Native-Mode) | [`mode: compose`](Compose-Mode) |
|---|---|---|
| External binary | none | required (`compose.command`) |
| Compose coverage | a [documented subset](Compose-File-Support) | whatever your tool supports |
| `wip run` | real `wslc run --rm` | falls back to `exec` |
| `wip logs` | one service | multi-service |
| `wip up --watch` | yes | no |
| `type: run` / `type: build` interactions | yes | no |
| `sync:` | works like container mode | needs `sync.image`/`sync.build`, `mode: run` only |

**Start with `compose-native`.** Fall back to `compose` only when your file needs something outside
the subset.

## Try compose-native first

```yaml
# wip.yml
version: 1
mode: compose-native

compose:
  service: app       # which service is "the app"
  project: myapp     # optional; also names the network
```

```bash
wip doctor
```

`wip doctor` parses the file and reports the first thing it can't handle:

```console
[OK]   Found compose file /home/me/app/compose.yml
[FAIL] compose.yml: services.app has unsupported key(s): healthcheck
```

If it parses, you're done:

```console
[OK] Parsed compose file
```

Then confirm wip read it the way you expect:

```bash
wip config
```

## Common blockers and what to do

| Blocker | Options |
|---|---|
| `healthcheck:` + `depends_on: {condition: service_healthy}` | Drop the condition and let the app retry its DB connection on boot, or switch to `mode: compose` |
| `deploy:` / `replicas:` | No scaling support — switch to `mode: compose` |
| Long-syntax `volumes:` / `ports:` | Rewrite as short syntax (`"./src:/app"`, `"3000:3000"`) where possible |
| `env_file:` | Move the values into `.env` next to `wip.yml` ([Env Files](Env-Files)) or inline them in `environment:` |
| `extends:` | Inline it, or use YAML anchors — aliases are enabled in `compose.yml` |
| `entrypoint:` | Fold into `command:`, or bake it into the image |
| `profiles:` | Fine — gated services are skipped ([Compose Profiles](Compose-Profiles)) |
| `tty:` / `stdin_open:` / `networks:` / `cap_add:` | Fine — accepted and ignored |

Full list: [Compose File Support](Compose-File-Support).

## Falling back to `mode: compose`

Install a compose-for-`wslc` tool ([candidates](Third-Party-Compose-Tools)), then:

```yaml
version: 1
mode: compose

compose:
  service: app
  command: wslc-compose     # the binary you installed
  file: compose.yml         # optional
  project: myapp            # optional
```

```bash
wip doctor       # confirms the binary resolves and responds to `version`
```

Then accept the constraints on that page: exec-only interactions, `wip run` falling back to
`exec`, no `--watch`.

## Keeping both files honest

Whichever mode you pick, `compose.yml` stays the source of truth for services and `wip.yml` stays
the source of truth for *your commands*:

```yaml
# wip.yml
version: 1
mode: compose-native
compose:
  service: app

interaction:
  rails:
    command: bin/rails
    interactive: true
  rspec:
    command: bundle exec rspec
  console:
    command: bin/rails console
    interactive: true
```

Nothing about the services is duplicated. Other tooling (CI, a teammate on plain Docker) keeps
using `compose.yml` unchanged.

## Adding sync to a compose project

Under **compose-native**, sync behaves exactly like container mode — wip rewrites the primary
service's mounts for you:

```yaml
sync:
  exclude: [".git", "tmp/", "node_modules/"]
```

Under **`mode: compose`**, wip rewrites nothing. Your compose service must declare the volume
itself:

```yaml
# compose.yml
services:
  app:
    volumes:
      - app-src:/app
volumes:
  app-src:
```

```yaml
# wip.yml
sync:
  volume: app-src
  target: /app
  mode: run
  build:
    dockerfile: |
      FROM alpine:latest
      RUN apk add --no-cache rsync
```

See [Sync Modes](Sync-Modes).

## Related

- [Compose Native Mode](Compose-Native-Mode)
- [Compose Mode](Compose-Mode)
- [Compose File Support](Compose-File-Support)
- [wip vs docker compose](wip-vs-docker-compose)
