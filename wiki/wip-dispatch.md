# wip dispatch

Runs a `wip.yml` [interaction](Interactions) explicitly, by name.

```
wip dispatch NAME [ARGS...]
```

## You usually don't need it

`dispatch` is the default command: any first argument wip doesn't recognize as a built-in is routed
here automatically. These are identical:

```bash
wip rspec --fail-fast
wip dispatch rspec --fail-fast
```

So you only reach for `dispatch` when the automatic routing can't reach your entry — which happens
in exactly one case.

## Resolving a name collision

Built-in commands always win. If you declare an interaction whose name matches a built-in, `wip
<name>` runs the **built-in**:

```yaml
interaction:
  sync:
    command: bin/sync-fixtures
  build:
    type: build
    context: .
    tag: myapp:dev
```

```bash
wip sync            # → the built-in mirror, not bin/sync-fixtures
wip dispatch sync   # → bin/sync-fixtures
```

Where it can, wip warns rather than silently shadowing:

```console
$ wip sync
wip: commands.sync in wip.yml is shadowed by the built-in `wip sync`; run it with `wip dispatch sync`
```

### Reserved names

`init`, `version`, `doctor`, `config`, `build`, `up`, `stop`, `down`, `exec`, `run`, `shell`,
`logs`, `sync`, `dispatch`, `help`.

Prefer renaming your entry (`sync-fixtures` rather than `sync`) — `dispatch` is the escape hatch,
not the intended daily spelling.

`build` is the deliberate exception: [`wip build`](wip-build) *is* driven by the `build`
interaction's `context`/`tag`, so that collision is by design rather than a shadowing problem.

## With no arguments

```console
$ wip dispatch
```

prints wip's help, the same as `wip help`.

## Unknown names

```console
$ wip dispatch nope
Unknown command: nope
```

## Behavior

The entry is resolved from `interaction:` / `commands:`, merged on top of the primary container's
values, and run according to its `type`:

| `type` | Runs as |
|---|---|
| `exec` (default) | [`wip exec`](wip-exec) into the primary container |
| `run` | [`wip run`](wip-run) in a fresh `--rm` container |
| `build` | [`wip build`](wip-build) |

Extra arguments are appended to the configured `command`. TTY allocation follows the entry's
`interactive:` value combined with real-TTY detection — see [TTY Allocation](TTY-Allocation).

Under [`mode: compose`](Compose-Mode), only `type: exec` is supported:

```
commands.migrate: type 'run' is not supported in compose mode (use `wslc-compose build`/`up --build` directly)
```

## Global options work here too

```bash
wip --config other/wip.yml dispatch rspec
wip dispatch rspec --debug
```

See [Global Options](Global-Options).

## Related

- [Interactions](Interactions)
- [CLI Command Reference](CLI-Command-Reference)
