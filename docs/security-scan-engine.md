# Security scan engine

The engine runs one diagnostic tool inside one declared sandbox, behind its declared
[network boundary](sandbox-network-isolation.md). It owns preparation, execution, report
collection and cleanup; each built-in adapter supplies only a command and report format.
OWASP ZAP is the first built-in adapter, selected as `--tool zap`.

## Command and image

```sh
wip security tools
wip security scan <sandbox> --tool <name> --target <address> [--timeout <seconds>] [--output <dir>] [--commit <sha>]
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
Flags are allowed there because they cannot become backend options. Loading rejects an empty
list, nonstring or empty elements, control characters, and whitespace in the first element.
Later arguments may contain spaces, as in `[nginx, -g, 'daemon off;']`.
Omit `command` to retain the image's original CMD and ENTRYPOINT unchanged.

When a command is declared, reusing, starting or executing inside an existing container
requires a matching `Config.Cmd`; an unreadable command is refused. To apply an edited
command, explicitly destroy and recreate the sandbox rather than relying on an implicit
configuration change.

This overrides CMD, not ENTRYPOINT. An image with a fixed scanner ENTRYPOINT still receives
the declared list as arguments to that entrypoint. The keepalive example therefore requires
an image with no ENTRYPOINT or an entrypoint that executes the supplied command; `command`
alone cannot replace a fixed one-shot ENTRYPOINT. It does not add an entrypoint override.
The image must also provide the report-reading utilities used by the engine.

## ZAP adapter

The built-in `zap` adapter pins this immutable multi-platform image:

```text
ghcr.io/zaproxy/zaproxy@sha256:5ccfff9a411743ab2615136a2edd7416bec9ce41572fa94e935df38c14adb2b6
```

Use that exact reference as the scanner sandbox's `image:` and declare
`command: [sleep, infinity]` to keep it running before `wip security scan`. This image has no
ENTRYPOINT, so the command replaces its default `bash` CMD. Its execution user is `zap`, not
root; reports are written to `/tmp/wip-zap-report.json`, which that user can write and the
engine can read through the same user's exec session.

The adapter passes Python source and arguments as separate argv elements. The fixed wrapper
writes an [Automation Framework](https://www.zaproxy.org/docs/automate/automation-framework/)
plan, then invokes `/zap/zap.sh -cmd -silent -addonuninstall oast -autorun <plan>`.
The context confines the traditional spider to the supplied HTTP(S) origin and path subtree.
Crawling is bounded to one minute, depth two and ten children; passive scanning waits at most
one more minute. Form submission, robots.txt and sitemap discovery are disabled to avoid
leaving the declared subtree. It does not run active scans or a browser spider. Silent mode disables
unsolicited update/telemetry requests, and removal of the OAST add-on disables out-of-band
callbacks. The declared internal network remains the enforcement boundary for egress.

The report job writes ZAP's native `traditional-json` report. The rules identifier combines
the immutable image digest with the reviewed `wip-passive-v1` profile; rules and add-ons come
from that image rather than a scan-time update. Completed runs retain ZAP's own exit code:
Automation Framework returns zero on completion, one on errors, or two on warnings. A completed passive
scan with no alerts does not establish that active or authenticated checks passed.

`/tmp/wip-zap-unperformed.txt` retains the profile's omitted checks and crawl limits together
with the native Automation Framework log. The engine collects that sidecar separately and
the CLI writes it to stderr under `Unperformed checks (raw):`; stdout remains the original
JSON report. Sidecars use the same collection deadline and retention limit as reports;
missing, unreadable or oversized diagnostics are reported explicitly. Raw artifacts remain
available alongside the versioned normalized result described below.

Only unauthenticated targets are supported here. Credentials are not accepted through argv;
[authenticated scanning](https://github.com/slidict/wip/issues/277) requires a separate secret
transport design.

Pull requests run the small [scan engine fixture](../tests/e2e/README.md#security-scans)
instead of downloading ZAP. It checks real-WSLC isolation and report/omission collection.
Scheduled and manually triggered runs additionally scan a single internal HTTP page with the
real adapter. The pinned image is about 1.12 GiB compressed, so its download and registry
availability are deliberately kept off unrelated pull requests. The real scenario has a
bounded pull and short scan inside the workflow's 45-minute job budget.

## Result document and persisted artifacts

`--output <dir>` writes `<dir>/<runId>/result.json`, `report.json` and
`unperformed-checks.txt`. `--commit <sha>` adds caller-supplied application revision metadata;
it is never inferred from the sandbox. The raw report and coverage files retain their original
text. Unavailable artifacts are empty files whose reason is recorded in `result.json`.
Persistence finishes before sandbox teardown, including for an incomplete timeout result.
Each file is written to a temporary name and moved into place; `result.json` is published
last. Consumers should begin with that document, not infer success from an artifact's presence.
Omitting `--output` skips persistence. Stdout remains the raw report in both cases.

The JSON contract is versioned and written through `Utf8JsonWriter`:

| Field | Consumer contract |
| --- | --- |
| `version` | Currently `1`; reject unsupported versions |
| `run` | `runId`, UTC ISO-8601 `startedAt`/`endedAt`, `tool`, `image`, `rulesVersion`, `sandbox`, `network`, `networkVerifiedInternal`, `target`, and optional `commit` |
| `status` | Exactly `completed` or `incomplete` |
| `scannerExitCode` | The original scanner status, independent of result completeness |
| `findings` | Present only for `completed`; an empty array means no findings were reported within the recorded coverage |
| `reason` | Present only for `incomplete`: `timeout`, `report-collection-failed`, `report-empty`, `report-unparseable` or `unperformed-checks-lost` |
| `coverage` | Verbatim `unperformedChecks`, or `missingReason` explaining why it is unavailable |

Each finding has optional `ruleId`, `name`, `severity`, `confidence` and `evidence`, plus
`location` containing optional `path`, `method` and `parameter`. Absent scanner fields remain
absent. ZAP severity codes map to `informational`, `low`, `medium` and `high`; confidence
codes map to `false-positive`, `low`, `medium`, `high` and `confirmed`. Unknown codes remain
strings rather than fabricated defaults. Location drops the deployment's scheme, host, port, query string and fragment so a
finding remains comparable across deployment authorities. Secret-shaped values in normalized
fields are replaced with `[REDACTED]` before writing; the separate raw artifacts are unchanged.
JSON string escaping preserves document structure even when scanner text contains controls,
quotes or markup. Raw coverage remains unmodified and may contain sensitive tool diagnostics.

Check `status` before consuming `findings`. An incomplete result has a reason and no findings
array, so it cannot appear as a clean empty scan. A completed result still has coverage:
omitted active, authenticated or other checks are not evidence that those checks passed.
The CLI reserves exit `70` for incomplete results, including when invoked from a WSL shell.
A completed scanner that itself returns `70` maps to CLI exit `1`; its original `70` remains
in `scannerExitCode` and provenance. Other completed scanner statuses are forwarded unchanged.
This reservation makes CLI exit `70` unambiguously mean an incomplete result.

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
| `UnperformedChecksPath` | Required canonical absolute path for raw omitted-check information; missing paths are rejected before execution |

Every adapter must declare a coverage record so an empty finding list cannot imply that
unreported checks ran. Plan validation rejects missing coverage paths before backend calls;
`unperformed-checks-lost` is reserved for a requested record that is lost, unreadable,
oversized or empty after execution.

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

The tool's own exit code is retained in the outcome and provenance because a nonzero code
can represent findings. Incomplete runs use a distinct CLI exit status. A timeout is
identified separately in the outcome and provenance. On timeout the engine records an
incomplete result, requests sandbox stop during cleanup and returns no report. It never
collects from the timed-out scanner, which may still be writing a partial file.
Timeout metadata is carried separately from the exit code. A scanner that exits normally
with code 124 retains that exit code and its report; it is not classified as timed out.
Stopping the backend client alone does not establish that container processes have stopped.

Normal report collection uses the shorter of the scan timeout and ten seconds. A collection
timeout, nonzero read exit or capture-limit overflow yields no report and a specific stderr
diagnostic. These failures preserve the scanner's exit code in provenance and the result,
but make the CLI return the incomplete status; they do not mean the scanner wrote no file.
Collection does not restart the scanner or extend its execution timeout.

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

Registry tests verify that `zap` resolves to the built-in adapter. Adapter tests check the
pinned image, scope and callback controls, report paths and raw omitted-check information.
Fake-backend tests do not verify actual WSLC process termination or scanner behavior; the
lightweight and scheduled real-ZAP scenarios cover those runtime paths separately.
