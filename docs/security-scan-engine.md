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

The directory is checked and created before the scan starts, and before the image request, so
an unusable path costs neither a pull nor a run. Persistence happens after the scan, so a path
rejected there would take a completed scan's report with it.

If storage fails after the preflight check, the CLI reports the persistence error on stderr
and still returns the captured report, coverage and scan status. The primary scan outcome is
preserved; a persistence error does not invent a new incomplete-scan reason or change the
scanner's completed status. Verify that `result.json` was published before consuming artifacts.

The JSON contract is versioned and written through `Utf8JsonWriter`:

| Field | Consumer contract |
| --- | --- |
| `version` | Currently `1`; reject unsupported versions |
| `run` | `runId`, UTC ISO-8601 `startedAt`/`endedAt`, `tool`, `image`, `rulesVersion`, `sandbox`, `network`, `networkVerifiedInternal`, `target`, and optional `commit` |
| `status` | Exactly `completed` or `incomplete` |
| `scannerExitCode` | The original scanner status, independent of result completeness |
| `findings` | Present only for `completed`; an empty array means no findings were reported within the recorded coverage |
| `reason` | Present only for `incomplete`: `timeout`, `report-collection-failed`, `report-empty`, `report-unparseable` or `unperformed-checks-lost` |
| `coverage` | Redacted `unperformedChecks`, or `missingReason` explaining why it is unavailable |

Each finding has optional `ruleId`, `name`, `severity`, `confidence` and `evidence`, plus
`location` containing optional `path`, `method` and `parameter`. Absent scanner fields remain
absent. ZAP severity codes map to `informational`, `low`, `medium` and `high`; confidence
codes map to `false-positive`, `low`, `medium`, `high` and `confirmed`. Unknown codes remain
strings rather than fabricated defaults. Location drops the deployment's scheme, host, port, query string and fragment so a
finding remains comparable across deployment authorities. A blank or unusable instance URI omits
only that instance's path; malformed report structure still makes the run incomplete. Locations
are not unique across origins within a multi-site report, so consumers must not merge different
sites by location alone. Origins remain in the raw report; a stable logical-site mapping is not
yet part of this format. Secret-shaped values in normalized
fields are replaced with `[REDACTED]` before writing; the separate raw artifacts are unchanged.
JSON string escaping preserves document structure even when scanner text contains controls,
quotes or markup. The embedded coverage copy uses the same redaction as other result strings;
`unperformed-checks.txt` remains verbatim for investigation and may contain sensitive diagnostics.

Check `status` before consuming `findings`. An incomplete result has a reason and no findings
array, so it cannot appear as a clean empty scan. A completed result still has coverage:
omitted active, authenticated or other checks are not evidence that those checks passed.
The CLI reserves two exit codes, both nonzero through a WSL shell. `70` means the result is
incomplete. `74` means the scan completed but its artifacts did not reach the requested
directory, which can still happen after the preflight check — a full volume, or a directory
removed mid-run. The two call for different responses: an incomplete scan has to be run again,
while a completed one whose save failed has its report on stdout and needs the output path
looked at. An incomplete result takes precedence when both apply, since its reason is in the
document either way.

A completed scanner that itself returns `70` or `74` maps to CLI exit `1`; its original status
remains in `scannerExitCode` and provenance. Other completed scanner statuses are forwarded
unchanged. So each reserved code means only what the CLI uses it for, and a completed scan whose
result was not saved never returns `0` — asking for `--output` and receiving neither a file nor
a nonzero status is the reading this format exists to prevent.

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
| `Rules` | Optional `PinnedRules(RepositoryUrl, Commit, ContainerPath)` declaration |

Every adapter must declare a coverage record so an empty finding list cannot imply that
unreported checks ran. Plan validation rejects missing coverage paths before backend calls;
`unperformed-checks-lost` is reserved for a requested record that is lost, unreadable,
oversized or empty after execution.

The plan cannot set backend options, networks, mounts or environment variables. Its argv
appears after the container ID and can only become the scanner command's arguments. The
engine and sandbox declaration control isolation. One scanner per invocation follows from
`Prepare` and `Run` each accepting one adapter.

## Pinned rules staging

An adapter whose rules are not bundled in its image declares `ScanPlan.Rules` with a source
`RepositoryUrl`, immutable `Commit` and destination `ContainerPath`. This data cannot select a
mount, volume, network, helper image or backend option. Validation requires an absolute HTTPS
URL without userinfo or control characters, exactly 40 lowercase hexadecimal commit characters,
and a canonical absolute Linux destination with at least two nonempty path segments.
`/opt/wip-rules` is accepted; `/etc`, `/usr` and `/rules` are refused so replacing rules cannot
remove a top-level container directory. Branches, tags and short hashes are refused.

For declared rules, `Prepare` first validates the prospective cache-tree path through the
same `mapRulesSource`/`WslPath.ForWslc` guard used by `Run`, before any image pull or git
fetch. An unusable WSL-UNC source therefore refuses preparation before downloads; the
cache remains beside the configuration rather than being relocated. After pulling the
image, `Prepare` uses host-side git to fetch the pinned commit into a staging tree,
verifies that `FETCH_HEAD` exactly equals the pin, and checks out that commit. Missing or
unrunnable git, a failed fetch/checkout, a mismatched commit or an unusable rule tree
stops preparation; there is no scan without the declared rules. Git uses
`core.autocrlf=false` and `core.eol=lf`: disabling autocrlf alone leaves attribute-driven
default line endings dependent on the host, which would also make cached fingerprints
host-dependent. An explicit repository `eol=crlf` attribute still overrides this LF
default. Failed temporary staging is cleaned up without replacing the original error if
cleanup also fails.

Verified trees are cached under `<config directory>/.wip/scanner-rules/<commit>/tree`. The
commit names immutable content; published verification metadata and a tree fingerprint
protect reuse against an incomplete or changed local entry. Fingerprinting sorts
slash-normalized relative paths in ordinal order, making the digest independent of native
separators and cache location. A second preparation of the same valid pin reuses the tree
without fetching. Verification metadata stays outside the copied tree, and symlinks are
refused. Version 2 cache markers reject older entries that may carry native line endings;
remove a rejected entry and prepare again. The same recovery instruction applies when an
existing commit directory is missing its tree or verification marker. Entries are not
deleted or migrated automatically, and incomplete entries cannot be reused. `Run` requires
a valid staged entry and performs no git fetch. It repeats the source-path guard with a
pinned-rules context label before `container cp`. Its default WSL-UNC refusal names the
host-path cause rather than allowing an empty transfer to look like empty rules. Runtime
state under any in-tree `.wip/` directory, including this cache, is ignored by git. The
rules copy is covered by fake-backend tests; real WSLC execution is not verified locally.

Before fetching and again before running, the engine rejects overlap between either the
rules path or its derived staging path and any referenced declared volume mount. The check
covers both directions, including a mount nested inside either path, so replacement cannot
remove mounted data. This belongs to the engine because only it has the sandbox declaration.
Before copying, `Run` also checks the running container's reported mounts, including stale
or implicit mounts absent from that declaration. Before removing either destination, separate
`exec <container> test -L <ancestor>` calls reject symlinked ancestors or an unverifiable
probe. Rejecting aliases keeps a path from resolving into a mount that its written spelling
does not overlap; missing ancestors can still be created normally.

After image and network verification, the engine uses a derived `<path>.staging` directory.
WSLC requires the copy destination to exist and nests the source directory inside it; even a
source ending in `/.` does not copy only its contents. The host cache's source basename is
always `tree`, so the engine uses separate backend argv calls, never a shell:

```text
exec <container> rm -rf -- <path> <path>.staging
exec <container> mkdir -p -- <path>.staging
container cp <source> <container>:<path>.staging
exec <container> mv -- <path>.staging/tree <path>
exec <container> rmdir -- <path>.staging
```

The whole-tree `container cp` step has a five-minute deadline for large Windows-to-WSL
transfers. Setup, moves, cleanup and probes keep separate one-minute bounds.

The nonempty `.staging` suffix cannot equal the validated rules path, and the adapter cannot
choose a separate staging destination. Moving the known `tree` directory puts its contents
directly at the declared path. Stdin-tar extraction also avoids nesting, but would require host
tar and backend stdin plumbing; this sequence needs neither. A failed copy, move or cleanup,
a missing path or an empty rules directory refuses execution. The copy uses the already
verified scanner image, with no separate mirror image or volume declaration.

Before executing the scanner, the engine probes every ancestor from `/` down to the rules
path and every directory in the rules tree for other-user read and traversal permissions.
It checks rather than chmods ancestors, preserving the image's existing access policy.
Appending `/.` resolves an ancestor directory even when its path ends in a symlink, so the
`-type d` predicate cannot silently skip it. Each probe is a separate backend invocation
without a shell:

```text
find <ancestor>/. -maxdepth 0 -type d ! -perm -o+rx -print -quit
find <path> -type d ! -perm -o+rx -print -quit
find <path> -type f -print0
find <path> -type f ! -perm -o+r -print -quit
```

The NUL-separated file list is counted against the exact number of regular files hashed in
the verified host tree. An empty copy refuses the scan; a shortfall is reported as an
incomplete copy and a surplus as extra files. Each refusal reports both expected and actual
counts, so one copied file cannot stand in for the complete rule set.

The copy is performed as root while the scanner may run as another user, so permission
checks inspect the other-user bits rather than effective access. Directories need both
`o+r` to list files and `o+x` to traverse them; files need `o+r`. BusyBox 1.36.1 in Alpine 3.20
rejects `-readable`; these `-perm` predicates work there and with GNU findutils. Missing
ancestor access, unreadable directories, an empty or partial tree, and unreadable files have
distinct refusals. A nonzero exit or timeout from any probe also refuses the scan rather than
assuming the rules are usable.

Provenance appends `;rules-commit:<commit>` to the adapter's `RulesVersion` once, so both stderr
and the result document identify the pin. The colon preserves the commit through normalized
metadata redaction. Adapters without `Rules`, including ZAP, retain their existing calls:
no rules cache access, git invocation, copy or extra pre-run check is added.

## Phases and output

| Phase | Behavior |
| --- | --- |
| Prepare | Validate the plan, pull the immutable image and fetch/verify any declared rules host-side |
| Run | Verify the image and boundary, copy/check any staged rules, then execute the tool |
| Collect | Read the report under a separate short deadline and bounded capture limit |
| Cleanup | Stop the owned container, including on execution failure |

The CLI calls `Prepare` before `Run`, but `Run` has no per-invocation preparation token:
it independently verifies the running sandbox and, for rule plans, requires a valid staged
cache entry that can be reused across runs. Callers must create
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
