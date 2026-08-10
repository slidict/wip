# wip build

Builds the image described by the `build` interaction in `wip.yml`, applying `.dockerignore` first.

```
wip build [--no-cache] [-- OPTIONS...]
```

## Configuration

`wip build` reads the interaction named `build`:

```yaml
interaction:
  build:
    type: build                            # implied for an entry named `build`
    context: .                             # relative to wip.yml's directory
    tag: slidict/slidict:development       # falls back to the inherited `image`
    shadow_context: /mnt/c/Users/me/AppData/Local/wip/build-contexts   # optional
```

Resulting invocation:

```
wslc build -t <tag> [extra options…] .
```

The `tag` falls back to the primary container's `image` when unset. An empty one fails:

```
Build image/tag must not be empty
```

## What happens

```console
$ wip build
wip: staging build context (/home/me/my-project)
wip: copying build context files: 1240/1240
wip: [debug] running: wslc.exe build -t myapp:dev .
```

1. Resolve `context` against `wip.yml`'s directory.
2. Read `.dockerignore` from the context root and stage only the included files — see
   [Dockerignore](Dockerignore).
3. If `shadow_context:` applies, sync a persistent Windows-side copy instead — see
   [Shadow Build Context](Shadow-Build-Context).
4. `chdir` into the staged directory and run `wslc build … .`

Step 4 uses `.` rather than an absolute path because `wslc build` crashes
(`ERROR_UNHANDLED_EXCEPTION`) when handed an absolute context.

## Flags

### `--no-cache`

```bash
wip build --no-cache
```

`wslc build` reuses matching local layers by default (like `docker build` without `--pull`).
`--no-cache` disables that. Note `wslc build` has no `--cache-from` flag — passing one is a hard
error.

### Extra options after `--`

Anything after `--` is passed straight through to `wslc build`:

```bash
wip build -- --build-arg RUBY_VERSION=3.4
wip build -- -f Dockerfile.dev
wip build --no-cache -- --build-arg TAG=ci
```

The `--` separator itself is stripped. `--no-cache` is inserted only once even if you also pass it
after `--`.

## Compose-native services

`wip build` builds the `build` **interaction**, which is a `wip.yml` concept. Services in
`compose.yml` that declare `build:` are built by `wip up`, not by this command:

```console
$ wip up -d
wip: building service 'app' (tag: myapp:dev) from /home/me/app
```

See [Compose Build](Compose-Build). `wip up --no-cache` applies `--no-cache` to those builds.

## Compose mode

Under [`mode: compose`](Compose-Mode), builds belong to your compose tool. A `type: build`
interaction is rejected:

```
commands.build: type 'build' is not supported in compose mode (use `wslc-compose build`/`up --build` directly)
```

## Common problems

| Symptom | Cause | Page |
|---|---|---|
| Staging copies far too many files | no/insufficient `.dockerignore` | [Dockerignore](Dockerignore) |
| Every build re-transfers the whole tree | context is WSL-side, no shadow configured | [Shadow Build Context](Shadow-Build-Context) |
| `pull access denied` | registry auth | [Registry Authentication](Registry-Authentication) |
| `no matching manifest for linux/…` | image arch mismatch | [Architecture Mismatch](Architecture-Mismatch) |
| Build fails fetching a `git:` dependency | Git missing in the build environment (`wip doctor` warns) | [wip doctor](wip-doctor) |

## Related

- [Interactions](Interactions) — the `build` entry's keys
- [Dockerignore](Dockerignore)
- [Shadow Build Context](Shadow-Build-Context)
- [wip up](wip-up)
