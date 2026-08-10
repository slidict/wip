# wip init

Writes a starter `wip.yml`, pre-filled with commented defaults and `TODO:` markers.

```
wip init [--force] [--template rails|node|rust|csharp] [--config PATH]
```

## Mode detection

`wip init` makes one real decision for you — the mode — because it's the one thing a placeholder
can't express. It looks for a compose file next to the `wip.yml` it's about to write:

```
compose.yml → compose.yaml → docker-compose.yml → docker-compose.yaml
```

| Found | Writes |
|---|---|
| yes | `mode: compose-native`, with a `compose:` block pointing at that file |
| no | `mode: container`, with a `dependencies:` skeleton |

```console
$ wip init
wip: wrote /home/me/my-project/wip.yml (mode: container)
```

It never writes `mode: compose` on its own — that mode needs a `compose.command` only you can name.
Switch by hand if you want it; see [Compose Mode](Compose-Mode).

## Flags

### `--force`

Without it, an existing `wip.yml` is left alone:

```
/home/me/my-project/wip.yml already exists (use --force to overwrite)
```

`--force` overwrites it. There's no backup — commit first.

### `--template NAME`

Picks the default `sync.exclude` patterns for a stack, written **live** into the generated file
(not as a commented suggestion):

| `--template` | Stack | Default `exclude` |
|---|---|---|
| `rails` | Rails | `.git`, `log/`, `tmp/`, `storage/`, `public/assets/`, `public/packs/`, `.bundle/`, `vendor/bundle/`, `coverage/`, `node_modules/` |
| `node` | Node.js | `.git`, `node_modules/`, `dist/`, `build/`, `.next/`, `.cache/`, `coverage/` |
| `rust` | Rust | `.git`, `target/` |
| `csharp` | C# | `.git`, `bin/`, `obj/`, `.vs/`, `packages/` |
| *(omitted)* | — | `.git`, `tmp/`, `node_modules/` |

Each list mirrors that stack's own `github/gitignore` template — directories that are either
regenerated inside the container or too large to be worth mirroring.

An unknown name fails before writing anything:

```
unknown --template "python" (valid: rails, node, rust, csharp)
```

### `--config PATH`

Writes to `PATH` instead of `./wip.yml`. Mode detection then looks for a compose file next to
`PATH`, not next to your current directory.

## What you have to fill in

The generated file is heavily commented; the parts that actually need you:

**Container mode**

```yaml
container: app                 # rename freely — must match a dependencies: key
dependencies:
  app:
    image: your/image:tag      # TODO
    workdir: /app              # TODO: match your image, or delete
```

**Compose-native mode**

```yaml
compose:
  service: app                 # TODO: which service in compose.yml is the app
```

## What's deliberately left commented

- **`interaction:`** — an empty `interaction: {}` is indistinguishable from omitting the key, so
  the example block stays commented out. Uncomment and edit to add `wip test` etc. See
  [Interactions](Interactions).
- **Derived `sync:` keys** (`target`, `volume`) — they track another key (`workdir`, the container
  name) and would go stale if hardcoded. See [Source Sync](Source-Sync).
- **`compose.file` / `compose.project`** — auto-detected and directory-derived respectively; the
  comment shows what they'd resolve to.
- **`network:` under compose-native** — it's derived from `compose.project`, and setting it
  directly is a `ConfigError`.

## After running it

```bash
wip doctor      # confirm the environment and the config
wip up -d
```

## Related

- [Getting Started](Getting-Started)
- [Choosing a Mode](Choosing-a-Mode)
- [Source Sync](Source-Sync)
