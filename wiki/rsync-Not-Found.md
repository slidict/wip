# rsync Not Found

```
rsync: not found
rsync: command not found
exec: "rsync": executable file not found in $PATH
```

The container running the mirror doesn't have `rsync`.

## The hint wip prints

```
`wip sync` needs rsync inside the image.

Install it in your Dockerfile:

  RUN apt-get update && apt-get install -y rsync

Or point sync.command at a tool the image already has.
```

## Which image is missing it?

This is the part that trips people up: the answer depends on `sync.mode` **and** on which step
failed.

| Step | Container | Image that needs rsync |
|---|---|---|
| `wip up`'s pre-boot mirror | throwaway (always) | `sync.build`'s tag → `sync.image` → the primary entry's image |
| `wip sync` / `--watch`, `mode: exec` | the running primary container | the primary container's image |
| `wip sync` / `--watch`, `mode: run` | throwaway | `sync.build`'s tag → `sync.image` → the primary entry's image |

So under the default `sync.mode: exec` with no `sync.image`/`sync.build`, **your app image** needs
rsync for both paths — the pre-boot mirror falls back to it too.

`wip doctor` warns about the half-configured case:

```
[WARN] sync.image/sync.build only cover `wip up`'s one-time pre-boot mirror … sync.mode: exec's
`wip sync`/`wip sync --watch` run rsync inside the primary container instead, so its image
needs rsync too
```

Full explanation: [Sync Modes](Sync-Modes).

## Fix 1: install rsync in the app image

```dockerfile
# Debian/Ubuntu
RUN apt-get update && apt-get install -y rsync && rm -rf /var/lib/apt/lists/*

# Alpine
RUN apk add --no-cache rsync
```

```bash
wip build
wip down && wip up -d
```

Simplest option, and it keeps `sync.mode: exec` — the fastest mode, since there's no container
start per mirror.

## Fix 2: a dedicated mirror image

Keep your app image minimal and let wip build a tiny one just for rsync:

```yaml
sync:
  mode: run
  build:
    dockerfile: |
      FROM alpine:latest
      RUN apk add --no-cache rsync
```

Built once per `wip up` / `wip sync` invocation (not per `--watch` tick), tagged
`wip-sync-<container>:latest` unless you set `build.tag`.

Or point at an image you manage yourself:

```yaml
sync:
  mode: run
  image: my-registry/rsync:latest
```

`sync.build`'s tag wins if both are set — don't configure both.

Required under [`mode: compose`](Compose-Mode), where there's no `dependencies:` entry to borrow an
image from.

## Fix 3: use a different tool

If your image already has something that can mirror a tree with compatible flags:

```yaml
sync:
  command: /usr/local/bin/my-mirror
```

wip builds the invocation as:

```
<command> -r -l -t --whole-file [--delete] [--exclude=… …] [options…] /host-src/ /app/
```

so your tool must accept those flags. In practice this means an rsync-compatible binary
(`rsync`-from-busybox often is not — check before relying on it).

## Verify

```bash
wip up -d
wip exec which rsync
wip sync
```

## Related

- [Sync Modes](Sync-Modes)
- [Source Sync](Source-Sync)
- [wip sync](wip-sync)
