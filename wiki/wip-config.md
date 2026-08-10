# wip config

Prints the **effective** configuration as YAML: every default filled in, every derived value
resolved, every secret-looking value masked.

```
wip config
```

This is the fastest way to answer "what did wip actually make of my `wip.yml`?" — especially under
[compose-native mode](Compose-Native-Mode), where most of the config comes from `compose.yml`.

## Example

```console
$ wip config
---
version: 1
wslc:
  command: auto
mode: container
container: app
network: app-tier
dependencies:
  app:
    image: slidict/slidict:development
    workdir: /app
    interactive: false
    remove: true
    env:
      RAILS_ENV: development
    ports:
    - '3000:3000'
    volumes:
    - .:/app
  redis:
    image: redis:latest
compose:
sync:
  source: "/home/me/my-project"
  target: "/app"
  mount: "/host-src"
  volume: app-src
  delete: true
  exclude:
  - ".git"
  - tmp/
  command: rsync
  options: []
  interval: 2
  mode: exec
  image:
  build:
commands:
  rspec:
    image: slidict/slidict:development
    workdir: /app
    type: exec
    command: bundle exec rspec
```

## What it shows that your file doesn't

| | |
|---|---|
| **Defaults** | `mode`, `wslc.command`, every `dependencies:` entry key, every `sync:` key |
| **Derived values** | `sync.source` / `target` / `volume`, compose-native's `container` and `network` |
| **Interaction inheritance** | each entry merged on top of the primary container's values |
| **compose.yml** | under compose-native, services rendered as `dependencies:` entries in dependency order |
| **Interpolated `${VAR}`** | post-substitution values — see [Compose Variable Interpolation](Compose-Variable-Interpolation) |

Two normalizations worth noting:

- **`interaction:` is always printed as `commands:`**, whichever spelling you wrote. They're
  aliases for one feature — see [Interactions](Interactions).
- **Every `env` value is a string.** `PORT: 3000` in your file appears as `PORT: '3000'`.

## Secret masking

Any key matching `token`, `password`, `secret`, `credential`, or `auth` (case-insensitive,
substring) has its value replaced:

```yaml
env:
  MYSQL_ROOT_PASSWORD: "[REDACTED]"
```

The match is on **key names only**, so a secret hidden inside a value (`DATABASE_URL`,
`API_KEY`) is printed in full. Read the output before pasting it anywhere — see
[Secret Masking](Secret-Masking).

## Uses

**Debug a config that "isn't taking effect":**

```bash
wip config | grep -A5 'sync:'
```

**Check what compose-native made of your compose file:**

```bash
wip config
```

If a service is missing, it's probably [profile-gated](Compose-Profiles). If a value looks empty,
a `${VAR}` didn't resolve.

**Diff two setups:**

```bash
wip config > /tmp/mine.yml
# on the machine where it works
wip config > /tmp/theirs.yml
diff /tmp/mine.yml /tmp/theirs.yml
```

**Confirm `.env` is being picked up** — note that `.env` values are merged in at command-build
time, not shown in `wip config`'s `env:` maps. To verify those, use `--debug` and read the `-e`
flags (masked as `KEY=***`), or run `wip exec env`.

## Failure

`wip config` loads and validates the config, so it fails exactly where every other command would:

```console
$ wip config
container: must be set when dependencies: has entries
```

See [Configuration Errors](Configuration-Errors).

## Related

- [Configuration Reference](Configuration-Reference)
- [Secret Masking](Secret-Masking)
- [wip doctor](wip-doctor)
