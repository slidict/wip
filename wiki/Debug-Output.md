# Debug Output

`--debug` (or `WIP_DEBUG=1`) makes wip narrate what it's doing and how long each step took. It's
the main tool for answering "why is this slow?" — particularly for telling wip's own overhead apart
from time spent inside the container.

## Turning it on

```bash
wip rails c --debug
WIP_DEBUG=1 wip rails c
```

## What it prints

```console
$ wip rails c --debug
wip: [debug] running: wslc.exe exec -it -w /app -e RAILS_ENV=*** app bin/rails c
+ wslc.exe exec -it -w /app -e RAILS_ENV=*** app bin/rails c
...
wip: [debug] done in 4.32s: running: wslc.exe exec -it -w /app -e RAILS_ENV=*** app bin/rails c
```

| Line | Meaning |
|---|---|
| `wip: [debug] checking: …` | an existence probe (network, container, dependency) |
| `wip: [debug] running: …` | the command wip is about to run |
| `+ …` | the runner echoing the command as it spawns it |
| `wip: [debug] still running (…): …` | a periodic host resource snapshot |
| `wip: [debug] done in N.NNs: …` | that step finished |

Environment values are masked (`-e KEY=***`) so a debug paste doesn't leak credentials. Other flags
are printed verbatim — see [Secret Masking](Secret-Masking).

## Reading the timings

For long-running interactive commands (`rails console`), the `done in …` line only prints after you
**exit**. So it doesn't tell you how long startup took. What does: the timestamp of the `+ …` line.
Everything before it is wip's own setup; everything after is `wslc` and the container.

```
[wip parses config, probes containers]  ← wip overhead
+ wslc.exe exec …                       ← handoff
[image pull, container boot, app boot]  ← wslc + your app
```

If the `+` line appears instantly and you still wait two minutes, the time is not wip's.

## Resource snapshots

While a step is still running, wip prints a host snapshot every 5 seconds:

```console
wip: [debug] still running (load 3.42 2.10 1.05 | mem 6.1G/15.6G | io read 12000KB/s write 400KB/s | top: wslc.exe(8842) cpu 61.0%/mem 3.2%, ruby(9001) cpu 4.0%/mem 1.1%): running: wslc.exe exec …
```

| Field | Source | What it tells you |
|---|---|---|
| `load` | `/proc/loadavg` | 1/5/15-minute load average |
| `mem` | `/proc/meminfo` | used / total, derived from `MemAvailable` |
| `io` | `/proc/diskstats` | read/write KB/s since the previous snapshot |
| `top` | `ps -eo pid,pcpu,pmem,comm` | the three highest-CPU processes |

Unavailable fields degrade to `n/a` rather than failing the command.

### The diagnostic pattern that matters

**Low CPU, low memory, low-ish IO, and nothing happening for minutes** is the signature of
bind-mount overhead: a framework doing many small `stat`/`open` calls across virtiofs, each one a
round trip. Almost no data moves, so nothing looks busy — but the process is blocked the whole
time.

That's not a broken app. It's [Fixing a Slow Boot](Fixing-a-Slow-Boot).

Contrast:

| Snapshot pattern | Likely cause |
|---|---|
| low CPU, low IO, long duration | bind-mount round trips → add `sync:` |
| high `io read` sustained | pulling an image, or a genuinely large copy |
| high CPU on `wslc.exe` | the VM is doing real work — probably fine |
| high memory near total | the host is swapping |

## Where snapshots go

By default, wip decides based on whether the command owns your terminal:

| Command | Destination |
|---|---|
| Interactive (`-it`) | a temp log file; the path is printed once |
| Non-interactive | inline on stderr |

```console
wip: [debug] command owns the terminal; streaming resource snapshots to /tmp/wip-debug-20260810.log
```

Interactive children control the terminal in raw mode; writing snapshots into it would garble both
outputs. Override with `--debug-log`:

```bash
wip rails c --debug --debug-log=-                     # force inline
wip rspec --debug --debug-log=/tmp/wip-debug.log      # force to a file
```

The file is opened in append mode, so repeated runs accumulate. See
[Global Options](Global-Options).

## Error hints

Independently of `--debug`, when a command fails wip inspects its output and prints a hint if it
recognizes the failure:

| Detected | Hint page |
|---|---|
| `pull access denied`, `insufficient_scope`, `authorization failed` | [Registry Authentication](Registry-Authentication) |
| `no matching manifest for linux/amd64\|arm64` | [Architecture Mismatch](Architecture-Mismatch) |
| `0x8007000e`, "too many mounted volumes" | [Volume Limit Reached](Volume-Limit-Reached) |
| `rsync: not found` and variants | [rsync Not Found](rsync-Not-Found) |

Note: hints require wip to be able to see the output. On native Windows, interactive commands
inherit the real stdio directly, so wip can't observe them and no hint is generated on that path.

## Related

- [Global Options](Global-Options)
- [Fixing a Slow Boot](Fixing-a-Slow-Boot)
- [Reporting Issues](Reporting-Issues) — `WIP_DEBUG=1` output is the single most useful attachment
