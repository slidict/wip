# TODO: event-driven `wip up --watch` via `wslc events`

## Status: blocked — not yet released upstream

`wslc` gained a `system events` command (Docker `events`-style container event
stream) in [microsoft/WSL#41608](https://github.com/microsoft/WSL/pull/41608),
merged **2026-09-17** into `master`. The most recent published WSL release —
`2.9.12`, itself only on the `--pre-release` channel — went out **2026-09-14**,
three days *before* that merge. So as of 2026-09-18:

- `wslc system events` / `wslc events` do not exist in the current stable
  channel.
- They do not exist in the current `--pre-release` channel either
  (`wsl --update --pre-release` will not get you this today).
- The feature only exists on WSL's unreleased `master` branch. It will first
  become reachable in whatever pre-release build ships after `2.9.12`
  (`2.9.13` or later, going by the version sequence so far).

Re-check `gh api repos/microsoft/WSL/releases` (or `wslc system --help`) before
starting implementation — this doc should not be acted on until a release
actually contains the command.

## What `wslc events` will provide

From the PR's `EventStore`/`SystemEventsCommand` implementation:

- Command surface: `wslc system events`, and per existing hoisting
  conventions (`list`, `exec`, etc. are aliased to the top level) likely also
  `wslc events`.
- Options: `--since`, `--until`, repeatable `--filter` (`type=container`,
  `event=<action>`, `container=<id>`, `image=...`).
- Emitted event actions: **`create`, `start`, `kill`, `stop`, `destroy`**
  only. No `health_status` (or any other) event exists in this PR — the
  built-in-container-healthcheck-status-changed notification that
  `docker events` has does not have an equivalent here.
- Streams live, Docker-style formatted lines, cancellable with Ctrl-C.
- Backed by a session-local ring buffer; a reader that falls behind gets a
  reported gap rather than silently missing events.

## Where this applies in wip

Two loops in wip currently poll `wslc` on a fixed interval instead of reacting
to state changes:

1. **`WatchRestarts`** (`src/Wip.Cli/CliContext.cs:1369`, driven by
   `Loop` at `:1467`) — `wip up --watch` polls every `--interval` seconds
   (default 5s), and for every dependency calls `ContainerEntry` →
   `Probe(Builder.DependencyFind(name))`, i.e. spawns
   `wslc list --all --filter name=<name> --format json` — to see whether it
   has exited, then restarts it per `restart:` policy.
   **This is the one `wslc events` can replace.** `stop`/`destroy` map
   directly onto "exited"; a single long-lived `wslc events --filter
   type=container --filter event=stop --filter event=destroy` subprocess
   can drive `RestartIfExited` immediately on each line instead of on the
   next tick. `--filter container=<id>` takes a container **ID**, not a
   wip dependency name, and that ID changes every time `RestartIfExited`
   recreates the container — so a design built on `container=` filters
   would have to re-resolve name→ID and re-subscribe after every restart.
   Simpler: subscribe unfiltered by container (keep `type=container` and
   the `event=` filters) and match each line against the watched
   dependency names client-side using the event's own name attribute
   (confirmed present in the PR's own event line format, e.g.
   `container stop <id> (exitCode=..., image=..., name=<name>)`) — no ID
   tracking, and it survives recreation for free.

2. **`WaitForHealthy`** (`src/Wip.Cli/CliContext.cs:1095`) — polls
   `dependencies.<name>.healthcheck` by running `wslc exec <name> <test>`
   itself every `interval` seconds during `wip up`.
   **Not addressable by this PR.** wip evaluates the healthcheck's `test:`
   itself rather than relying on a container-engine-reported health status,
   and `wslc events` has no `health_status` action to subscribe to. This
   loop stays as-is unless/until wslc adds its own health-status event.

## Measured baseline (current polling cost)

`wslc list --all --filter name=<container> --format json` against a running
container, 20 consecutive invocations on the dev machine used for this
investigation:

| metric | value |
|---|---|
| mean | 68.6 ms |
| median | 67 ms |
| range | 65–75 ms |

Projected for a representative `wip up --watch` session — 3 dependencies,
default `--interval 5`, running for 1 hour:

| | polling (today) | `wslc events` (once available) |
|---|---|---|
| subprocess spawns | 720 ticks × 3 = 2,160 | 1 (long-lived) |
| cumulative spawn overhead | 2,160 × ~70ms ≈ 151s | ~0 |
| exited-detection latency | avg 2.5s, worst case 5s (next tick) | sub-second (push on event arrival) |

Spawn count and cumulative overhead scale as
O(dependencies × watch duration / interval) today; with events it's O(1)
regardless of dependency count or how long `--watch` runs. The latency win
(worst case 5s → near-instant) matters more in practice than the raw CPU
saved.

## Open questions for implementation (once the command ships)

- Exact multi-`container=` filter semantics (OR'd in one stream, or does each
  filter value need its own subprocess?) — determines whether `--watch` can
  use one `wslc events` process for all dependencies or needs one per
  dependency.
- Whether to detect `wslc events` support at runtime (e.g. probe
  `wslc system events --help` or gate on a minimum `wslc version`) and fall
  back to the current polling loop on older installs, since wip must keep
  working against whatever WSLC build the user already has.
- Whether a dropped/gapped event stream (session ring buffer overflow) should
  fall back to a one-shot `wslc list` reconciliation before resuming the
  subscription, so a missed event can't leave `--watch` in a stale view
  forever.
- Re-measure `wslc events` subprocess startup + first-event latency once it's
  actually available, to replace the projected numbers above with real ones.

## Next step

Watch for a WSL release whose tag postdates the `214bcad` merge commit on
`microsoft/WSL`. Once one ships, redo the `wslc system --help` /
`wslc events --help` check, then start on the `WatchRestarts` replacement
described above.
