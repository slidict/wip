# wip vs Docker Desktop benchmark — results

Generated: 2026-09-15T08:09:14+09:00

**Run type:** pilot (not the full protocol in `SKILL.md`).

## Summary (round 1)

| Metric | windows-docker | windows-wip | wsl-docker | wsl-wip |
|---|---|---|---|---|
| Infra start → ready | 13.07 s | 3.98 s | 14.48 s | 4.05 s |
| App start → HTTP ready | not ready (60 s timeout) | 0.28 s | 0.26 s | 0.25 s |
| Load throughput (30 s, concurrency 20) | not measured | 13,687 req/s | 9,124 req/s | 13,769 req/s |
| Load error rate | not measured | 0% | 0% | 0% |

## Storage

512 MB written then deleted, one dedicated test volume per backend. Single measurement.

| | Docker Desktop (`docker_data.vhdx`) | WSLC (`storage.vhdx`) |
|---|---|---|
| Before creation | 258.87 GiB | 151.05 GiB |
| After writing 512 MB | 258.87 GiB | 151.08 GiB (+32 MiB) |
| After deleting the test volume | 258.87 GiB | 151.08 GiB |
| After the reclaim attempt | 258.87 GiB | 151.05 GiB (‑31 MiB) |

Full detail in `storage.csv`.

## Per-config detail

| config | round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|
| windows-wip | 1 | 3983.1 | 314.5 | 276.6 | True | 266.2 | 13687.33 | 0 | 1.2968 | 2.2907 | |
| wsl-wip | 1 | 4049.9 | 4768.8 | 248.4 | True | 2875.2 | 13768.68 | 0 | 1.3156 | 2.1972 | |
| windows-docker | 1 | 13065.5 | 632.0 | 62046.6 | False | 510.7 | | | | | app never became http-ready |
| windows-docker | 2 | 10663.9 | 703.8 | 284.3 | True | 545.7 | 6480.12 | 0 | 0.694 | 1.1584 | |
| windows-docker | 3 | 12417.2 | 641.4 | 19.1 | True | 487.4 | 7379.30 | 0 | 0.5967 | 1.0288 | |
| windows-docker | 4 | 10079.4 | 625.8 | 18.3 | True | 457.5 | 7432.97 | 0 | 0.5924 | 1.0217 | |
| windows-docker | 5 | 10580.5 | 629.1 | 20.4 | True | 497.7 | 6487.22 | 0 | 0.6971 | 1.1349 | |
| wsl-docker | 1 | 14481.2 | 691.5 | 258.1 | True | 573.6 | 9123.996 | 0 | 2.0952 | 2.9704 | |
| wsl-docker | 2 | 12640.9 | 740.2 | 20.4 | True | 553.0 | 7132.07 | 0 | 0.6356 | 1.0294 | |
| wsl-docker | 3 | False | 158.7 | 0 | False | 106.9 | | | | | infra failed to become ready; Cannot connect to the Docker daemon |
| wsl-docker | 4 | 11872.4 | 834.8 | 21.4 | True | 604.1 | 6980.82 | 0 | 0.6451 | 1.0582 | |
| wsl-docker | 5 | 13350.2 | 721.2 | 21.9 | True | 589.9 | 7196.20 | 0 | 0.5981 | 1.1062 | |

See `results.csv` / `samples.csv` for full raw data, `SKILL.md` for the protocol.
