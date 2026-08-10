# Dependencies

`dependencies:` declares every container wip manages under [Container Mode](Container-Mode) — the
app itself and every sidecar, in one uniformly-shaped block. `container:` names which one is
primary.

Under [Compose Native Mode](Compose-Native-Mode) this block is synthesized from `compose.yml`'s
`services:` instead, and `compose.service` plays the role of `container:` — everything below about
primary vs. sidecar behavior still applies.

## Shape

```yaml
container: app
dependencies:
  app:
    image: slidict/slidict:development
    workdir: /app
    user: "1000:1000"
    interactive: false
    remove: true
    command: server
    restart: "no"
    env:
      RAILS_ENV: development
    ports:
      - "3000:3000"
    volumes:
      - ".:/app"
  redis:
    image: redis:latest
```

## Entry keys

| Key | Required | Default | Maps to | Notes |
|---|---|---|---|---|
| `image` | **yes** | — | the image argument | empty/missing is a `ConfigError` |
| `command` | no | *(image default)* | trailing args | split with shell-word rules |
| `workdir` | no | `nil` | `-w` | |
| `user` | no | `nil` | `-u` | e.g. `"1000:1000"` |
| `env` | no | `{}` | `-e KEY=VALUE` | values stringified; wins over `.env` |
| `ports` | no | `[]` | `-p` | not applied on `exec` |
| `volumes` | no | `[]` | `-v` | rewritten when `sync:` is set |
| `restart` | no | `"no"` | polled by `--watch` | see [Restart Policies](Restart-Policies) |
| `interactive` | no | `false` | `-it` | combined with real-TTY detection |
| `remove` | no | `true` | `--rm` on `wip run` | |

`interactive` and `remove` only affect the primary entry's `run`/`exec` paths; sidecars are always
started detached.

## `container:` has no default

Once `dependencies:` has any entries, `container:` must be set:

```
container: must be set when dependencies: has entries
```

There used to be an implicit `app` default. Guessing a name either matches by luck or fails later
in a way that points at the wrong thing (`No dependencies.app entry`) instead of at the real
problem — a differently-named entry. So it's explicit.

If `container:` names an entry that doesn't exist, `wip doctor` reports:

```
[FAIL] No dependencies.app entry
```

and any command that needs the primary container fails with:

```
No dependencies.app entry (check container: in wip.yml)
```

## Primary vs. sidecar

The distinction is operational, not structural — same keys, same shape.

| | primary | sidecar |
|---|---|---|
| `wip up` | started **last** | started **first**, by name |
| `wip stop` / `wip down` | yes | yes |
| `wip exec` / `wip shell` | yes | no |
| `wip run` | yes | no |
| `wip build` | yes | no |
| `interaction:` entries | yes | no |
| `wip up --watch` restarts it | yes | yes |

This mirrors Compose's own split: services are things you bring up; one of them is the thing you
exec into.

### Why sidecars start first

So the app can reach them. `wip up` creates `network:` if it's set and missing, starts each
sidecar by its dependency name, then boots the primary container — at which point `bin/rails c`
can connect to `development.mysql` or `redis` by name, the way Compose service names resolve. See
[Networking](Networking).

## Startup semantics

For each sidecar, `wip up` first probes whether it exists:

```
wip: starting existing dependency 'redis'      # found → wslc start redis
wip: dependency 'redis' not found, creating it # not found → wslc run --name redis …
```

The same existence check runs for the primary container. Re-running `wip up` against a running
stack is safe: starting an already-running container is a no-op.

`wip down` removes the primary container and all sidecars. **The network itself is left in place.**

## `restart: no` and YAML

An unquoted `restart: no` parses as the boolean `false` in YAML, not the string `"no"`. wip
normalizes `false`, `nil`, and `""` to `"no"`, so both spellings behave identically. Quoting it
(`restart: "no"`) is still clearer. See [Restart Policies](Restart-Policies).

## Interaction with `sync:`

When `sync:` is configured, any `volumes` entry on the **primary** container that mounts the sync
target (the usual `.:/app`) is replaced by the read-only source mount plus the named volume. Other
volumes pass through untouched, and sidecar volumes are never rewritten. See
[Source Sync](Source-Sync).

## Related

- [Networking](Networking)
- [Restart Policies](Restart-Policies)
- [Interactions](Interactions)
- [wip up](wip-up)
