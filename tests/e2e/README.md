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
| `wip up -d` | exit 0; `wslc list --all` shows the container **running**, and `wip ps` reports the same state (the state column, not just the name) |
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
are reused by the next run, and the CI runner is thrown away regardless. Named sandbox and
volume fixtures use a unique resource namespace; their consumers are inspected for fixture
ownership before removal. Volume finalizers explicitly remove only the declared owned
storage after consumers are gone. Nonzero cleanup fails the run. Failed runs keep the scratch
configuration/journal for recovery, even without `-KeepWorkspace`.

## The terminal half

`pty-session.ps1` covers what only exists when a terminal does: whether the container's
shell sees a tty, the size it reads, whether a resize reaches it, and whether Ctrl-C arrives
as an interrupt rather than as a byte of text.

A CI runner has no terminal, but that only means nobody hands one over — a process can create
one. [`PtyHarness`](PtyHarness) calls `CreatePseudoConsole` (ConPTY), starts wip inside that
console, and drives the session from a script of directives (`send`, `expect`, `resize`,
`ctrl-c`, `eof`, `sleep`). Publish it alongside `wip.exe`:

```powershell
dotnet publish tests/e2e/PtyHarness/PtyHarness.csproj -c Release -o artifacts/pty
pwsh tests/e2e/run-e2e.ps1 -Wip artifacts/win-x64/wip.exe -PtyHarness artifacts/pty/pty-harness.exe
```

Two things the harness learned the hard way, both documented at their call sites:

- A process attached to a pseudo console still inherits the creator's standard handles when
  the creator has its own, so a CI step's redirected pipes reach the child and `isatty` is
  false — measured as `IsInputRedirected=True` in a correctly sized 80x24 console, with
  `wslc exec -i` then failing with `ERROR_INVALID_HANDLE`. The harness therefore starts the
  command through a second stage that opens the console's own `CONIN$`/`CONOUT$` first.
- Closing the console's input before the session ends is read as the terminal going away. The
  close event reaches `wslc`, which dies with `STATUS_CONTROL_C_EXIT`, and wip faithfully
  reports that instead of the status the shell chose. A session that ends itself is given the
  chance to; the outcome is written to `--result` before anything is closed, which is why the
  assertions read that file rather than the harness's own exit code.

## In CI

[`.github/workflows/e2e-windows.yml`](../../.github/workflows/e2e-windows.yml) runs it on
`windows-latest`: it publishes `wip.exe`, updates stable WSL directly from GitHub
with `wsl --update --web-download` (WSLC is GA in WSL 3.0.1 and later), verifies
`wslc` is on PATH, publishes the pseudo-console harness, then runs this script. It runs on
every pull request, plus weekly and on demand. Keeping it out of the `Test` workflow is about that workflow staying
WSLC-free on Linux, not about running this one rarely.

If the runner image has no WSLC, the "Verify wslc is available" step says so in one line
instead of letting a wip command fail for unrelated reasons.

## Adding a case

Keep fixtures minimal and use only disposable data. `volume-lifecycle.ps1` tests declared
storage via explicit backend `--mount type=volume` consumers: persistent data survives
container recreation; two references protect ephemeral storage until final detach; the
next ephemeral cycle is empty. This verifies storage without implementing the subsequent
sandbox mount integration. Bind-mount/path-resolution coverage remains separate.
