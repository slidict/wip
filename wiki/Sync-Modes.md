# Sync Modes

`sync.mode` decides **where** the mirror runs: inside the already-running app container (`exec`),
or in a throwaway container wip boots just for it (`run`). That choice determines which image needs
`rsync` installed.

## The two modes

| | `exec` | `run` |
|---|---|---|
| Where rsync runs | inside the running primary container | in a fresh `wslc run --rm` container |
| Needs the app image to have rsync | **yes** | no |
| Uses `sync.image` / `sync.build` | no (ignored) | yes |
| Requires the container to be up | yes | no |
| Overhead per mirror | none | one container start |
| Default under `mode: container` / `compose-native` | ✔ | |
| Default under `mode: compose` | | ✔ (and `exec` is rejected) |

The mode is fixed by config, not guessed from whether a container happens to be running. That's
deliberate: a mirror that silently changes behavior depending on machine state is a bad thing to
debug.

## The one exception: `wip up`'s pre-boot mirror

`wip up` mirrors the source **before** the primary container exists, so that step always uses a
throwaway container — regardless of `sync.mode`.

This is the single most confusing thing about sync, so stated plainly:

| Step | Container used | Image used |
|---|---|---|
| `wip up`'s pre-boot mirror | throwaway | `sync.build`'s tag → `sync.image` → the primary entry's image |
| `wip sync` / `--watch` under `mode: exec` | the running primary container | the primary container's own image |
| `wip sync` / `--watch` under `mode: run` | throwaway | `sync.build`'s tag → `sync.image` → the primary entry's image |

So under the default `exec` mode, **both** images may need `rsync`: the throwaway one for pre-boot
(which falls back to your app image anyway, unless you set `sync.image`/`sync.build`), and the app
image for every later mirror.

`wip doctor` warns about exactly this case:

```
[WARN] sync.image/sync.build only cover `wip up`'s one-time pre-boot mirror (the primary
container isn't running yet, so that step always uses a throwaway container) — sync.mode:
exec's `wip sync`/`wip sync --watch` run rsync inside the primary container instead, so its
image needs rsync too
```

## Image resolution for the throwaway container

In order:

1. `sync.build`'s tag, if `sync.build` is configured (built first — see below)
2. `sync.image`, if set
3. the primary `dependencies:` entry's own `image`

Under [`mode: compose`](Compose-Mode) step 3 doesn't exist — there's no `dependencies:` entry to
borrow from — so one of `sync.image` / `sync.build` is **required**:

```
sync.image or sync.build is required under mode: compose (there's no dependencies: entry to
borrow the mirror container's image from)
```

## Why `exec` is rejected under `mode: compose`

```
sync.mode: exec needs mode: container (compose owns its services' mounts, so it can't
guarantee the running container has the sync mounts attached)
```

Only a container wip itself booted is guaranteed to have both the read-only source mount and the
named volume attached. A compose service's mounts come from `compose.yml`, which wip doesn't
rewrite. Running rsync inside it would mirror from a path that isn't there.

## `sync.build`: a dedicated mirror image

Under `sync.mode: run`, a fresh container starts on every mirror. Reusing your app's full image for
something that only ever runs `rsync` just adds startup overhead. wip doesn't publish or default to
a minimal image (same reasoning as `compose.command` — picking a third-party image for you isn't
its call), but it can build one for you:

```yaml
sync:
  mode: run
  build:
    dockerfile: |
      FROM alpine:latest
      RUN apk add --no-cache rsync
    tag: my-sync:latest      # optional; default: wip-sync-<container>:latest
```

- The Dockerfile is written to a temp directory and built with `wslc build -t <tag> .`.
- It's built **once per `wip up` / `wip sync` invocation** — including once before a `--watch` loop
  starts, not on every tick.
- `sync.build.dockerfile` must be non-empty (`sync.build.dockerfile must not be empty`).
- If both are set, `sync.build`'s tag wins over `sync.image` — so don't configure both.

Prefer managing the image yourself? Build and tag it however you like, then set `sync.image` to
that tag.

## Choosing

| Situation | Mode |
|---|---|
| `mode: container` / `compose-native`, app image has (or can get) rsync | `exec` — fastest |
| App image must stay minimal, you'd rather not add rsync to it | `run` + `sync.build` |
| `mode: compose` | `run` (forced) + `sync.image` or `sync.build` (required) |

## Related

- [Source Sync](Source-Sync) — the full parameter list
- [wip sync](wip-sync)
- [rsync Not Found](rsync-Not-Found)
- [Continuous Sync](Continuous-Sync)
