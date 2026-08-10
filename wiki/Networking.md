# Networking

How containers reach each other by name.

## Container mode: `network:`

```yaml
network: app-tier
dependencies:
  app:   { image: your/image:tag }
  redis: { image: redis:latest }
```

Every entry under `dependencies:` joins this one network, so the app can connect to `redis://redis:6379`
using the dependency name as the hostname — the same way Compose service names resolve.

`network:` is optional. Leave it out and containers are created without `--network`, which means
they get whatever default `wslc` gives them and generally **cannot** resolve each other by name.
If your app talks to a sidecar, set it.

### When the network is created

At `wip up`, and only if it doesn't already exist:

```console
$ wip up -d
wip: creating network 'app-tier'
```

wip lists existing networks first and matches on exact name. Creation failure is non-fatal — the
boot continues, and the subsequent `run` will surface the real error.

### When it is *not* removed

`wip down` stops and removes containers, but leaves the network in place. Networks are cheap,
shared, and may be referenced by containers wip doesn't manage; tearing one down as a side effect
of `wip down` would be surprising. Remove it yourself if you need to:

```bash
wslc network remove app-tier
```

## Compose-native mode: derived from `compose.project`

There is no `network:` key here — setting one alongside `compose:` is a `ConfigError`. Instead wip
creates one project network for every service, named:

1. `compose.project`, if set
2. otherwise the basename of the directory holding `wip.yml`

```yaml
compose:
  service: app
  project: myapp     # → network "myapp"
```

This gives the same guarantee real Compose's per-project network does: every service in the file
can reach every other by service name.

`networks:` inside a compose service is accepted and **ignored** — every service already shares
the one project network. Top-level `networks:` is ignored outright. See
[Compose File Support](Compose-File-Support).

## Compose mode: not wip's concern

Under [`mode: compose`](Compose-Mode), the external compose binary owns networking entirely. wip
never passes `--network`, and `network:` in `wip.yml` is rejected as mutually exclusive with
`compose:`.

## Ports

Publishing is per-entry, not per-network:

```yaml
dependencies:
  app:
    ports:
      - "3000:3000"
```

Ports are applied when a container is **created** (`wip up`, `wip run`) — not on `wip exec`, which
attaches to an already-running container and therefore never re-publishes anything. If you added a
port to `wip.yml` and it isn't listening, the container predates the change: `wip down && wip up -d`.

## Related

- [Dependencies](Dependencies)
- [Compose Native Mode](Compose-Native-Mode)
- [wip up](wip-up)
