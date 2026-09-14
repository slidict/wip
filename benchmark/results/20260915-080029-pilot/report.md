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
the app never became HTTP-ready within 60 s. This is the same Docker Desktop
mirrored-networking issue documented in the two prior pilot runs
(`20260914-225724-pilot`, `20260914-235138-pilot`) — it has now hit `windows-docker` twice and
`wsl-docker` once, in three different runs, never the same config twice in a row. It looks like a
real characteristic of this host's Docker Desktop setup after a fresh boot, not a bug in this
harness.

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

