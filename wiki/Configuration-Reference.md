# Configuration Reference

Index of every `wip.yml` key. Each feature has its own page; this page is the map plus the
top-level keys that don't warrant one.

## Top-level keys

| Key | Modes | Default | Meaning |
|---|---|---|---|
| `version:` | all | `1` | Config schema version. Only `1` is supported; anything else is a `ConfigError`. |
| `mode:` | all | `container` | `container` / `compose` / `compose-native`. See [Choosing a Mode](Choosing-a-Mode). |
| `wslc:` | all | `{command: auto}` | Which `wslc` binary to shell out to. See [Config File Discovery](Config-File-Discovery). |
| `container:` | container | *(none)* | Which `dependencies:` entry is primary. Required once `dependencies:` has entries. See [Dependencies](Dependencies). |
| `network:` | container | *(unset)* | Container network shared by every dependency. See [Networking](Networking). |
| `dependencies:` | container | `{}` | Every container wip manages. See [Dependencies](Dependencies). |
| `compose:` | compose, compose-native | — | Which compose file/service to use. See [Compose Mode](Compose-Mode) / [Compose Native Mode](Compose-Native-Mode). |
| `interaction:` / `commands:` | all | `{}` | Project commands run as `wip NAME`. See [Interactions](Interactions). |
| `sync:` | all | *(unset)* | Mirror the source into a named volume. See [Source Sync](Source-Sync). |

## By feature

### File and binary resolution
- [Config File Discovery](Config-File-Discovery) — where `wip.yml` is found, `--config`, `version:`, the `wslc:` block

### Containers
- [Dependencies](Dependencies) — `dependencies:` entries, `container:`, primary vs. sidecar
- [Networking](Networking) — `network:`, when it's created, name resolution
- [Restart Policies](Restart-Policies) — `restart:` values and `wip up --watch`

### Commands
- [Interactions](Interactions) — `interaction:` / `commands:`, `type: exec|run|build`, name collisions

### Environment
- [Env Files](Env-Files) — `.env` loading rules, `--env-file`, precedence
- [Secret Masking](Secret-Masking) — what `wip config` redacts, and what that does and doesn't protect

### Builds
- [Dockerignore](Dockerignore) — how `.dockerignore` filters the build context
- [Shadow Build Context](Shadow-Build-Context) — `shadow_context:`, when it applies, how it stays incremental

### Source sync
- [Source Sync](Source-Sync) — the `sync:` block, every parameter, mount rewriting
- [Sync Modes](Sync-Modes) — `exec` vs. `run`, `sync.image` / `sync.build`, per-mode defaults

### compose.yml
- [Compose File Support](Compose-File-Support) — the supported subset
- [Compose Build](Compose-Build) — `build:` services
- [Compose Depends On](Compose-Depends-On) — ordering
- [Compose Profiles](Compose-Profiles) — profile-gated services
- [Compose Variable Interpolation](Compose-Variable-Interpolation) — `${VAR}`

## Which keys are legal in which mode

| Key | `container` | `compose-native` | `compose` |
|---|---|---|---|
| `container:` | required with deps | rejected (implied) | rejected (implied) |
| `dependencies:` | yes | rejected | rejected |
| `network:` | yes | derived from `compose.project` | rejected |
| `compose.service` | — | required | required |
| `compose.file` | — | optional | optional |
| `compose.project` | — | optional | optional |
| `compose.command` | — | rejected | required |
| `interaction.type: run` | yes | yes | rejected |
| `interaction.type: build` | yes | yes | rejected |
| `sync.mode: exec` | yes (default) | yes (default) | rejected |
| `sync.image` / `sync.build` | optional | optional | one required |

Every rejection above is a load-time `ConfigError` — see [Configuration Errors](Configuration-Errors)
for the exact messages.

## Seeing the effective config

```bash
wip config
```

prints the fully defaulted, secret-masked configuration as YAML — the fastest way to check what
wip actually resolved. See [wip config](wip-config).
