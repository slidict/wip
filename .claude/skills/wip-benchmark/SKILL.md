---
name: wip-benchmark
description: On a local Windows PC, benchmark Docker Desktop against Wip using PowerShell and WSL Ubuntu as execution origins. Use this when you want to update performance-comparison results, e.g. for presentations.
---

# Wip Benchmark

## Purpose

Compare the following four configurations on the same Windows PC, with the same app and the same
load.

| ID | Command origin | Container backend |
|---|---|---|
| windows-docker | Windows PowerShell | Docker Desktop |
| windows-wip | Windows PowerShell | Wip / WSLC |
| wsl-docker | WSL Ubuntu Bash | Docker Desktop's WSL integration |
| wsl-wip | WSL Ubuntu Bash | Wip / WSLC |

On the WSL side, Docker means Docker Desktop. Do not install a standalone Docker Engine inside
Ubuntu to replace it.

Judge which is better from the results. Do not assume up front that Wip is lighter or faster.

## Checking the environment

Before running anything, check and record the following in the results.

- Windows, WSL, and Ubuntu versions
- CPU and physical memory
- Docker Desktop, Docker Engine, Wip, and WSLC versions
- Docker Desktop's backend
- CPU/memory limits for WSL and for each environment
- Docker context and its target
- The path to the executable each shell actually uses
- Where source code and container data are stored

Confirm how to operate Wip and WSLC using the installed version's `--help` output and the
project's documentation. Do not invent commands or options you have not confirmed exist.

If a required configuration is not available, mark that configuration as not measured and record
why. Do not substitute another configuration's results for it.

## Comparison conditions

- Use the same app, image, configuration, data, and load throughout.
- Reuse an existing benchmark app if one already exists.
- Otherwise, prepare a small HTTP app that returns a fixed response.
- Generate load from one common process on the Windows side.
- Fix concurrency, request count, and measurement duration.
- Measure only one configuration at a time.
- Stop the configuration being compared against, and check for leftover processes.
- If stopping something like a production/business container is required, name the target and
  confirm before doing so.

For the primary comparison, keep the physical storage location of the source the same **and put
it on the Windows filesystem** (referenced from WSL as `/mnt/c/...`, not as a native WSL path like
`~/...`). Wip's bind mounts can silently mount an empty directory when the project isn't actually
reachable the way Wip expects, and `wip doctor` rejects some placements outright — running
`wsl-wip` against a project under WSL's own `~/...` risks comparing against no real data instead
of catching the misconfiguration. Windows and WSL may reference the same Windows-filesystem
location through different path spellings.

If you want to compare the Windows filesystem against the WSL filesystem, treat that as a
separate scenario. Do not change the execution origin and the storage location at the same time
and then attribute the cause to just one of them.

## What to measure

### Time

Measure the following separately.

- From the container backend starting until it becomes usable
- From an already-running backend until the app becomes ready
- Until the app finishes stopping

Determine "ready" by an HTTP response or similar, not merely by the process existing. Finish
pulling images ahead of time; do not let that time count toward app startup time.

If you measure builds, separate the no-cache case from the cached case. If any network fetch time
leaked into a measurement, say so explicitly.

### CPU / memory

Record on the Windows host side, roughly once per second.

- Host-wide CPU usage
- Host-wide physical memory in use
- Usage of related processes/VMs, to whatever extent it can be obtained

For each configuration, measure the following states.

1. Baseline: both container backends fully stopped — the Docker Desktop / WSLC service itself,
   not merely this benchmark's own container removed. Confirm first that nothing else depends on
   either backend still running (see the "production/business container" confirmation rule
   above); stopping the backend service can restart or hard-kill anything else that was using it.
2. Only the target backend started
3. The app started, with no requests being sent
4. The same load being applied

Default durations: 60 seconds each for the baseline, backend-only, and app-idle states, and 120
seconds for the load measurement. If a state has not stabilized, extend it and record how long you
extended it.

Report both the raw host-wide values and the increase over the baseline. Since that increase also
includes fluctuation from other processes, do not describe it as an exact figure for the container
alone.

When summing process memory values, do not double-count shared memory or VM usage.

### Load handling

Record the following alongside resource usage.

- Number of successful requests
- Error rate
- Throughput
- Median and p95 response time

Do not judge one configuration as better than another based solely on lower CPU/memory use when
the amount of work it actually processed differed.

### Storage

Write the same amount of data to a dedicated test volume, and measure at the following points.

1. Before creation
2. After writing the data
3. After deleting the test data/resources
4. After a space-reclamation operation (if one can be performed)

Values to record:

- Logical usage inside the container environment
- The related virtual disk's file size
- The virtual disk's actual allocated size, if obtainable
- Free space on the relevant Windows drive

Distinguish between deleting data and actually returning disk space to the host. Do not judge how
much space was returned from the virtual disk's file size alone.

Do not run a prune across the whole existing environment, and do not perform a deletion that would
sweep up existing data. If space reclamation would affect the existing environment, show that
impact and confirm before proceeding.

## How to run this

Bundle the measurement logic into reusable scripts. If scripts already exist, use them — don't
rewrite them each time.

- PowerShell: host-side measurement on Windows, execution from the Windows side, saving results
- Bash: execution from the WSL Ubuntu side
- Skill: checking conditions, managing execution, interpreting results

Default for startup/CPU/memory/load measurements: one warmup run plus three measured rounds per
configuration. Vary the execution order of the configurations each round, and record that order.

Treat storage measurement as a separate phase. If the starting conditions for each round cannot be
restored, mark it explicitly as a one-off measurement.

Save timeouts and failures as results too — don't keep only the successful runs. After a failure,
investigate the cause before re-running.

## Deliverables

Save the following into a directory named for the run's date and time.

- `environment.json`: environment, configuration, and measurement conditions
- `samples.csv`: time-series data such as CPU and memory
- `results.csv`: each trial's time, load-handling, and storage results
- `report.md`: a comparison table across the four configurations, an explanation of the results,
  and the measurement's limitations

The report should include each trial's individual values as well as the median, minimum, and
maximum. Do not build a combined score across metrics that use different units.

Show the Docker Desktop vs. Wip comparison within Windows and within WSL first. Then show, for the
same container backend, how the execution origin makes a difference.

Mark unmeasured values as "not measured" — do not fill them in with estimates. Describe results as
what was obtained on this PC, with this configuration and this load, this time.

When finished, clean up only the resources this measurement created, and restore any startup state
you changed, to the extent that's possible.
