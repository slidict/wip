# Shadow Build Context

An optimization for projects living on WSL's native filesystem. It keeps a persistent copy of the
build context on the **Windows** filesystem, so `wslc build` reads from fast local storage instead
of pulling the tree across the VM boundary on every build.

## The problem

WSLC containers run in their own VM. A build context on the WSL side has to be shared in over
virtiofs, file by file. For a project with tens of thousands of files, that transfer dominates the
build — and it happens again on every single build, even when nothing changed.

Projects already on `/mnt/c` (or another mounted Windows drive) don't have this problem: the files
are already where the VM can read them cheaply.

## Enabling it

On a build interaction:

```yaml
interaction:
  build:
    type: build
    context: .
    tag: myapp:dev
    shadow_context: /mnt/c/Users/me/AppData/Local/wip/build-contexts
```

On a compose-native service's `build:`:

```yaml
# compose.yml
services:
  app:
    build:
      context: .
      dockerfile: Dockerfile
      shadow_context: /mnt/c/Users/me/AppData/Local/wip/build-contexts
```

Point it at a directory on the Windows filesystem. wip creates a subdirectory per source path
underneath it, so one root can serve every project on the machine.

## When it actually applies

All three must hold, or wip silently builds directly instead:

| Condition | Why |
|---|---|
| `shadow_context:` is set | opt-in; no default |
| Running on WSL2 | WSL1 (and anything else) has no VM boundary to optimize across |
| The context is **not** under `/mnt/<drive>` | already Windows-side; copying would only add work |

You can tell which path was taken from the build output:

```console
wip: using shadow build context at /mnt/c/Users/me/AppData/Local/wip/build-contexts/<hash>/context
```

## How the incremental sync works

Inside the shadow root, wip keeps one directory per source path, keyed by a hash of that path:

```
<shadow_root>/
  <sha256-of-context-path>/
    lock            # exclusive lock held for the duration of a build
    manifest.json   # what was copied last time, and its fingerprints
    context/        # the actual staged context handed to wslc
```

On each build:

1. Walk the context, applying [`.dockerignore`](Dockerignore).
2. Fingerprint every included file — symlinks by target, regular files by size + mtime (nanosecond
   precision) + mode.
3. Compare against `manifest.json`:
   - **changed or new** → copied
   - **removed, or newly ignored** → deleted from the shadow, pruning directories left empty
   - **unchanged** → skipped entirely
4. Write the new manifest and run `wslc build` against `context/`.

So the first build copies everything; later builds copy only the delta.

### Robustness details worth knowing

- **An exclusive file lock** is held across the whole build, so two concurrent `wip build` runs on
  the same context can't corrupt the shadow.
- **Copies are atomic** — each entry is written to a temp name and renamed into place, so an
  interrupted build leaves the previous copy intact rather than a half-written file.
- **File modes are preserved**, so an executable stays executable even on a DrvFs mount whose
  `fmask` would otherwise strip the bit and break a `RUN ./script`.
- **A missing or unparsable manifest** discards the shadow and rebuilds it from scratch, rather
  than leaving stale, deleted, or newly-ignored files in place.
- **Symlinks stay symlinks** — never dereferenced.

## Restrictions

The shadow root must live **outside** the build context. Otherwise the next build would walk the
shadow, copy it into itself, and grow without bound:

```
shadow_context (/home/me/app/.shadow) must not be inside the build context (/home/me/app)
```

The value must also be a non-empty string, and only makes sense on a `type: build` command:

```
commands.build.shadow_context must be a non-empty path for a build command
```

## Choosing a location

Anything under your Windows user profile works. `AppData/Local` is a good default because it's
machine-local and excluded from roaming profiles:

```
/mnt/c/Users/<you>/AppData/Local/wip/build-contexts
```

Avoid a synced folder (OneDrive, Dropbox) — you'd be paying sync cost on every build.

## Related

- [Dockerignore](Dockerignore) — what gets included in the first place
- [wip build](wip-build)
- [Compose Build](Compose-Build)
