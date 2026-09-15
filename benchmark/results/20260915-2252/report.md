# wip vs Docker Desktop benchmark — results

Generated: 2026-09-16T07:45+09:00 (main run: 2026-09-15 22:52–2026-09-16 00:15 JST; wsl-docker round 3 captured in a separate supplemental run 2026-09-16 07:33–07:39 JST — see Limitations)

## Environment

- Host: Windows 11 Pro 10.0.26200, 12th Gen Intel Core i7-12700KF (12 cores / 20 logical), 63.86 GB RAM
- WSL: version 2.9.12.0, kernel 6.18.40.1-1, Windows 10.0.26200.9445 (WSLg 1.0.79)
- Docker: 29.8.0 (client/server), context `desktop-linux`
- wip: 2.5.2, repo commit `20897a0`
- App image: `wip-bench:latest` (sha256:06d35d4e…), same image/config used for all four configs
- Load: 1 warmup + 3 measured rounds per config, execution order randomized per round (see `run.log` for the exact order each round), concurrency 20, 120 s per load phase, 60 s each for baseline/infra-idle/app-idle
- All source/app data on the Windows filesystem (`C:\Users\Yusuke\codes\wip\benchmark\app`), referenced from WSL as `/mnt/c/...`

## Load & timing results

Each table: per-round measured values (warmup excluded), then median/min/max across the 3 rounds.

### windows-docker

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 4994.2 | 408.3 | 228.5 | True | 362.1 | 19124.76 | 0 | 1.0484 | 1.3995 |  |
| 2 | 3567.7 | 416.9 | 233.8 | True | 385.7 | 19256.78 | 0 | 1.0388 | 1.4001 |  |
| 3 | 3619 | 453.4 | 230.3 | True | 402.6 | 19920.22 | 0 | 0.9601 | 1.404 |  |

| stat | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms |
|---|---|---|---|---|---|---|---|---|
| median | 3619 | 416.9 | 230.3 | 385.7 | 19256.78 | 0 | 1.0388 | 1.4001 |
| min | 3567.7 | 408.3 | 228.5 | 362.1 | 19124.76 | 0 | 0.9601 | 1.3995 |
| max | 4994.2 | 453.4 | 233.8 | 402.6 | 19920.22 | 0 | 1.0484 | 1.404 |

### windows-wip

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1716 | 1974.5 | 236.4 | True | 230.4 | 27020.80 | 0 | 0.5481 | 1.3663 |  |
| 2 | 1744.1 | 2150.3 | 237.2 | True | 214.6 | 27896.21 | 0 | 0.5368 | 1.3072 |  |
| 3 | 1694.1 | 2033.4 | 239.3 | True | 208.9 | 26881.96 | 0 | 0.556 | 1.4173 |  |

| stat | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms |
|---|---|---|---|---|---|---|---|---|
| median | 1716 | 2033.4 | 237.2 | 214.6 | 27020.80 | 0 | 0.5481 | 1.3663 |
| min | 1694.1 | 1974.5 | 236.4 | 208.9 | 26881.96 | 0 | 0.5368 | 1.3072 |
| max | 1744.1 | 2150.3 | 239.3 | 230.4 | 27896.21 | 0 | 0.556 | 1.4173 |

### wsl-docker

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 8114.1 | 703.5 | 236.6 | True | 691.3 | 18986.23 | 0 | 1.0495 | 1.4165 |  |
| 2 | 10058.9 | 849.2 | 235 | True | 736 | 19029.96 | 0 | 1.0478 | 1.4286 |  |
| 3 | 7567.4 | 1912.6 | 273.5 | True | 857.7 | 20752.63 | 0 | 0.9413 | 1.353 | captured in separate supplemental run 2026-09-16T07:33 (see Limitations) |

| stat | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms |
|---|---|---|---|---|---|---|---|---|
| median | 8114.1 | 849.2 | 236.6 | 736 | 19029.96 | 0 | 1.0478 | 1.4165 |
| min | 7567.4 | 703.5 | 235 | 691.3 | 18986.23 | 0 | 0.9413 | 1.353 |
| max | 10058.9 | 1912.6 | 273.5 | 857.7 | 20752.63 | 0 | 1.0495 | 1.4286 |

Also for wsl-docker: the warmup round (round 0) failed before any of the above — `docker run` hit `Conflict: The container name "/wip-bench" is already in use` from a leftover container from an earlier aborted attempt. It self-resolved from round 1 onward once that leftover was cleared.

### wsl-wip

| round | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_ready_ok | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1743.8 | 7247.2 | 237.7 | True | 4267.3 | 27714.32 | 0 | 0.5466 | 1.2956 |  |
| 2 | 1741.3 | 6474.4 | 236.6 | True | 4668.5 | 28216.61 | 0 | 0.5355 | 1.2779 |  |
| 3 | 1706.5 | 6408.9 | 229.8 | True | 5101.3 | 27854.91 | 0 | 0.5616 | 1.2858 |  |

| stat | infra_start_ms | app_start_cmd_ms | app_ready_ms | app_stop_ms | throughput_rps | error_rate | p50_ms | p95_ms |
|---|---|---|---|---|---|---|---|---|
| median | 1741.3 | 6474.4 | 236.6 | 4668.5 | 27854.91 | 0 | 0.5466 | 1.2858 |
| min | 1706.5 | 6408.9 | 229.8 | 4267.3 | 27714.32 | 0 | 0.5355 | 1.2779 |
| max | 1743.8 | 7247.2 | 237.7 | 5101.3 | 28216.61 | 0 | 0.5616 | 1.2956 |

All 15 completed measured-round trials had `load_error_rate = 0` (all 2.28M–3.39M requests per trial succeeded; request volume differs by config because throughput differs at the same fixed 120 s duration, so it is not itself a fairness problem — same duration, same concurrency, same target URL for every trial).

## Comparison 1: Docker Desktop vs wip, within Windows PowerShell

| metric (median of 3 rounds) | windows-docker | windows-wip |
|---|---|---|
| backend start → ready | 3619 ms | 1716 ms |
| app start → HTTP ready (`app_start_cmd_ms`+`app_ready_ms`) | 647 ms | 2270 ms |
| app stop | 385.7 ms | 214.6 ms |
| throughput | 19257 rps | 27021 rps |
| p50 / p95 latency | 1.04 / 1.40 ms | 0.55 / 1.37 ms |

Backend start is faster for wip; app start is faster for Docker Desktop (its app-start step is a bare `docker run` against an already-built image, wip's is `wip up -d` which does more per-invocation work); once running, wip served roughly 40% higher throughput and about half the p50 latency at the same concurrency.

## Comparison 2: Docker Desktop vs wip, within WSL Ubuntu

| metric (median of 3 rounds) | wsl-docker | wsl-wip |
|---|---|---|
| backend start → ready | 8114 ms | 1741 ms |
| app start → HTTP ready | 1086 ms | 6711 ms |
| app stop | 736 ms | 4668 ms |
| throughput | 19030 rps | 27855 rps |
| p50 / p95 latency | 1.05 / 1.42 ms | 0.55 / 1.29 ms |

Same overall pattern as Windows: wip's backend starts faster, Docker Desktop's app-start/stop is faster, and wip wins on throughput/latency. Absolute backend-start time for docker is much larger from WSL than from Windows (8.1 s vs 3.6 s median) — see Comparison 3.

## Comparison 3: same backend, Windows PowerShell vs WSL Ubuntu as execution origin

| metric (median) | windows-docker | wsl-docker | windows-wip | wsl-wip |
|---|---|---|---|---|
| backend start → ready | 3619 ms | 8114 ms | 1716 ms | 1741 ms |
| app start+ready | 647 ms | 1086 ms | 2270 ms | 6711 ms |
| throughput | 19257 rps | 19030 rps | 27021 rps | 27855 rps |

For Docker Desktop, launching from WSL roughly doubled backend-start time versus launching the identical `docker` CLI from Windows PowerShell (3.6 s → 8.1 s median) — both talk to the same single Docker Desktop engine, so this is the cost of the extra `wsl.exe` hop plus whatever Docker Desktop's WSL integration does on each cold start, not a different engine. For wip, backend-start time was essentially the same from either origin (1.72–1.74 s). App start+ready was consistently slower from WSL for both backends, most dramatically for wip (2.3 s → 6.7 s). Throughput/latency at steady state were within noise of each other regardless of origin (both backends), which is expected since the origin only affects how the container is launched, not the network path the load generator uses once it's up.

## CPU / memory (host-wide, ~1 Hz sampling; medians shown, see `samples.csv` for full series)

Baseline was sampled once per trial (both backends fully stopped) across the whole ~9-hour run window (main run 2026-09-15 22:52–2026-09-16 00:15, plus the supplemental wsl-docker round 3 the next morning at 07:33); pooled across all 16 baseline samples: median host CPU 5.3%, median host memory used 23.09 GB (range 19.52–27.33 GB across the whole run — this range reflects other host activity over many hours, not the containers, per the skill's own caution not to treat this as an exact per-container figure).

| config | phase | cpu_pct median (min–max) | mem_used_mb median (min–max) | mem increase vs pooled baseline |
|---|---|---|---|---|
| windows-docker | infra_idle | 6.04 (0.09–37.67) | 26328 (25456–26693) | +3242 |
| windows-docker | app_idle | 5.51 (0.30–27.62) | 26367 (25712–26963) | +3280 |
| windows-docker | load | 43.99 (4.46–62.72) | 26369 (23877–27076) | +3282 |
| windows-wip | infra_idle | 5.73 (0.67–25.78) | 21048 (20341–25321) | −2039 |
| windows-wip | app_idle | 5.56 (0.26–25.82) | 21962 (21819–22634) | −1125 |
| windows-wip | load | 20.49 (5.04–35.62) | 22122 (21925–22937) | −964 |
| wsl-docker | infra_idle | 5.40 (0.09–26.44) | 27789 (23351–28164) | +4703 |
| wsl-docker | app_idle | 5.43 (0.92–18.80) | 27674 (23116–28071) | +4587 |
| wsl-docker | load | 43.92 (4.34–54.18) | 24099 (19480–27406) | +1013 |
| wsl-wip | infra_idle | 5.26 (0.61–15.04) | 21091 (20320–25255) | −1996 |
| wsl-wip | app_idle | 5.17 (0.37–14.28) | 24365 (23309–25776) | +1279 |
| wsl-wip | load | 21.03 (3.99–35.34) | 22192 (21889–24264) | −895 |

Under load, both docker configs drove host CPU to ~44% median vs ~20–21% for both wip configs — Docker Desktop's own VM/engine overhead appears to cost roughly double the host CPU of wip/WSLC at the same request volume-ish load (throughput differed by config so this is not a perfectly matched comparison — see Load & timing section). Memory deltas vs. the pooled baseline are noisy (some negative, which is not physically an actual decrease — it reflects that baseline itself fluctuated ±4 GB over the 9-hour window from unrelated host activity) and should be read as "docker configs used noticeably more resident memory than wip configs while idle/under load in this run," not as precise per-container attribution.

## Storage

Not measured. The skill separates storage measurement into its own phase (`Measure-Storage.ps1`) and this run only covered timing/CPU/memory/load; no storage-phase run was performed.

## Limitations

- **wsl-docker round 3 is not from the same continuous run as the other 15 trials.** The original run's wsl-docker round 3 hung for 37+ minutes on Docker Desktop startup (`starting infra (docker)`, no other trial in the same run took more than ~10 s at that step) and the underlying PowerShell process was killed when the Claude Code session it ran under was interrupted, before any diagnosis completed or `report.md` was written. It was re-measured ~7 hours later (2026-09-16 07:33–07:39 JST) as a standalone single-round run against the same image/config, and merged into `results.csv`/`samples.csv` here with `round` relabeled from 1→3 and a note on the row recording this. Its host CPU/memory context (ambient host state 7 hours later, on system restart) is not the same ambient conditions as the other 15 trials, and it is the only row not covered by the original run's `environment.json` capture window.
- **wsl-docker's warmup round failed** (leftover container name conflict) and was not retried — it's excluded from all tables above (as is every warmup, by design).
- The initial full run needed Node.js installed mid-session (the load generator, `load-gen.mjs`, requires `node`); one earlier attempt failed entirely before that was fixed, and is not included in any of the numbers above.
- Baseline host CPU/memory was pooled across trials spanning ~9 hours (with one trial 7 hours after the rest) rather than a single fixed baseline window, so "increase over baseline" figures carry more ambient-noise risk than a same-session run would.
- Single machine, single run (no repeat of the full 1 warmup + 3 round protocol beyond what's described above) — throughput/latency numbers are consistent to within ~5% across the 3 real rounds per config, which is reassuring, but this is still one measurement campaign on one PC, not a statistically repeated study.
- No storage-phase measurement was performed (see Storage section).
