# Configuration Errors

Every configuration problem raises a `ConfigError` **at load time**, before any container is
created, and names the offending key. This page catalogs them.

Reproduce any of these quickly with:

```bash
wip config      # or wip doctor, which reports them as [FAIL]
```

## File and version

| Message | Cause |
|---|---|
| `wip.yml was not found (searched from … to the filesystem root)` | No `wip.yml` in the current directory or any ancestor. Use `--config PATH`. See [Config File Discovery](Config-File-Discovery). |
| `Could not parse /path/wip.yml: …` | Invalid YAML. Note aliases are **disabled** in `wip.yml` — an anchor/merge key fails here. |
| `Unsupported configuration version: 2` | Only `version: 1` exists. |

## Mode

| Message | Cause |
|---|---|
| `mode must be one of container, compose, compose-native` | Typo in `mode:`. |
| `mode: compose requires a compose: block` | The mode is set but `compose:` is missing. |
| `a compose: block requires mode: compose or compose-native` | `compose:` present with `mode: container` (or no `mode:`). |

## Dependencies

| Message | Cause |
|---|---|
| `dependencies must be a mapping` | `dependencies:` is a list or scalar. |
| `container: must be set when dependencies: has entries` | No default exists — name the primary entry. See [Dependencies](Dependencies). |
| `dependencies.app must be a mapping` | An entry is a scalar or list. |
| `dependencies.app must set image` | Every entry needs a non-empty `image`. |
| `No dependencies.app entry (check container: in wip.yml)` | `container:` names an entry that doesn't exist. |
| `Configured container must not be empty` / `container: must be set in wip.yml` | Reached a command needing the primary container with none set. |
| `Unknown dependency: redis` | An internal lookup for a name not in `dependencies:`. |
| `Configured network must not be empty` | A network operation with `network:` empty. |

## Commands / interactions

| Message | Cause |
|---|---|
| `commands is mutually exclusive with interaction — pick one` | Both keys declared. See [Interactions](Interactions). |
| `commands must be a mapping` | The block is a list or scalar. |
| `commands.rspec must be a mapping` | An entry is a scalar. |
| `Invalid command type for migrate: exectue` | `type:` must be `exec`, `run`, or `build`. |
| `commands.build.shadow_context must be a non-empty path for a build command` | `shadow_context` on a non-build command, or empty. See [Shadow Build Context](Shadow-Build-Context). |
| `Unknown command: nope` | `wip nope` with no matching interaction. See [wip dispatch](wip-dispatch). |
| `Build image/tag must not be empty` | A build command with neither `tag` nor an inherited `image`. |
| `commands.migrate: type 'run' is not supported in compose mode (use \`wslc-compose build\`/\`up --build\` directly)` | Only `type: exec` works under `mode: compose`. |

## Compose block

| Message | Cause |
|---|---|
| `compose must be a mapping` | `compose:` is a scalar or list. |
| `compose.service must not be empty` | Required in both compose modes. |
| `compose is mutually exclusive with dependencies` | Pick one orchestration path. |
| `compose is mutually exclusive with network` | Under compose-native, the network comes from `compose.project`. See [Networking](Networking). |
| `compose.command must not be empty` | Required under `mode: compose`. |
| `compose.command is not used under mode: compose-native (wip drives wslc directly — there's no external compose binary to name)` | Remove it. |
| `compose mode: no compose file found next to /path/wip.yml (looked for compose.yml, compose.yaml, docker-compose.yml, docker-compose.yaml)` | Set `compose.file`, or put a compose file next to `wip.yml`. |
| `compose.service 'seed' is gated behind profiles: … but wip has no --profile flag to activate one` | See [Compose Profiles](Compose-Profiles). |

## compose.yml parsing (compose-native only)

| Message | Cause |
|---|---|
| `Compose file not found: /path/compose.yml` | `compose.file` points at a missing file. |
| `Could not parse /path/compose.yml: …` | Invalid YAML. Aliases **are** enabled here. |
| `compose.yml: services: must be a mapping` | Missing or malformed `services:`. |
| `compose.yml: services.app must be a mapping` | A service is a scalar. |
| `compose.yml: services.app has unsupported key(s): healthcheck, deploy` | Outside the supported subset. See [Compose File Support](Compose-File-Support). |
| `compose.yml: services.app must set image or build` | One is required. |
| `compose.yml: services.app.build must be a string or mapping` | `build:` is a list. |
| `compose.yml: services.app.build has unsupported key(s): target` | Only `context`, `dockerfile`, `args`, `shadow_context`. See [Compose Build](Compose-Build). |
| `compose.yml: services.app.ports must be an array` | Scalar `ports:`. |
| `compose.yml: services.app.volumes only supports short syntax ("host:container"), not long-syntax mappings` | Rewrite as strings. |
| `compose.yml: services.app.profiles must be an array of strings` | Malformed `profiles:`. |
| `compose.yml: services.app.environment must be a mapping or an array of KEY=VALUE` | Wrong shape. |
| `compose.yml: services.app.environment.FOO must have a value (host environment pass-through is not supported)` | A null mapping value. See [Env Files](Env-Files). |
| `compose.yml: services.app.environment entries must be KEY=VALUE` | A bare key in the array form. |
| `compose.yml: services.app.build.args …` | Same rules as `environment:`. |
| `compose.yml: services.app.depends_on must be an array or a mapping` | Wrong shape. |
| `compose.yml: services.app depends_on unknown service 'databse'` | Typo, or the service isn't defined. |
| `compose.yml: services.app.depends_on.db: condition 'service_healthy' is not supported (only service_started — no health checks)` | See [Compose Depends On](Compose-Depends-On). |
| `compose.yml: services.app depends_on 'x', gated behind profiles: (…) wip never activates (no --profile flag)` | See [Compose Profiles](Compose-Profiles). |
| `compose.yml: services.app is part of a depends_on cycle` | Circular dependency. |
| `compose.yml contains a self-referential YAML alias` | An anchor that refers to itself. See [Compose Variable Interpolation](Compose-Variable-Interpolation). |

## Sync

| Message | Cause |
|---|---|
| `sync must be a mapping` | `sync:` is a scalar or list. (`sync: {}` is valid.) |
| `sync.target must be an absolute path` | Must start with `/`. |
| `sync.mount must be an absolute path` | Must start with `/`. |
| `sync.mount must differ from sync.target` | They'd shadow each other. |
| `sync.interval must be a positive number` | Non-numeric or ≤ 0. |
| `sync.mode must be one of exec, run` | Typo. See [Sync Modes](Sync-Modes). |
| `sync.mode: exec needs mode: container (compose owns its services' mounts …)` | Under `mode: compose`, only `run` works. |
| `sync.image or sync.build is required under mode: compose (there's no dependencies: entry to borrow the mirror container's image from)` | Name an image. |
| `sync.build must be a mapping` | Wrong shape. |
| `sync.build.dockerfile must not be empty` | Required when `sync.build` is present. |
| `No sync: block configured in wip.yml` / `` `wip sync` needs a sync: block in wip.yml `` | Running a sync operation without configuring it. |
| `No sync.build configured in wip.yml` | An internal build request with no `sync.build`. |

## Build context

| Message | Cause |
|---|---|
| `shadow_context (/path) must not be inside the build context (/path)` | The shadow would copy itself recursively. See [Shadow Build Context](Shadow-Build-Context). |

## Runtime restrictions

| Message | Cause |
|---|---|
| `` `wip logs` is only available in compose mode `` | Not available under `mode: container`. See [wip logs](wip-logs). |
| `` `wip logs` under mode: compose-native takes at most one SERVICE … `` | Pass one, or none. |
| `` `wip up --watch` is not supported under mode: compose … `` | See [Restart Policies](Restart-Policies). |
| `--interval must be a positive number` | On `wip up --watch` or `wip sync --watch`. |

## Not a ConfigError

These use the plain `Error` class instead, but read the same way:

| Message | Cause |
|---|---|
| `/path/wip.yml already exists (use --force to overwrite)` | [wip init](wip-init) protecting your file. |
| `unknown --template "python" (valid: rails, node, rust, csharp)` | [wip init](wip-init). |
| `WSLC was not found. Checked: …` | `CommandNotFoundError` — see [WSLC Not Found](WSLC-Not-Found). |

## Related

- [Configuration Reference](Configuration-Reference)
- [wip doctor](wip-doctor)
- [Troubleshooting & FAQ](Troubleshooting-and-FAQ)
