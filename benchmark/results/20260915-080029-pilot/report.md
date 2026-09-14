# wip vs Docker Desktop benchmark — results

Generated: 2026-09-15T08:09:14.0798743+09:00

**Run type:** pilot (1 round per config, no warmup, 30 s load, 15 s baseline/idle) — not the full
protocol (warmup + 3 rounds, 120 s load) from `SKILL.md`. This is the first run where all four
configs completed in a single unattended pass, after fixing all 30 findings from PR #187's
automated review (safety gates against force-cycling a backend with foreign containers, WSLC
readiness polling, async I/O, path escaping, CSV/locale fixes, and more — see that PR / the
`fix(benchmark)` commit for detail).

## Summary

| Metric | windows-docker | windows-wip | wsl-docker | wsl-wip |
|---|---|---|---|---|
| Infra start → ready | 13.07 s | **3.98 s** | 14.48 s | **4.05 s** |
| App start → HTTP ready | **never ready (known flake, see below)** | 0.28 s | 0.26 s | 0.25 s |
| Load throughput (30 s, concurrency 20) | not measured | 13,687 req/s | 9,124 req/s | 13,769 req/s |
| Load error rate | not measured | 0% | 0% | 0% |

**windows-docker hit its known intermittent flake again this run**: `docker run` succeeded but
the app never became HTTP-ready within 60 s. The same Docker Desktop mirrored-networking issue
also showed up in two earlier throwaway harness-validation runs (not kept — they predate the
PR #187 fixes this run validates) — across those plus this run, it has hit `windows-docker` twice
and `wsl-docker` once, in three different runs, never the same config twice in a row. It looks
like a real characteristic of this host's Docker Desktop setup after a fresh boot, not a bug in
this harness.

**wsl-docker succeeded end-to-end for the first time**, validating the `Wait-WslDockerReady` fix:
a previous run found that `docker.sock` inside the WSL distro can lag behind `docker info`
succeeding on the Windows side right after a fresh Docker Desktop start, so `docker run` from WSL
failed even though the engine was already up per Windows. This run's infra-start step polled from
inside the distro and correctly waited that out.

Across all three pilot runs so far, wip/wslc-backed configs (`windows-wip`, `wsl-wip`) have
reached HTTP-ready in under 300 ms every single time (6/6 observations), while docker-backed
configs have ranged from ~20 ms to a full 60 s timeout (3 successes, 2 flakes out of 6). This
pattern has now shown up consistently enough across independent runs that it looks like a real
difference in this host's startup reliability, not noise — though only the full protocol's 3
measured rounds per config would make that a confident claim rather than a repeated pilot
observation.

## Follow-up: hunting the windows-docker/wsl-docker flake

After this run, a targeted follow-up tried to reproduce the intermittent app-ready flake with
container diagnostics captured (`docker logs`/`inspect`/`port`) before teardown, instead of
letting `Stop-App` destroy the evidence every time as it had in all prior observations. 4 rounds
each of `windows-docker` and `wsl-docker` were run back-to-back (short durations: 5 s
baseline/idle/load instead of this run's 15/15/30 s — their rows are appended to `results.csv`
above reusing round numbers 1–4, so treat those as a separate, differently-parameterized batch,
not more samples of this run's own rounds).

**7 of 8 succeeded.** The one failure was `wsl-docker` round 2, but not the symptom being hunted —
it failed earlier, at the infra-readiness step: `Wait-WslDockerReady` (30 s timeout) never saw
`docker info` succeed inside the WSL distro, so `infra_start_ready=False`, and the subsequent
`docker run` correctly failed with "Cannot connect to the Docker daemon at
unix:///var/run/docker.sock". No `diag-*.log` was produced, because the new diagnostic capture
only triggers when the *app* fails to become ready after a *successful* start — an infra-level or
start-level failure like this one falls outside what it currently watches for. That gap is itself
a finding: **the diagnostics feature needs to also fire on `infra_start_ready=false` or
`app_start_cmd_ok=false` for docker configs**, not just on a successful start followed by a failed
readiness probe.

This is still useful evidence, though: it's a direct, mechanical symptom (the WSL-side
`docker.sock` unreachable for >30 s after a fresh Docker Desktop boot) rather than an unexplained
HTTP timeout, and it's consistent with a theory raised earlier — that Docker Desktop's networking
subsystem under WSL2 mirrored mode has a variable, sometimes long initialization window after
`docker info` first succeeds, and that this affects *both* the WSL-side `docker.sock` proxy
(seen here) and the Windows-side HTTP port-forwarding for published container ports (seen in this
run's own `windows-docker` row, and in earlier pilots). 7/8 quick back-to-back successes here vs.
a flake in nearly every pilot run so far also suggests the failure rate may depend on how "cold"
Docker Desktop's start is — repeated starts within one session, close together, look more reliable
than this pilot's every-round full stop/restart cycle. Not confirmed; worth designing a future run
around specifically (e.g. compare a cold first-of-the-day start against a warm repeat).

## Storage

Ran after the timing/load phase above, per `SKILL.md`'s "treat storage as a separate phase" —
`Measure-Storage.ps1`, one dedicated test volume per backend, 512 MB written then deleted, at the
four points `SKILL.md` defines. Single measurement each, not repeated — treat as one data point,
not a trend. Full detail in `storage.csv`; free-space deltas below also include whatever the
preflight image pull (`alpine:latest`) and ordinary host activity added, not just the test data.

| | Docker Desktop (`docker_data.vhdx`) | WSLC (`storage.vhdx`) |
|---|---|---|
| Before creation | 258.87 GiB | 151.05 GiB |
| After writing 512 MB (logical usage inside container: `512.0M`) | 258.87 GiB (unchanged) | 151.08 GiB (**+32 MiB**) |
| After deleting the test volume | 258.87 GiB (unchanged) | 151.08 GiB (unchanged) |
| After the reclaim attempt* | 258.87 GiB (unchanged) | 151.05 GiB (**‑31 MiB**, back to ~1 MiB above the original) |

\* Neither reclaim attempt ran `Optimize-VHD` (needs the Hyper-V PowerShell module and
elevation, not assumed available — see `Measure-Storage.ps1`'s header). For Docker Desktop, the
reclaim attempt is just a full stop; for WSLC, it's `wslc system session terminate`. WSLC's disk
size tracks the write/delete almost exactly (grew by 32 MiB writing 512 MB, shrank by 31 MiB after
the session ended) — a real, small, automatic reclaim, seemingly in ~32 MiB blocks. Docker
Desktop's `docker_data.vhdx` showed **no byte-level change at all** across write, delete, or
reclaim in this one run; whether that means it doesn't reclaim under this backend the way WSLC's
volume did, or the file's existing slack space simply absorbed a 512 MB write without needing to
grow, isn't distinguishable from a single measurement — worth a repeat with a larger write size to
tell those apart.

## Per-config detail (auto-generated)

Auto-generated from `results.csv` by `Invoke-Benchmark.ps1`. Each row is one measured trial (warmup rounds excluded). See `samples.csv` for the CPU/memory time series and `SKILL.md` for the protocol.

## windows-docker

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 13065.5 | 632 | 62046.6 | False | 510.7 |  |  |  |  | app never became http-ready;  |

## windows-wip

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 3983.1 | 314.5 | 276.6 | True | 266.2 | 13687.329200826502 | 0 | 1.2968 | 2.2907 |  |

## wsl-docker

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 14481.2 | 691.5 | 258.1 | True | 573.6 | 9123.996134492985 | 0 | 2.0952 | 2.9704 |  |

## wsl-wip

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 4049.9 | 4768.8 | 248.4 | True | 2875.2 | 13768.679597413851 | 0 | 1.3156 | 2.1972 |  |

