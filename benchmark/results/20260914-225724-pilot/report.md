# wip vs Docker Desktop benchmark — pilot run report

**Run:** 2026-09-14 22:57–23:34 JST (`20260914-225724-pilot`)
**Scope:** small-scale pilot to validate the benchmark harness (scripts, timing, sampling), per the
protocol in `.claude/skills/wip-benchmark/SKILL.md`. **Not** the full protocol (1 round per config, no
warmup, 30 s load instead of 120 s, 15 s baseline/idle instead of 60 s, no storage phase). Numbers
below describe this one machine, this one run, under this reduced protocol — they are not a general
claim about either product's performance.

## Incident during this run (read before trusting anything else)

While iterating on the harness, its "stop both backends to reach a clean baseline" step force-killed
the Docker Desktop process between rounds. This machine has an unrelated, real project running under
Docker Desktop (`www-vue2-1`, `www-vue3-1`, `www-rails-1`, `www-sidekiq-1`, `www-https-portal-1`,
`www-redis-1`, `www-db-1` — Rails + two Vue frontends + Postgres + Redis + an https-portal). The
initial environment check only looked at what was *currently running* (nothing, since Docker Desktop
hadn't been launched yet) and never checked for containers that would auto-restart once Docker
Desktop came back up. `www-db-1` (Postgres) and `www-redis-1` and `www-https-portal-1` were hard-killed
and auto-restarted twice each before this was caught. Postgres completed WAL crash-recovery cleanly
both times ("REDOは必要ありません") and both containers were verified healthy afterward
(`pg_isready`, `redis-cli PING`) — no data loss was observed — but they were still killed
ungracefully, not stopped cleanly. `www-vue2-1`/`www-vue3-1`/`www-rails-1`/`www-sidekiq-1` were
already `Exited (255)` before this run and were not further affected.

Fixes applied as a direct result:
- `Stop-DockerDesktopInfra` now tries a graceful `CloseMainWindow()` quit (up to 45s) before
  falling back to `Stop-Process -Force`, so Docker Desktop's own shutdown sequence gets a chance to
  stop containers cleanly first.
- Before any future run touching Docker Desktop, run `docker ps -a` (not just `docker ps`) to see
  what would come back, and get explicit confirmation before proceeding if anything unrelated is
  present — this is what `SKILL.md` already said to do and this run skipped.

At the user's instruction, the `www-*` containers were left stopped after this run rather than
restarted, to be resumed manually by the user.

## Harness bugs found and fixed during this pilot

This was the harness's first real execution, and three bugs surfaced and were fixed in place:

1. **`Invoke-TimedProcess` silently dropped all arguments.** Windows PowerShell 5.1 runs on .NET
   Framework, where `ProcessStartInfo.ArgumentList` exists but is never auto-instantiated (unlike
   .NET Core), so `$psi.ArgumentList.Add(...)` threw "cannot call a method on a null-valued
   expression" for every argument, and the commands actually ran with none. This made `wip up -d`
   run bare `wip` (print help, do nothing) and `wsl -d Ubuntu -- bash -lc "..."` run bare `wsl`
   (harmless, but not the intended command) — both reported `exitCode 0` because the bare command
   itself succeeded, masking the bug. Fixed by building the classic `Arguments` string instead.
2. **`localhost` hangs on this host; `127.0.0.1` does not.** `docker run`/`wip up` and the container
   itself all worked correctly, but `Invoke-WebRequest -Uri http://localhost:18080/` timed out
   consistently while `http://127.0.0.1:18080/` answered in under 150 ms and a raw TCP test to
   `localhost:18080` succeeded. Root cause not fully isolated (plausibly `localhost` resolving `::1`
   first and IPv6 loopback traffic stalling under WSL2's mirrored networking mode), but the fix —
   target `127.0.0.1` explicitly everywhere — is confirmed to work. This one cost the `windows-docker`
   row's app-ready measurement in the run below (see caveats).
3. **`Start-HostSampler` dropped the whole `-ProcessPattern` argument for docker configs.**
   `Start-Process -ArgumentList` does not auto-quote array elements containing spaces (unlike
   `ProcessStartInfo.ArgumentList` under .NET Core). The docker-side pattern
   (`'Docker Desktop|com\.docker'`) contains a space; the wip-side pattern does not — so the sampler
   silently failed to bind `-ProcessPattern` and exited without writing any rows, but only for the
   two docker configs. This is why `samples.csv` below only has `windows-wip`/`wsl-wip` data. Fixed
   by quoting every `Start-Process -ArgumentList` element; verified with a standalone test after the
   fix (confirmed writing rows for a docker-pattern config), but **not** re-verified inside a full
   run — see caveats.

## Environment

See `environment.json` for full detail. Summary:

| | |
|---|---|
| OS | Windows 11 Pro 10.0.26200.9445 |
| CPU | Intel Core Ultra 7 155H (16 cores / 22 logical) |
| RAM | 63.46 GB total; `.wslconfig`: memory=45GB, processors=10, networkingMode=mirrored |
| WSL | 2.9.4.0, kernel 6.18.35.2-1 |
| WSL distro used for wsl-* configs | `Ubuntu` (26.04 "Resolute Raccoon") — chosen by the user over the default `Ubuntu-22.04` |
| Docker Desktop | 29.7.2, WSL2 backend, containerd snapshotter, WSL integration enabled for both `Ubuntu` and `Ubuntu-22.04` |
| wip / wslc | wip 2.5.2, wslc 2.9.4.0 |
| Benchmark app | fixed-response Node.js HTTP server, no dependencies, `node:22-alpine`, same Dockerfile built once for both engines ahead of any timed phase |
| Load generator | custom Node.js script (no dependencies), always run from the Windows host PowerShell process regardless of config |
| Source location | `benchmark/app` on NTFS, referenced as `C:\...\benchmark\app` (Windows configs) or `/mnt/c/.../benchmark/app` (wsl configs) — same physical directory in both cases |

## Results

Per `SKILL.md`, the primary comparison is Docker Desktop vs. wip/WSLC within each shell, then the
same backend across shells.

### Within Windows PowerShell: Docker Desktop vs. wip/WSLC

| Metric | windows-docker | windows-wip |
|---|---|---|
| Infra start → ready | 13.08 s | 4.82 s |
| App start command | 0.62 s | 0.34 s |
| App start → HTTP ready | **never ready (60 s timeout — known harness bug, see caveats)** | 0.23 s |
| App stop | 0.41 s | 0.25 s |
| Load (30 s, concurrency 20): throughput | not measured (app never became ready) | 12,927 req/s |
| Load: error rate | not measured | 0% (387,872 / 387,872) |
| Load: p50 / p95 latency | not measured | 1.48 ms / 2.24 ms |

### Within WSL Ubuntu bash: Docker Desktop vs. wip/WSLC

| Metric | wsl-docker | wsl-wip |
|---|---|---|
| Infra start → ready | 15.18 s | 4.85 s |
| App start command | 1.13 s | 3.25 s |
| App start → HTTP ready | 0.27 s | 0.24 s |
| App stop | 0.55 s | 2.75 s |
| Load (30 s, concurrency 20): throughput | 9,025 req/s | 15,515 req/s |
| Load: error rate | 0% (270,821 / 270,821) | 0% (465,517 / 465,517) |
| Load: p50 / p95 latency | 2.10 ms / 2.97 ms | 1.18 ms / 1.91 ms |

### Same backend, across shells

| Metric | Docker: windows vs wsl | wip: windows vs wsl |
|---|---|---|
| Infra start | 13.08 s (windows) vs 15.18 s (wsl) | 4.82 s (windows) vs 4.85 s (wsl) |
| App start command | 0.62 s (windows) vs 1.13 s (wsl) | 0.34 s (windows) vs 3.25 s (wsl) |
| App stop | 0.41 s (windows) vs 0.55 s (wsl) | 0.25 s (windows) vs 2.75 s (wsl) |
| Load throughput | n/a vs 9,025 req/s | 12,927 req/s (windows) vs 15,515 req/s (wsl) |

The wip `app start command` and `app stop` gap between windows (0.34 s / 0.25 s) and wsl (3.25 s /
2.75 s) is large relative to everything else in this table and is based on a single measurement each
— it is exactly the kind of number the full protocol's 3-round design exists to check for noise
before it gets treated as real. Do not read anything into it yet.

### Host CPU/memory (only available for wip configs — see harness bug #3 above)

| Phase | windows-wip avg CPU% | windows-wip avg used mem | wsl-wip avg CPU% | wsl-wip avg used mem |
|---|---|---|---|---|
| baseline (both backends stopped) | 2.24% | 39,364.8 MB | 1.65% | 38,144.0 MB |
| load (20 concurrent, 30s) | 15.23% | 38,749.6 MB | 13.31% | 39,055.8 MB |

`windows-docker`/`wsl-docker` host samples are missing from this run entirely (bug #3); the fix has
been verified standalone but not inside a full run yet.

## Known issues / what this pilot does not tell you

- **Single round, no warmup.** Every number above is one measurement. The full protocol's warmup +
  3 rounds, config order randomized per round, exists specifically to catch flakes like the
  `windows-docker` app-ready timeout below — this pilot cannot distinguish a real effect from noise.
- **`windows-docker` app-ready result is not trustworthy.** It hit the exact `localhost` bug
  described above (fixed for future runs) or a similar one-off stall — isolated manual retests
  immediately before and after this run both succeeded in ~450 ms against the identical image and
  command. Needs a clean re-run to confirm the fix actually resolves it in-harness, not just in an
  isolated test.
- **Docker-side CPU/memory samples are missing** for this run (bug #3, fixed but not re-verified in
  a full run).
- **Storage phase not run.** Not part of this pilot's scope.
- **`related_process_ws_mb` in `samples.csv` is a naive sum of matching processes' working sets** —
  it double-counts shared memory and does not include the WSL2 VM's own memory footprint
  (`vmmem`/`vmmemwslc-*` processes are included in the *pattern* but Working-Set accounting for a
  VM process does not represent the VM's guest-side usage). Treat it as a rough relative signal, not
  an absolute container memory figure, per `SKILL.md`'s own caution against double-counting.
- **This machine had Docker Desktop's WSL integration enabled for both Ubuntu distros already**,
  and starting Docker Desktop also starts `Ubuntu` and `Ubuntu-22.04` themselves (confirmed via
  `wsl --list --verbose`) — so a `wsl-docker` "infra start" partly measures Docker Desktop's own
  distro-integration bootstrap, not a `wsl-docker`-specific cost. This is real, current product
  behavior, not a test artifact, but worth stating explicitly since it affects how "infra start" is
  interpreted for that one config.

## Recommended next step

Re-run the pilot once more (still 1 round, short durations) to confirm all three harness fixes hold
together in a single unattended run — specifically that `windows-docker` reaches ready and that
`samples.csv` gets rows for all four configs — **after** confirming with the user that `www-*` is in
a state where Docker Desktop can be safely cycled again (it is currently stopped, left that way at
the user's request). Only after that clean confirmation run should the full protocol (warmup + 3
rounds, 120 s load, storage phase) be scheduled, as its own multi-hour, multi-cycle run.
