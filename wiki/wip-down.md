# wip down

Stops **and removes** the primary container and every sidecar.

```
wip down
```

## Behavior

| Mode | What runs |
|---|---|
| `container` | `wslc remove -f <container>`, then `wslc remove -f <name>` for each sidecar |
| `compose-native` | the same, across every service from `compose.yml` |
| `compose` | `<compose command> -f FILE [-p PROJECT] down` |

`-f` forces removal of a still-running container, so there's no need to `wip stop` first.

Failures are non-fatal — wip works through the whole list rather than aborting on the first
container that doesn't exist.

## What survives

| | Removed by `wip down`? |
|---|---|
| Containers | **yes** |
| Anything written inside a container's filesystem | **yes** (gone with the container) |
| The `network:` | no |
| Named volumes, including the sync volume | no |
| Images | no |

### Why the network is left in place

Networks are cheap and shared, and may be referenced by containers wip doesn't manage. Removing one
as a side effect of `wip down` would be surprising. Remove it yourself when you mean to:

```bash
wslc network remove app-tier
```

### Why the sync volume is left in place

The mirror is expensive to rebuild and holds no state you'd miss deleting deliberately. `wip up`
re-mirrors into it anyway. To start truly clean:

```bash
wip down
wslc volume remove app-src     # name from `wip config`
wip up -d
```

## When to use it

- After changing `ports:`, `volumes:`, `env:`, `command:`, or `image:` — those apply at container
  **creation**, so an existing container won't pick them up.
- When a container is in a state you don't want to reason about.
- Before switching branches with meaningfully different container config.

For an ordinary "done for now", [`wip stop`](wip-stop) is cheaper: it keeps the containers so the
next `wip up` is a `start` rather than a `run`.

## Interaction with `--watch`

Stop any [`wip up --watch`](Restart-Policies) loop first. Removed containers report state `deleted`
(`4`), not `exited` (`3`), so the loop won't recreate them — but a container that's mid-shutdown
can be caught in `exited` and restarted out from under you.

## Related

- [wip stop](wip-stop)
- [wip up](wip-up)
- [Networking](Networking)
