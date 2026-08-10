# wip logs

Follows container logs. **Compose modes only.**

```
wip logs [-f] [SERVICE...]
```

## Availability

| Mode | Available | Services per invocation |
|---|---|---|
| `container` | ✘ | — |
| `compose-native` | ✔ | at most **one** |
| `compose` | ✔ | as many as your compose tool accepts |

```console
$ wip logs        # under mode: container
`wip logs` is only available in compose mode
```

Under `mode: container`, use `wslc logs` directly:

```bash
wslc logs -f app
```

## Compose-native: one service at a time

`wslc logs`, like `docker logs`, follows a single container — there's no multi-service aggregation
the way a real compose tool provides. So:

```bash
wip logs              # follows compose.service
wip logs worker       # follows the "worker" service
wip logs app worker   # error
```

```
`wip logs` under mode: compose-native takes at most one SERVICE (wslc logs, unlike a real
compose tool, only follows one container at a time)
```

Resulting invocation:

```
wslc logs [-f] <service>
```

To watch several at once, run one `wip logs` per terminal — or use [`mode: compose`](Compose-Mode),
where the external tool aggregates.

## Compose mode: full pass-through

```
<compose command> -f FILE [-p PROJECT] logs [-f] [SERVICE…]
```

Every service name you pass is forwarded, so multi-service following works exactly as your compose
tool implements it.

## Flags

### `-f` / `--follow`

**On by default.** `wip logs` follows; pass `--no-follow` to print what's there and exit:

```bash
wip logs --no-follow
wip logs --no-follow worker
```

This is the opposite of `docker logs`, where following is opt-in — worth remembering if you script
it, since the default will otherwise block forever.

## Stopping

`Ctrl-C`. wip exits `130`.

## Related

- [Compose Native Mode](Compose-Native-Mode)
- [Compose Mode](Compose-Mode)
- [Debug Output](Debug-Output) — for wip's own timing, rather than container output
