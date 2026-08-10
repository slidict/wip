# Fixing a Slow Boot

The single most common performance complaint with WSLC: a container that takes minutes to boot and
looks completely hung, while the host shows almost no activity.

## Recognizing the symptom

Run the slow command with `--debug`:

```console
$ wip rails c --debug
wip: [debug] running: wslc.exe exec -it -w /app app bin/rails c
+ wslc.exe exec -it -w /app app bin/rails c
wip: [debug] still running (load 0.31 0.22 0.18 | mem 3.1G/15.6G | io read 40KB/s write 0KB/s | top: wslc.exe(8842) cpu 2.1%/mem 0.9%): running: …
wip: [debug] still running (load 0.29 0.24 0.19 | mem 3.1G/15.6G | io read 36KB/s write 0KB/s | top: …): running: …
```

The tell: **low CPU, low memory, low IO, and nothing happening for minutes.** Almost no data is
moving, so nothing looks busy — but the process is blocked the entire time.

If instead you see sustained high `io read` or high CPU, this isn't your problem — something is
genuinely doing work.

## The cause

WSLC containers run in their own VM. A bind-mounted host directory (`.:/app`) is always shared in
over virtiofs — **even when the host path is already on WSL's native filesystem**. Every file
operation is a round trip across that boundary.

Frameworks that scan large directory trees at startup make an enormous number of tiny
`stat`/`open` calls:

- Ruby's Zeitwerk autoloader walking `app/`
- Bundler resolving a large `Gemfile.lock`
- Node resolving through `node_modules/`
- .NET probing assemblies

Each call is cheap in isolation and ruinous at ten thousand round trips. CPU on the Windows side
can look busy while essentially no data is transferred, and the process appears hung.

## The fix: stop bind-mounting the source

Mirror it into a named volume instead. The app then reads from fast native storage inside the VM,
and the bind mount is only touched in bulk by `rsync`:

```
before:  host ./ ──(virtiofs, every stat/open)──► /app   ← the app reads here
after:   host ./ ──(virtiofs, bulk rsync only)──► /host-src
                                    │
                                    ▼
         named volume app-src ─────────────────► /app     ← the app reads here
```

### Minimal configuration

Add a `sync:` block to `wip.yml`:

```yaml
sync:
  exclude:
    - .git
    - tmp/
    - node_modules/
```

That's genuinely all of it — every other key has a default. wip then:

- rewrites the primary container's `.:/app` into `<source>:/host-src:ro` + `app-src:/app`
- mirrors before the container boots on `wip up`
- re-mirrors on demand with `wip sync`

```bash
wip down       # existing containers keep their old mounts
wip up -d      # recreated with the new ones
```

### Getting the excludes right

Exclude anything large, regenerated inside the container, or irrelevant:

```yaml
sync:
  exclude:
    - .git
    - log/
    - tmp/
    - storage/
    - public/assets/
    - .bundle/
    - vendor/bundle/
    - coverage/
    - node_modules/
```

`wip init --template rails|node|rust|csharp` writes a stack-appropriate list for you. See
[Source Sync](Source-Sync).

Two reasons to exclude generously:

1. Less to mirror = faster syncs.
2. Excluded paths survive `--delete`, so container-generated content (installed gems, compiled
   assets) isn't wiped on the next pass.

### rsync has to exist

Under the default `sync.mode: exec`, the mirror runs *inside* your app image:

```dockerfile
RUN apt-get update && apt-get install -y rsync
```

Don't want it in your app image? Use `sync.mode: run` with a dedicated image:

```yaml
sync:
  mode: run
  build:
    dockerfile: |
      FROM alpine:latest
      RUN apk add --no-cache rsync
```

See [Sync Modes](Sync-Modes) and [rsync Not Found](rsync-Not-Found).

## Keeping it current while you work

The mirror is a snapshot, not a live view. Run a watcher in a second terminal:

```bash
# terminal 1
wip up -d

# terminal 2
wip sync --watch
```

See [Continuous Sync](Continuous-Sync).

## Verify it worked

```bash
wip doctor                 # [OK] Sync source … mirrors into volume app-src at /app
wip config                 # confirm the resolved source/volume/target
wip rails c --debug        # compare the time before the prompt appears
```

## The trade-off

You give up an always-live view of host edits. The mirror is one-way (host → volume) and slightly
delayed, and `--delete` removes anything the app wrote under the target unless you exclude it, give
it its own volume, or set `delete: false`.

For an edit-run-edit loop with a 2-second watcher, that's usually invisible. For a workflow that
depends on generated files landing back on the host (scaffold generators, migration files), you'll
copy them out manually — see [Source Sync](Source-Sync).

## Also worth checking

If **builds** are slow rather than boots, that's a different problem with a different fix — the
build context crossing the same boundary. See [Shadow Build Context](Shadow-Build-Context) and
[Dockerignore](Dockerignore).

## Related

- [Source Sync](Source-Sync)
- [Sync Modes](Sync-Modes)
- [Continuous Sync](Continuous-Sync)
- [Debug Output](Debug-Output)
