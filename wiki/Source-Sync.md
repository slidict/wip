# Source Sync

`sync:` mirrors your source tree into a named volume instead of bind-mounting it live, so the
running app reads from fast storage inside the VM. It's the fix for the slow-boot problem described
in [Fixing a Slow Boot](Fixing-a-Slow-Boot).

Entirely optional. `sync: {}` alone already works — every key below has a default.

## What it changes

```
without sync:                     with sync:
  host ./  ──(virtiofs)──► /app     host ./ ──(virtiofs, read-only)──► /host-src
                                                                          │ rsync
                                                                          ▼
                                    named volume app-src ──────────────► /app
```

The app only ever touches the volume. The bind mount still exists, but nothing reads from it
except `rsync`, in bulk, on demand.

## Minimal config

```yaml
sync:
  exclude:
    - .git
    - tmp/
    - node_modules/
```

## Every parameter

```yaml
sync:
  source: .          # host path, relative to wip.yml (default: the wip.yml directory)
  target: /app       # container path served by the volume
  volume: app-src    # named volume holding the mirror
  mount: /host-src   # where the source is bind-mounted read-only
  exclude:           # rsync --exclude patterns
    - .git
    - tmp/
  delete: true       # rsync --delete
  command: rsync     # binary that does the mirroring
  options: []        # extra flags appended to the rsync invocation
  interval: 2        # seconds between syncs for `wip sync --watch`
  mode: exec         # exec | run — see Sync Modes
  image: null        # image for the mirror container
  build:             # or have wip build that image itself
    dockerfile: |
      FROM alpine:latest
      RUN apk add --no-cache rsync
    tag: null
```

| Key | Default | Notes |
|---|---|---|
| `source` | the `wip.yml` directory | expanded against `wip.yml`, so it's identical from any subdirectory |
| `target` | the primary container's `workdir`, else `/app` | must be absolute |
| `volume` | `<container>-src` (`wip-src` if no container is named) | |
| `mount` | `/host-src` | must be absolute, and must differ from `target` |
| `exclude` | `[]` | each becomes `--exclude=PATTERN` |
| `delete` | `true` | adds `--delete` |
| `command` | `rsync` | point it at any tool with compatible flags |
| `options` | `[]` | appended verbatim after the excludes |
| `interval` | `2` | must be a positive number |
| `mode` | `exec` (`run` under `mode: compose`) | see [Sync Modes](Sync-Modes) |
| `image` / `build` | unset | see [Sync Modes](Sync-Modes) |

## The rsync invocation

```
rsync -r -l -t --whole-file [--delete] [--exclude=… …] [options…] /host-src/ /app/
```

The base flags are deliberately minimal for a local-to-local mirror:

| Flag | Why |
|---|---|
| `-r` | walk the tree |
| `-l` | keep symlinks as symlinks |
| `-t` | preserve mtimes, so re-syncs quick-check (size + mtime) instead of re-transferring |
| `--whole-file` | skip the delta-transfer checksum pass, which only pays off over a slow network |

Owner/group/permission preservation (`-o -g -p`, part of `-a`) is intentionally left out, since
both sides are the same user. Add them back via `options: ["-o", "-g", "-p"]` if your project needs
them.

The trailing slashes on both paths matter: they copy the *contents* of the mount into the target
rather than nesting it one directory deeper.

## Mount rewriting

With `sync:` configured, wip rewrites the **primary** container's volumes:

- Any entry mounting `target` (or `mount`) — the usual `.:/app` — is dropped and replaced by
  `<source>:/host-src:ro` plus `<volume>:/app`.
- Every other volume (`bundle:/usr/local/bundle`, …) passes through untouched.
- Sidecar `dependencies:` entries are never rewritten.

Matching accounts for trailing mount options, so `.:/app:ro`, `.:/app:cached`, and `.:/app/` are
all recognized as mounting the target.

Under [`mode: compose`](Compose-Mode) no rewriting happens at all — compose owns the volume layout,
so your compose service must declare the volume itself. See [Sync Modes](Sync-Modes).

## When mirroring happens

| Trigger | What runs |
|---|---|
| `wip up` | one mirror before the primary container boots (skip with `--no-sync`) |
| `wip sync` | one mirror on demand |
| `wip sync --watch` | a mirror every `interval` seconds until `Ctrl-C` |

```console
$ wip up -d
wip: syncing /home/me/app -> app-src:/app
wip: run `wip sync --watch` in another terminal to keep /app up to date
```

## The two things to keep in mind

**1. rsync must exist somewhere.** Under `sync.mode: exec` it runs *inside* your app image, so that
image needs it:

```dockerfile
RUN apt-get update && apt-get install -y rsync
```

Or point `sync.command` at a copy tool the image already has. See [rsync Not Found](rsync-Not-Found).

**2. The mirror is one-way (host → volume).** Anything the app writes under `target` is removed by
the next `--delete` pass. Three ways out:

- `exclude` the path (`tmp/`, `log/`, `node_modules/`)
- give it its own volume (`- bundle:/usr/local/bundle`)
- set `delete: false`

Generated files you *want* back on the host (a scaffold generator's output, a migration file) won't
come back on their own — that's the trade-off for not paying the virtiofs cost.

## Default `exclude` per `wip init --template`

| `--template` | Stack | Default `exclude` |
|---|---|---|
| `rails` | Rails | `.git`, `log/`, `tmp/`, `storage/`, `public/assets/`, `public/packs/`, `.bundle/`, `vendor/bundle/`, `coverage/`, `node_modules/` |
| `node` | Node.js | `.git`, `node_modules/`, `dist/`, `build/`, `.next/`, `.cache/`, `coverage/` |
| `rust` | Rust | `.git`, `target/` |
| `csharp` | C# | `.git`, `bin/`, `obj/`, `.vs/`, `packages/` |
| *(omitted)* | — | `.git`, `tmp/`, `node_modules/` |

These are written live into the generated `wip.yml` by [wip init](wip-init), mirroring each stack's
own `github/gitignore` template.

## Diagnostics

```console
$ wip doctor
[OK] Sync source /home/me/app mirrors into volume app-src at /app
```

`wip doctor` fails if the source directory doesn't exist, and warns if you set `sync.image` /
`sync.build` while using `sync.mode: exec` — those only cover the pre-boot mirror. See
[wip doctor](wip-doctor).

## Related

- [Sync Modes](Sync-Modes) — `exec` vs. `run`, and the image requirements
- [wip sync](wip-sync)
- [Fixing a Slow Boot](Fixing-a-Slow-Boot) — why this exists
- [Continuous Sync](Continuous-Sync) — the two-terminal workflow
