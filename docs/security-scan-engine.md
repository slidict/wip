# Security scan engine

The engine runs one diagnostic tool inside one declared sandbox, behind its declared
[network boundary](sandbox-network-isolation.md). It owns preparation, execution, report
collection and cleanup; each built-in adapter supplies only a command and report format.
No adapter is registered yet. ZAP is the first planned adapter.

## Command and image

```sh
wip security tools
wip security scan <sandbox> --tool <name> --target <address> [--timeout <seconds>]
```

The sandbox and its internal network remain declared in `wip.yml`. The target is an address
or alias reachable inside that network, such as another sandbox's network alias. The command
cannot replace the configured boundary.

The adapter and sandbox declaration must use the same immutable image reference:
`repository@sha256:<64 hexadecimal characters>`. Tags, including version tags, can be
reassigned and are refused. `Prepare` pulls the digest outside the isolated sandbox; `Run`
compares the declaration and the running container's `Config.Image` with the plan. The
inspect record's top-level `Image` is a config image ID, not a repository digest reference,
so it cannot replace a missing `Config.Image`. Unknown or mismatched references are refused.

### Keeping the scanner container running

`SandboxLifecycle.Create` starts the image with `run -d` and requires the resulting container
to remain running. The sandbox definition currently has no command or entrypoint override.
A one-shot scanner image whose entrypoint exits therefore cannot be used directly.

Package a scanner runtime image with a persistent entrypoint before adding its adapter. For
example, a derived image can replace the upstream entrypoint with a keepalive process:

```dockerfile
FROM upstream-scanner@sha256:<upstream digest>
ENTRYPOINT ["sleep", "infinity"]
```

This example requires a `sleep` implementation supporting `infinity`; choose a persistent
foreground process available in the actual image. Publish the derived image and use its
resolved repository digest in both the adapter's `ScanPlan.Image` and the sandbox's `image:`.
The adapter executes the scanner binary explicitly through `Argv`, since the original image
entrypoint is no longer the container's main process. Keep the container running until scan
cleanup stops it. The derived runtime must also provide the report-reading utilities used by
the engine.

A future configuration override would need typed argv fields, backend argument validation
and lifecycle tests; it is not implemented by this engine.

## Internal adapter contract

`ISecurityScanner`, `ScanEngine` and their plan, target, image and outcome types are internal
to wip, visible to its CLI and tests. There is no external plugin API, dynamic assembly
loading or configuration key that registers a tool. `ScannerRegistry` is a static table;
adding an adapter requires a reviewed change to its code and registry entry.

An adapter supplies a `ScanPlan`:

| Field | Meaning |
| --- | --- |
| `Image` | Immutable repository digest for the scanner runtime |
| `Argv` | Executable and arguments inside the sandbox, passed directly without a shell |
| `ReportPath` | Canonical absolute path of the report inside the container |
| `RulesVersion` | Version or digest of the applied rule, template or add-on set |

The plan cannot set backend options, networks, mounts or environment variables. Its argv
appears after the container ID and can only become the scanner command's arguments. The
engine and sandbox declaration control isolation. One scanner per invocation follows from
`Prepare` and `Run` each accepting one adapter.

## Phases and output

| Phase | Behavior |
| --- | --- |
| Prepare | Validate the plan and pull its immutable image outside the isolated sandbox |
| Run | Revalidate the plan, running image and boundary, then execute the tool |
| Collect | Read the report under a separate short deadline and bounded capture limit |
| Cleanup | Stop the owned container, including on execution failure |

`Run` accepts the plan returned by `Prepare` but still validates it. A supplied plan is not
proof of preparation or valid isolation. Cleanup retains the container for inspection;
removal remains an explicit `sandbox destroy` operation.

A tool's own exit code is retained because a nonzero code can represent findings. A timeout
is identified separately in the outcome and provenance. On timeout the engine stops the
sandbox immediately and returns no report: a stopped container cannot serve a report through
exec, and reading before stopping could collect a file still being written by the scanner.
The runner reserves exit code 124 for timeouts, so a scanner returning that code is also
classified as timed out. Stopping the backend client alone does not establish that container
processes have stopped.

Normal report collection uses the shorter of the scan timeout and ten seconds. A failed
report read yields no report; collection does not restart the scanner or extend the scan's
execution timeout.

Reports contain untrusted target-controlled text. Captured report text uses UTF-8 decoding
and a 1,048,576-character capture limit; exceeding the limit fails collection rather than silently
truncating a report. Interactive terminal output sanitizes control characters; redirected
output preserves the captured report text. Progress and provenance go to stderr, leaving
stdout for report output.

## Fail closed

Execution is refused when the tool is absent or unknown, the network is undeclared or cannot
be verified as owned and internal, the container is not running, or it has any attachment
other than exactly its declared network. Image mismatches, missing source image references,
mutable image references, missing rules versions, noncanonical report paths, invalid argv,
failed pulls and nonpositive scan timeouts are also refused.

The running-container network check lives in `SandboxLifecycle.RunningId`, so it protects
exec, capture, interactive exec, attach and the relay as well as scans. The engine checks the
boundary again to report scan-specific refusals. Sandboxes with no declared network retain
their existing behavior; the scan engine always requires one.

## Validation scope

`tests/Wip.Tests/ScanEngineTests.cs` uses a fake backend and stub scanner to exercise plan
validation, image and boundary checks, timeout handling, report collection and cleanup.
It also checks that adapter arguments resembling backend flags remain after the container
ID and never become `run`, network or mount options. Lifecycle tests check source image
reference extraction and the shared running-container guards. Runner and CLI tests cover
capture and report output behavior.

The registry is tested as empty until a real adapter is added. No real-scanner end-to-end test
exists yet; it belongs with that first adapter. Fake-backend tests do not verify actual WSLC
process termination, image entrypoints or scanner report behavior in a running environment.
