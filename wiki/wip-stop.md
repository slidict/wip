# wip stop

Stops the primary container and every sidecar, **without removing them**.

```
wip stop
```

## Behavior

| Mode | What runs |
|---|---|
| `container` | `wslc stop <container>`, then `wslc stop <name>` for each sidecar |
| `compose-native` | the same, across every service from `compose.yml` |
| `compose` | `<compose command> -f FILE [-p PROJECT] stop` |

Failures are non-fatal: wip continues through the rest of the list rather than aborting on the
first container that was already stopped or never existed. The command's own exit code is not
propagated, so `wip stop` on an already-stopped stack is a quiet no-op.

## `stop` vs. `down`

| | `wip stop` | [`wip down`](wip-down) |
|---|---|---|
| Container process | stopped | stopped |
| Container itself | **kept** | removed |
| Container filesystem changes | **kept** | lost |
| Named volumes | kept | kept |
| Network | kept | kept |
| Next `wip up` | `start` — fast | `run --name …` — recreates |

Use `stop` when you're done for the day and want to pick up where you left off. Use `down` when you
want a clean slate, or after changing `ports:` / `volumes:` / `env:` in `wip.yml`, since those only
take effect on creation.

## Interaction with `--watch`

If [`wip up --watch`](Restart-Policies) is running in another terminal, it may restart what you
just stopped: the loop checks whether a container is *currently* exited, not whether it exited on
its own. `Ctrl-C` the watch loop first.

## Profile-gated services

Under compose-native, services behind `profiles:` are never started by wip, so they're not stopped
either. See [Compose Profiles](Compose-Profiles).

## Related

- [wip down](wip-down)
- [wip up](wip-up)
- [Dependencies](Dependencies)
