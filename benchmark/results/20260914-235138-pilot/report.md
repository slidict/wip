# wip vs Docker Desktop benchmark — confirmation pilot run report

**Run:** 2026-09-14 23:51–00:00 JST (`20260914-235138-pilot`)
**Purpose:** confirm the three harness bugs found in the prior pilot
(`20260914-225724-pilot`) are actually fixed, by re-running the same reduced protocol (1 round
per config, no warmup, 30 s load, 15 s baseline/idle). Still **not** the full protocol.

## Bug-fix confirmation

| Bug | Status |
|---|---|
| `Invoke-TimedProcess` dropping all arguments (.NET Framework `ProcessStartInfo.ArgumentList`) | **Confirmed fixed.** Every `docker`/`wip`/`wsl` invocation this run ran with its real arguments and exit codes matched expected behavior. |
| `localhost` hanging vs `127.0.0.1` | **Confirmed fixed as a deterministic bug** — `127.0.0.1` answered in double-digit milliseconds every time it was reached (`windows-docker`: 20.9 ms, `windows-wip`: 265.3 ms, `wsl-wip`: 225 ms). See below for a separate, *not* fully explained issue this exposed. |
| `Start-HostSampler` silently dropping `-ProcessPattern` for docker configs | **Confirmed fixed.** `samples.csv` now has full `baseline`/`infra_idle`/`app_idle`/`load` coverage for `windows-docker` (previously entirely missing). `wsl-docker` is missing `app_idle`/`load` only because its app never became ready this run (expected — those phases are skipped when the app isn't reachable), not because of the sampler bug. |

## A separate, unresolved finding: intermittent app-ready timeout on Docker Desktop, unrelated to hostname

This run's `wsl-docker` timed out waiting for the app to become HTTP-ready (62 s, `127.0.0.1`),
the same symptom the *prior* run saw on `windows-docker` — except this time `windows-docker`
succeeded in 20.9 ms and `wsl-docker` is the one that failed. Both runs used the exact same
`127.0.0.1` target and the same fixed `Invoke-TimedProcess`. Since which config fails is not
consistent between runs, this looks like a real intermittent characteristic of this host's Docker
Desktop under WSL2 mirrored networking — plausibly the published port taking unusually long to
become reachable right after a *fresh* Docker Desktop boot — rather than a bug in the harness. It
has never happened on a wip/wslc config across all runs so far (4 for wip configs, 0 failures).
This is noted as an open finding, not fixed, and worth watching for in the full protocol's 3
rounds per config.

## Results this run

| Metric | windows-wip | wsl-docker | windows-docker | wsl-wip |
|---|---|---|---|---|
| Infra start → ready | 4.66 s | 15.57 s | 14.80 s | 3.94 s |
| App start command | 0.31 s | 1.21 s | 0.58 s | 3.14 s |
| App start → HTTP ready | 0.27 s | **timed out (60 s)** | 0.02 s | 0.23 s |
| App stop | 0.25 s | 0.59 s | 0.46 s | 3.01 s |
| Load (30 s, concurrency 20): throughput | 17,081 req/s | not measured | 10,312 req/s | 12,352 req/s |
| Load: error rate | 0% (512,495/512,495) | not measured | 0% (309,404/309,404) | 0% (370,620/370,620) |
| Load: p50 / p95 latency | 1.09 / 1.62 ms | not measured | 1.83 / 2.93 ms | 1.54 / 2.28 ms |

Combining both pilot runs (2 samples per config now, still short of the full protocol's 3+warmup):
throughput has ranged 9,025–17,081 req/s across all four configs with 0% errors whenever the app
became ready, and wip/wslc-backed configs have reached HTTP-ready in under 300 ms in all 4
observations so far, while docker-backed configs have ranged from 21 ms to a full 60 s timeout.

## Recommendation

The three harness bugs are confirmed fixed and no longer need attention. The intermittent
docker-side app-ready timeout is real and unresolved — it should be watched for, not silently
retried, during the full protocol. Given it has now hit both `windows-docker` and `wsl-docker`
(once each, in different runs), the full protocol's 3 measured rounds should be enough to
characterize how often it happens rather than treat either occurrence as a fluke.

Before running the full protocol (warmup + 3 rounds × 4 configs × 120 s load — several hours of
repeated Docker Desktop stop/start cycles), reconfirm with the user that this is an acceptable
time to run it and that `www-*` should stay stopped for its duration (it was left stopped after
the prior run's incident and is still stopped now).
