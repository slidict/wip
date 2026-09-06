# Upstream pin: `microsoft/WSL` `feature/compose`

| | |
|---|---|
| Branch | `feature/compose` |
| Commit | `df786af866dd3fdb9c4b7bfc2be03ee0f68c8e14` |
| Title | *wslc compose POC (#41378)* |
| Dated | 2026-08-26 |
| Read on | 2026-09-06 |

`UpstreamComposeInterpreter` in `tests/Wip.Tests/ComposeCompat/` is a reading of this commit,
not a copy of it: it answers "what would `wslc compose` make of this compose.yml?" in the same
shape wip's own answer is recorded in, so the two can be compared. Nothing under `src/`
references it, and no behaviour of wip is derived from it.

## Files read

| Upstream path | What it decides |
|---|---|
| `src/windows/wslcsession/ComposeSpec.cpp` | The whole compose.yml parser: supported keys, and how each one is read |
| `src/windows/wslcsession/ComposeSpec.h` | The parsed shape — what upstream is able to represent at all |
| `src/windows/wslcsession/WSLCSession.cpp` (`CreateComposeContainers`) | Network name, network alias, port binding, mount kind |
| `src/windows/wslc/services/ComposeService.cpp` | The `compose create` / `up` / `start` / `attach` / `stop` verbs |
| `src/windows/wslc/services/ContainerModel.cpp` (`PublishPort::Parse`) | How `wslc run -p` reads a port string — the parser wip's forwarded ports actually meet |
| `src/windows/common/MountSpecParsing.cpp` (`ParseDockerVolumeString`) | How `wslc run -v` reads a volume string — likewise for volumes |
| `test/windows/WSLCComposeTests.cpp` | Which behaviours upstream considers settled enough to assert |

## Rules extracted

Numbered so a re-read of a changed `feature/compose` can be walked against the same list.

1. **Supported keys.** `name`, `container_name`, `image`, `environment`, `working_dir`,
   `command`, `volumes`, `ports`. Any other key is a hard error naming the key.
2. **`image`** is required and must be a non-empty scalar. There is no `build:`.
3. **Container name** is `name:`, else `container_name:`, else the service name. `name:` is an
   upstream extension — the Compose Specification has no service-level `name:` — and it wins
   over `container_name:` when both are present.
4. **`command`.** A list is taken as written. A scalar is split on single spaces, dropping
   empty fields, with no quote handling: `-b "0.0.0.0"` yields `-b` and `"0.0.0.0"` including
   the quote characters. Upstream marks this `// TODO: Implement proper parsing`.
5. **`environment`.** A list is passed through as written, so a bare `KEY` stays a bare `KEY`
   with no `=`. A mapping becomes `KEY=VALUE`, and a null value becomes `KEY=` rather than the
   Specification's host pass-through.
6. **`working_dir`** must be a scalar and must start with `/`; a relative one is refused.
7. **`volumes`.** Short syntax only, and a list. A trailing `:ro`/`:rw` is stripped first, then
   the rightmost colon splits source from destination. The destination must start with `/`.
   The source is a *named volume* unless it looks like a path — absolute, or starting with
   `.`, or containing `/` or `\`. A relative bind source resolves against compose.yml's own
   directory.
8. **`ports`.** A list of `host:container` scalars, and nothing else: exactly one colon, digits
   only on both sides, no ranges, no `/udp`, no bind address, no container-only form. The host
   side may be `0` (ephemeral); the container side may not.
9. **Project name** is the compose file's stem — `compose` for `compose.yml`. Marked
   `// TODO: Implement this properly`.
10. **Network** is `<project>_default`, deleted and recreated on every session create.
11. **Network alias** is `AddPrimaryNetworkAlias(definition.Name)` — the *container* name, so
    `container_name:` renames the alias too.
12. **Port binding** is `AddPort(host, container, AF_INET)`, whose default binding address is
    the `127.0.0.1` literal, TCP only.
13. **Lifecycle.** `compose create` parses and creates; `start` deletes and recreates every
    container, then starts them in file order; `stop` sends SIGTERM to each; `attach` relays
    stdout/stderr only. There is no `down`, no `ps`, no `logs`, and no per-service `exec`.

## Behaviour marked provisional upstream

These are recorded so wip is not accidentally frozen against them. wip follows the Compose
Specification where it disagrees with any of them.

| Rule | Upstream marker |
|---|---|
| Scalar `command` split on spaces (4) | `// TODO: Implement proper parsing` |
| Project name from the file stem (9) | `// TODO: Implement this properly` |
| Network deleted rather than reused (10) | `// TODO: open an existing network instead of deleting.` |
| Compose session state not re-checked when reused | `// TODO: Check the state of the compose session before returning.` |
| `Attach()` on the session object | `// TODO`, returns `S_OK` without doing anything |
| Image pull output discarded | `// TODO: Wire the pull output to caller.` |
| No stdin, no TTY, no ctrl-c handling in `attach` | `// TODO: Add support for stdin, tty processes, stop on ctrl-c.` |

## Not modelled here

Nothing in upstream corresponds to `build`, `depends_on`, `healthcheck`, `restart`,
`profiles`, `.env` or `${VAR}` interpolation — its parser rejects every one of those keys.
They are tracked as `wip-only` in `../matrix.json` and are deliberately *not* a compatibility
failure. When upstream grows any of them, add the rule to the list above, teach
`UpstreamComposeInterpreter` about it, and re-record: the matching `wip-only` fixture will then
fail with "upstream now reads the fixture", which is the signal to re-classify it.

## Re-reading upstream

```bash
git clone --depth 1 --branch feature/compose https://github.com/microsoft/WSL
```

Then walk the numbered rules above against the files in the table, update
`UpstreamComposeInterpreter`, update this pin, and re-record (see `../README.md`).
