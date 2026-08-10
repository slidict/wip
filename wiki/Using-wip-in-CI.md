# Using wip in CI

wip is built for interactive development, but nothing stops it running unattended — provided you
account for the absence of a TTY and the absence of anyone to answer a prompt.

> Note: CI runners rarely have WSL2 and WSLC available. In practice most projects run their tests
> on plain Docker in CI and use wip locally. This page is for the cases where you do have a
> WSLC-capable runner (a self-hosted Windows/WSL2 machine, for example).

## TTY handling is already automatic

wip only allocates a TTY when **both** stdin and stdout are real TTYs. In CI neither is, so nothing
is allocated even for commands configured `interactive: true`. You don't need `--no-interactive` —
though passing it is harmless and documents the intent:

```bash
wip exec --no-interactive bundle exec rspec
```

See [TTY Allocation](TTY-Allocation).

## Exit codes propagate

wip exits with the child's exit code, so a failing test suite fails the step:

| Code | Meaning |
|---|---|
| `0` | success |
| `1` | wip-level failure (`ConfigError`, `wip doctor` found a `[FAIL]`) |
| `127` | the resolved binary couldn't be executed |
| `130` | interrupted |
| `128 + N` | killed by signal `N` |
| other | passed through from the child |

## A workable pipeline

```bash
set -euo pipefail

wip version                 # record versions in the log
wip doctor                  # exits 1 on any [FAIL]
wip build --no-cache
wip up -d
wip exec --no-interactive bundle exec rspec
wip down
```

`wip doctor` as a gate is worth it: it turns "the container silently didn't have what it needed"
into an explicit failure with a message.

## Things to get right

### Never leave a `--watch` loop running

`wip up --watch` and `wip sync --watch` are foreground loops that never exit. A CI step running one
hangs until the job times out. Use `wip up -d` and `wip sync` (one-shot) instead.

### Always tear down

Containers outlive the process. On a self-hosted runner they'll still be there next build:

```bash
trap 'wip down || true' EXIT
```

### Configuration is environment-specific

Use `--config` and `--env-file` rather than mutating files in place:

```bash
wip --config ci/wip.yml --env-file ci/.env up -d
```

See [Global Options](Global-Options).

### Secrets

Pass them through the environment or an `--env-file` written at runtime, never a committed
`wip.yml`. Remember `wip config`'s masking is a key-name heuristic — don't dump config into a
public build log without reading it. See [Secret Masking](Secret-Masking).

### Registry login happens before wip

```bash
wslc registry login -u "$REGISTRY_USER" ghcr.io
wip up -d
```

See [Registry Authentication](Registry-Authentication).

### Debug output when a build fails

```bash
wip up -d --debug --debug-log=-
```

`--debug-log=-` forces resource snapshots inline rather than into a temp file you'd have to hunt
for afterwards. See [Debug Output](Debug-Output).

## Sync in CI

Usually unnecessary — a CI checkout is a one-shot copy, and the slow-boot problem
[sync solves](Fixing-a-Slow-Boot) is about repeated interactive boots. If you do use it, note:

- `wip up` mirrors once before boot anyway; no watcher needed.
- `--no-sync` skips even that, if the image already contains the source.
- Under `sync.mode: exec`, rsync must exist in the app image.

## Concurrency

Container and network names come from `wip.yml`, so two jobs on the same runner will collide.
Isolate with per-job config:

```bash
export JOB_ID="${CI_JOB_ID}"
wip --config "ci/wip-${JOB_ID}.yml" up -d
```

Under compose-native, `compose.project` names the network, so varying it per job is enough to keep
networks apart — container names still come from service names, so you'll want distinct configs
regardless.

## Related

- [TTY Allocation](TTY-Allocation)
- [Global Options](Global-Options)
- [wip doctor](wip-doctor)
- [CLI Command Reference](CLI-Command-Reference) — the exit code table
