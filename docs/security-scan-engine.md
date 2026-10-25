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
reassigned and are refused. `Prepare` validates the plan and requests a pull outside the
isolated sandbox; `Run` compares the declaration and the running container's `Config.Image`
with the plan. The inspect record's top-level `Image` is a config image ID, not a repository
digest reference, so it cannot replace a missing `Config.Image`. Unknown or mismatched
references are refused.

### Keeping the scanner container running

`SandboxLifecycle.Create` starts the image with `run -d` and requires the resulting container
to remain running. Declare `command` as an argv list to replace a one-shot image CMD with a
persistent foreground command while keeping the upstream image digest:

```yaml
sandboxes:
  - name: scanner
    image: scanner-runtime@sha256:<64 hexadecimal characters>
    network: scan-network
    command: [sleep, infinity]
```

The image must provide `sleep` with `infinity` support; choose a persistent foreground command
available in the actual scanner image. The adapter's `ScanPlan.Image` and the sandbox's
`image:` still name the same upstream digest. Its `Argv` explicitly runs the scanner binary
through exec while the declared main command keeps the container alive until cleanup.

The list is passed after the image reference, never through shell splitting or expansion.
It cannot become backend options. Loading rejects an empty list, nonstring or empty elements,
control characters, and any element beginning with `-`. Omit `command` to retain the image's
original CMD and ENTRYPOINT unchanged. The declaration applies when a container is created;
starting or reusing an existing container preserves its original command. To apply an edited
command, explicitly destroy and recreate the sandbox.

This overrides CMD, not ENTRYPOINT. An image with a fixed scanner ENTRYPOINT still receives
the declared list as arguments to that entrypoint. The keepalive example therefore requires
an image with no ENTRYPOINT or an entrypoint that executes the supplied command; `command`
alone cannot replace a fixed one-shot ENTRYPOINT. It does not add an entrypoint override.
The image must also provide the report-reading utilities used by the engine.

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
| Prepare | Validate the plan and request an immutable-image pull outside the isolated sandbox |
| Run | Revalidate the plan, running image and boundary, then execute the tool |
| Collect | Read the report under a separate short deadline and bounded capture limit |
| Cleanup | Stop the owned container, including on execution failure |

The CLI calls `Prepare` before `Run`, but `Run` has no preparation token or history check:
it accepts a valid plan and independently verifies the running sandbox. Callers must create
and start that sandbox before scanning. Consequently its pinned image is already available
locally when a scan can run; the preparation pull may check registry metadata but is not a
guaranteed first image fetch. It does not create, start or reorder sandbox creation. Preparing
before sandbox creation would require a separate lifecycle workflow and is outside this
command's current design.

Cleanup retains the container for inspection; removal remains an explicit `sandbox destroy`
operation.

A tool's own exit code is retained because a nonzero code can represent findings. A timeout
is identified separately in the outcome and provenance. On timeout the engine immediately requests a
sandbox stop and returns no report: a stopped container cannot serve a report through
exec, and reading before stopping could collect a file still being written by the scanner.
Timeout metadata is carried separately from the exit code. A scanner that exits normally
with code 124 retains that exit code and its report; it is not classified as timed out.
Stopping the backend client alone does not establish that container processes have stopped.

Normal report collection uses the shorter of the scan timeout and ten seconds. A collection
timeout, nonzero read exit or capture-limit overflow yields no report and a specific stderr
diagnostic. These failures preserve the scanner's exit code and provenance; they do not mean
that the scanner wrote no file. Collection does not restart the scanner or extend its
execution timeout.

Reports contain untrusted target-controlled text. Captured report text uses UTF-8 decoding
and a 1,048,576-character limit applied only to report collection. Existing probe callers,
including dependency health checks, retain their previous capture behavior. Oversized reports
are discarded with a diagnostic rather than returned as complete artifacts. Interactive
terminal output sanitizes control characters in buffered writes; redirected output preserves
the captured report text. Progress and provenance go to stderr, leaving stdout for reports.

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
