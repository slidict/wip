# Glossary

### primary container

The one container `wip` treats as "the app": the target of `exec`, `run`, `build`, `shell`, and
every [interaction](Interactions). Named by `container:` under [Container Mode](Container-Mode),
and by `compose.service` under [Compose Mode](Compose-Mode) /
[Compose Native Mode](Compose-Native-Mode). It has no default — see [Dependencies](Dependencies).

### sidecar / dependency

Every other entry under `dependencies:` (or every other `compose.yml` service): a database, Redis,
a queue worker. wip only ever starts and stops these; it never execs into them. See
[Dependencies](Dependencies).

### interaction

A named project command declared in `wip.yml`, run as `wip <name> [args...]` — `wip rspec`,
`wip rails console`. Spelled `interaction:` (the primary key, same as `dip`) or `commands:` (an
alias). Declaring both is an error. See [Interactions](Interactions).

### dispatch

The built-in that runs an interaction explicitly: `wip dispatch NAME`. Needed only when an
interaction's name collides with a built-in command, since the built-in wins. See
[wip dispatch](wip-dispatch).

### bind mount

A host directory shared into the container (`.:/app`). Under WSLC this always crosses a virtiofs
boundary, which is what makes framework boot slow. See [Fixing a Slow Boot](Fixing-a-Slow-Boot).

### sync volume

The named volume that holds a mirror of your source, so the running app reads from fast storage
inside the VM instead of a bind mount. Defaults to `<container>-src`. See
[Source Sync](Source-Sync).

### mirror

One `rsync` pass from the read-only source mount into the sync volume. Run before boot by
`wip up`, on demand by `wip sync`, and repeatedly by `wip sync --watch`. One-way, host → volume.

### shadow context

A persistent copy of the build context kept on the Windows filesystem so `wslc build` doesn't have
to read a WSL-side tree over the VM boundary on every build. Enabled per build command via
`shadow_context:`. See [Shadow Build Context](Shadow-Build-Context).

### staged context

The temporary directory `wip build` assembles by applying `.dockerignore` to the build context
before handing it to `wslc build`. Distinct from a shadow context, which is persistent and
Windows-side. See [Dockerignore](Dockerignore).

### `WslcContainerState`

The integer `wslc list --format json` reports in each entry's `State` field:

| Value | State | Meaning for wip |
|---|---|---|
| `0` | invalid | not a usable container |
| `1` | created | exists, never started |
| `2` | running | up |
| `3` | exited | the only state `wip up --watch` restarts |
| `4` | deleted | gone; needs `wip up` to recreate, not `start` |

Unlike Docker there is no separate `dead` state. Reference:
[wsl.dev](https://wsl.dev/api-reference/c/enumerations/wslccontainerstate/). See
[Restart Policies](Restart-Policies).

### compose-for-wslc tool

A third-party binary that speaks Compose vocabulary against `wslc`. `mode: compose` bridges to one
of these; wip doesn't bundle or favor any. See [Third Party Compose Tools](Third-Party-Compose-Tools).

### `ConfigError`

The error class raised for every configuration problem, at load time, naming the offending key.
Catalog: [Configuration Errors](Configuration-Errors).
