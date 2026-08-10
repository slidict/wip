# Architecture Mismatch

```
no matching manifest for linux/arm64 in the manifest list entries
```

The image has no variant for your CPU architecture.

## The hint wip prints

```
The image does not contain a manifest for the current CPU architecture.

Current architecture:
  linux/arm64

Inspect the image with:

  docker buildx imagetools inspect <image>

Rebuild and push a multi-platform image with:

  docker buildx build \
    --platform linux/amd64,linux/arm64 \
    -t <image> \
    --push .
```

Triggered by output matching `no matching manifest for linux/amd64` or `.../arm64`.

## Confirm your architecture

```console
$ wip doctor
[OK] Architecture: linux/arm64
```

| Host CPU | Reported |
|---|---|
| `x86_64`, `x64` | `linux/amd64` |
| `aarch64`, `arm64` | `linux/arm64` |
| anything else | `linux/<host_cpu>` |

## Confirm the image's

```bash
docker buildx imagetools inspect ghcr.io/example/myapp:dev
```

```
Manifests:
  Platform:  linux/amd64
```

One platform, and it isn't yours → confirmed.

## Fixes

### Your own image → publish multi-arch

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/example/myapp:dev \
  --push .
```

Full walkthrough, including a CI recipe: [Multi Arch Images](Multi-Arch-Images).

### Third-party image → find a multi-arch tag

Most popular images publish both architectures. Check before assuming:

```bash
docker buildx imagetools inspect postgres:16
```

Some vendor images publish arch-specific tags (`-amd64`, `-arm64`) instead of a manifest list —
pinning one works, at the cost of a `wip.yml` that only runs on one kind of machine.

### After the image is fixed

```bash
wip down
wip up -d      # pulls fresh
```

## Also seen as

Not every architecture problem surfaces as this message. A container that starts and then
immediately dies with `exec format error` is the same underlying issue: a binary built for the
wrong architecture, either in the image or copied in during the build.

Check the base image's platform in your Dockerfile, and any binaries you `COPY` in.

## Related

- [Multi Arch Images](Multi-Arch-Images) — the full guide
- [wip doctor](wip-doctor)
