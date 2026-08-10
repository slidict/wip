# Multi Arch Images

Fixing "the image doesn't run here" when your machine's CPU architecture doesn't match the image's.

## The symptom

```
no matching manifest for linux/arm64 in the manifest list entries
```

wip detects this and prints a hint automatically:

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

## Confirm your architecture

```console
$ wip doctor
[OK] Architecture: linux/arm64
```

wip reports `linux/amd64` for `x86_64`/`x64` hosts and `linux/arm64` for `aarch64`/`arm64`. This
line is always `[OK]` — it's informational, not a check.

## Inspect the image

```bash
docker buildx imagetools inspect ghcr.io/example/myapp:dev
```

```
Manifests:
  Name:      ghcr.io/example/myapp:dev@sha256:…
  Platform:  linux/amd64
```

One platform listed, and it isn't yours → that's the problem.

## Fix: publish a multi-arch image

```bash
docker buildx create --use          # once, if you have no builder
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/example/myapp:dev \
  --push .
```

`--push` is required: multi-platform manifests can't live in a local image store, only in a
registry.

Verify:

```bash
docker buildx imagetools inspect ghcr.io/example/myapp:dev
```

```
Manifests:
  Platform:  linux/amd64
  Platform:  linux/arm64
```

Then pull it fresh:

```bash
wip down
wip up -d
```

## If you can't rebuild the image

It's third-party, or you don't control the registry.

**Find an official multi-arch tag.** Most popular images (`postgres`, `redis`, `node`, `ruby`)
publish both. Check with `imagetools inspect` before assuming.

**Pin a different base.** For your own Dockerfile, a multi-arch base gets you multi-arch output as
long as everything you install is available for both.

**Emulate.** `wslc` may support running a foreign-architecture image under emulation depending on
your setup, but it's slow enough that it isn't a real answer for a dev container.

## Building multi-arch from a wip build?

`wip build` shells out to `wslc build`, which builds for the host architecture. There is no
`--platform` plumbing in `wip.yml`. Multi-arch publishing is a separate, CI-shaped concern — do it
with `docker buildx` as above, then reference the published tag from `wip.yml`:

```yaml
dependencies:
  app:
    image: ghcr.io/example/myapp:dev
```

You can still pass extra flags through if your `wslc build` accepts them:

```bash
wip build -- --platform linux/arm64
```

## A CI recipe

```yaml
# .github/workflows/image.yml
- uses: docker/setup-buildx-action@v3
- uses: docker/login-action@v3
  with:
    registry: ghcr.io
    username: ${{ github.actor }}
    password: ${{ secrets.GITHUB_TOKEN }}
- uses: docker/build-push-action@v6
  with:
    platforms: linux/amd64,linux/arm64
    push: true
    tags: ghcr.io/example/myapp:dev
```

Both an Apple Silicon laptop and an x86 workstation then pull the right variant of the same tag.

## Related

- [Architecture Mismatch](Architecture-Mismatch) — the error page
- [wip doctor](wip-doctor)
- [wip build](wip-build)
