# Container Mode

`mode: container` is the default. wip declares every container itself in `wip.yml` and drives
`wslc` directly — no `compose.yml`, no external binary.

Use it when your project has no `compose.yml` (or when you'd rather not have one). If you already
have a compose file, see [Compose Native Mode](Compose-Native-Mode) or
[Compose Mode](Compose-Mode).

## Minimal config

```yaml
version: 1
mode: container
container: app
dependencies:
  app:
    image: your/image:tag
    workdir: /app
```

## Full example

```yaml
version: 1
mode: container            # default; may be omitted
wslc:
  command: auto            # wslc.exe → wslc → /mnt/c/Windows/System32/wslc.exe
container: app             # required once dependencies: has entries
network: app-tier          # optional; shared by every dependencies: entry
dependencies:
  app:                     # the primary container — container: points here
    image: slidict/slidict:development
    workdir: /app
    interactive: false
    remove: true
    command: server        # extra args appended when `wip up` creates it
    env:
      RAILS_ENV: development
      PORT: "3000"
    ports:
      - "3000:3000"
    volumes:
      - ".:/app"
  redis:
    image: redis:latest
  development.mysql:
    image: mysql:8.0
    command: --default-authentication-plugin=mysql_native_password
    restart: unless-stopped
    env:
      MYSQL_ROOT_PASSWORD: password
      MYSQL_DATABASE: development
interaction:
  rails:
    type: exec
    command: bin/rails
    interactive: true
  rspec:
    command: bundle exec rspec
  migrate:
    type: run
    command: bundle exec rails db:migrate
    remove: true
  build:
    type: build
    context: .
    tag: slidict/slidict:development
sync:
  exclude:
    - .git
    - tmp/
    - node_modules/
```

## Keys available in this mode

| Key | Page |
|---|---|
| `version:`, `mode:`, `wslc:` | [Config File Discovery](Config-File-Discovery) |
| `container:`, `dependencies:` | [Dependencies](Dependencies) |
| `network:` | [Networking](Networking) |
| `interaction:` / `commands:` | [Interactions](Interactions) |
| `dependencies.*.restart:` | [Restart Policies](Restart-Policies) |
| `sync:` | [Source Sync](Source-Sync) |

`compose:` is **not** available here — a `compose:` block requires `mode: compose` or
`mode: compose-native`.

## What each command does in this mode

| Command | Behavior |
|---|---|
| [`wip up`](wip-up) | create network → start sidecars → mirror source → start primary |
| [`wip stop`](wip-stop) | `wslc stop` on the primary, then each sidecar |
| [`wip down`](wip-down) | `wslc remove -f` on the primary, then each sidecar; network is left in place |
| [`wip exec`](wip-exec) | `wslc exec` into the primary container |
| [`wip run`](wip-run) | real `wslc run --rm` — a fresh, ephemeral container |
| [`wip build`](wip-build) | builds from the `build` interaction's `context`/`tag` |
| [`wip logs`](wip-logs) | **not available** (compose modes only) |
| [`wip sync`](wip-sync) | defaults to `sync.mode: exec` |
| [`wip up --watch`](Restart-Policies) | available |

## Primary vs. sidecar

`dependencies:` holds every container uniformly — there is no separate, differently-shaped block
for "the one you exec into." What sets the primary entry apart is purely operational:

- `wip up` starts every **other** entry first (by name), then the primary one, so the app can
  resolve `redis` / `development.mysql` by their dependency names.
- `exec`, `run`, `build`, `shell`, and interactions only ever target the primary entry.
- Sidecars are only started and stopped.

Details and the per-key reference: [Dependencies](Dependencies).

## Generating this shape

```bash
wip init            # writes mode: container when no compose.yml is found
wip init --template rails
```

See [wip init](wip-init).
