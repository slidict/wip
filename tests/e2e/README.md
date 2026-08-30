# End-to-end tests against real WSLC

Everything else in `tests/` runs without WSLC on purpose. The unit and golden suites prove
what wip *would* send to `wslc` — they replay a corpus and compare argv arrays — which is why
they run on Linux and macOS too, and why they stay fast. What they cannot prove is that
`wslc` accepts any of it.

This directory is the other half: `run-e2e.ps1` drives the published `wip.exe` through the
whole lifecycle against real containers and asserts on exit codes and output. A renamed
`wslc` flag, or a different shape for `wslc list --format json`, fails here rather than in
someone's terminal.

**Nothing else depends on it.** The `Test` workflow does not run this, and no test in
`tests/Wip.Tests` reaches WSLC — that separation is the point, not an oversight.

## What it covers

| Step | Asserted |
|---|---|
| preflight | `wslc version`, and `wslc run --rm <base image> echo …` — the environment, before any wip command |
| `wip version` / `wip config` | exit 0; wip resolves `wslc` and reads the fixture |
| `wip build` | exit 0; builds `wip-e2e:latest` from the fixture Dockerfile |
| `wip up -d` | exit 0; `wip ps` reports the container **running** (the state column, not just the name), and `wslc list --all` agrees |
| `wip exec` | exit 0 and the build-time marker comes back; an `interaction:` entry (`wip marker`) reaches the same container |
| `wip exec` (failing command) | a non-zero status inside the container is forwarded as wip's own |
| `wip run` | exit 0, the marker is echoed, and the long-lived container is untouched |
| `wip down` | exit 0; the container is gone from `wslc list --all` |

## Running it locally

Requires Windows with WSL2 and WSLC, and PowerShell 7 (`pwsh`).

```powershell
dotnet publish src/Wip.Cli/Wip.Cli.csproj -c Release -r win-x64 -o artifacts/win-x64
pwsh tests/e2e/run-e2e.ps1 -Wip artifacts/win-x64/wip.exe
```

Useful switches:

- `-BaseImage mirror.example/alpine:3.20` — build on something other than Docker Hub's
  `alpine:3.20`. The script rewrites the `FROM` line in its scratch copy, so the checked-in
  `Dockerfile` must keep that line a plain `FROM <image>`.
- `-KeepWorkspace` — leave the scratch project behind instead of deleting it.
- `-Wslc C:\path\to\wslc.exe` — used for the preflight and for diagnostics only; wip still
  resolves its own `wslc` through `wip.yml`.

The fixture is copied to a scratch directory under `RUNNER_TEMP`/`TEMP` before anything runs,
so a checkout on the WSL filesystem does not reach `wslc` as a UNC path — see
[Running it from a WSL2 shell](../../README.md#running-it-from-a-wsl2-shell).

Cleanup removes the `wip-e2e-app` container whether the run passed or failed, and the
fixture's `command: sleep 600` bounds the container's life even if the script is killed
outright. The `wip-e2e-net` network and the `wip-e2e:latest` image are left in place — both
are reused by the next run, and the CI runner is thrown away regardless.

## In CI

[`.github/workflows/e2e-windows.yml`](../../.github/workflows/e2e-windows.yml) runs it on
`windows-latest`: it publishes `wip.exe`, updates WSL to the pre-release channel that carries
WSLC, verifies `wslc` is on PATH, then runs this script. It runs on every pull request, plus
weekly and on demand. Keeping it out of the `Test` workflow is about that workflow staying
WSLC-free on Linux, not about running this one rarely.

If the runner image has no WSLC, the "Verify wslc is available" step says so in one line
instead of letting a wip command fail for unrelated reasons.

## Adding a case

Keep the fixture minimal: one image, one container, no bind mounts. `volumes:` is left out
deliberately — how `wslc` resolves a `-v` source is still an open question (see the README's
[Known gaps & TODO](../../README.md#known-gaps--todo)), and mixing it in would make a mount
failure look like a lifecycle failure. When that question is settled, a mount case belongs
here as its own step, with its own assertion.
