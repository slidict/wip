# Security scan engine

The engine runs one diagnostic tool inside one declared sandbox, behind its declared
[network boundary](sandbox-network-isolation.md). It owns preparation, execution, report
collection and cleanup; each built-in adapter supplies only a command and report format.
Select exactly one built-in adapter: `--tool zap` or `--tool nuclei`. Comma-separated or
repeated tool selections are unsupported. Adding a tool requires a reviewed pull request
to wip itself; configuration cannot load arbitrary scanners or plugins.

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

`command` overrides CMD. For an image with a fixed scanner ENTRYPOINT, declare an
`entrypoint` executable too. WSLC replaces only that executable, so its arguments go in
`command`, not in `entrypoint`. For the official Nuclei image:

```yaml
entrypoint: sleep
command: [infinity]
```

`entrypoint` must be one nonempty string without whitespace, control characters or a leading
`-`; lists and shell command lines are refused. Declaring it requires `command` as well:
the override clears the image CMD, so the executable would otherwise run with no operand.
The configuration still loads without `command` -- `sandbox create` is what refuses it, so
commands that do not create a sandbox, `destroy` included, keep working on such a config.
Omitting `entrypoint` preserves the image ENTRYPOINT.
Create, reuse and exec verify a declared override against the singleton argv in
`Config.Entrypoint`. Drift requires destroy-then-create; an unreadable field or a mismatch
immediately after creation reports a backend limitation rather than recommending a loop of
recreation. As with `command`, removing the declaration does not restore or verify the image
default on an existing container. The image must provide the engine's report-reading utilities.

## Nuclei adapter

`--tool nuclei` uses the official v3.9.0 multi-platform image:

```text
projectdiscovery/nuclei@sha256:66b5f88c9b98ad29c03343dd34e5e85bde0ad10428d72e0171b067efe1ed7811
```

Declare that exact `image`, `entrypoint: sleep` and `command: [infinity]` on the scanner's
internal network. The image runs as root and contains no templates. `PinnedRules` stages
`https://github.com/projectdiscovery/nuclei-templates.git` at commit
`2603fc1f23f2ef219ea4834128a163113c6b5168` (v10.3.2) into
`/opt/wip-nuclei-templates` through the existing verified cache/copy path. Only
`http/misconfiguration` and `helpers` are staged; templates in the first read wordlists from
the second. Provenance includes
the image, `http-misconfiguration-v1` profile and the template commit.

The fixed shell wrapper passes the target as positional data, never shell syntax. It runs:

```text
nuclei -u <url> -t /opt/wip-nuclei-templates/http/misconfiguration
  -duc -ni -no-stdin -nh -dr -dut -pt http -etags dos,fuzz,bruteforce
  -rl 10 -c 2 -timeout 5 -retries 0 -nc -jsonl -omit-raw -omit-template
  -o /tmp/wip-nuclei-native.jsonl -elog /tmp/wip-nuclei-errors.jsonl
```

These flags were checked against the pinned image's v3.9.0 binary.
`-duc` disables initial template installation and update checks; `-ni` excludes interactsh
callbacks. Only signed HTTP misconfiguration templates are selected. Other template
categories, workflows, code, browser/headless, authenticated scanning and crawling are not
run. This profile sends active HTTP requests and does not promise ZAP's path-subtree scope:
templates can address other paths on the target. Redirects and implicit HTTP probing are
disabled. Each request has a five-second timeout, no retry, concurrency two and a rate limit
of ten requests/second; the engine supplies the total deadline. The internal network remains
the egress boundary. Targets must be unauthenticated HTTP(S) URLs without fragments or commas
(the latter would turn one `-u` value into multiple targets).

`/tmp/wip-nuclei-unperformed.txt` records these omissions and bounds, nuclei's stderr log,
and the request error log. Each log is bounded: one over 256 KiB keeps its first 192 KiB and
its last 64 KiB, which holds nuclei's closing `[FTL]`/`[ERR]` summary, with a
`[wip: truncated, N bytes dropped]` line between them. Its stdout is discarded: with `-o` nuclei writes every
finding to both, and the duplicate would pass the engine's 1 MiB collection cap on a noisy
target, costing the whole coverage record for a scan that succeeded. Runtime failures or skipped templates are evidence
of incomplete coverage, not passed checks. A missing sidecar remains an incomplete result
under the shared engine's existing rules.

The collected report `/tmp/wip-nuclei-report.jsonl` preserves native JSONL findings and,
only on exit zero with an actual native output file, appends this adapter completion record:

```json
{"wip-nuclei-completed":"http-misconfiguration-v1"}
```

This distinguishes a successful zero-finding run from lost/empty output without changing the
shared engine. Missing templates still fail in Nuclei; a missing output file cannot acquire
a fabricated completion record. The converter validates each JSONL object and report
structure, raising `JsonException` or `FormatException` for damage. It maps template ID,
name, severity (`info` to `informational`), extracted evidence or matcher name, and the matched
URL's path. Query strings and hosts are not endpoint keys; method, parameter and confidence
remain absent because this profile does not reliably supply them. A completion record must
be the final record, and is never a finding. No findings does not establish that excluded
checks passed. Container lifecycle and the shared scanner contracts are unchanged.

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
After publishing, the CLI writes `<tool>: result saved to <dir>/<runId>` to stderr, so a
retried run that leaves several run directories names the one it produced.

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
| `reason` | Present only for `incomplete`: `timeout`, `report-missing` (the scanner wrote no report), `report-collection-failed` (the read failed, and the report's absence could not be established), `report-empty`, `report-unparseable` or `unperformed-checks-lost` |
| `coverage` | Redacted `unperformedChecks`, or `missingReason` explaining why it is unavailable |

Each finding has optional `ruleId`, `name`, `severity`, `confidence` and `evidence`, plus
`location` containing optional `path`, `method` and `parameter`. Absent scanner fields remain
absent. ZAP severity codes map to `informational`, `low`, `medium` and `high`; confidence
codes map to `false-positive`, `low`, `medium`, `high` and `confirmed`. Unknown codes remain
strings rather than fabricated defaults. Location drops the deployment's scheme, host, port, query string and fragment so a
finding remains comparable across deployment authorities. An absent, null, blank or unparseable
instance URI omits only that instance's path. Any other present shape -- nested, array, boolean or
numeric -- is malformed report structure and makes the run incomplete. `uri` is deliberately
stricter than its sibling fields, which accept text or a number: it carries the location, so a
template that changed its shape would otherwise cost every finding its location while the run still
reported completed with a full list, and a number loses it exactly as a nested object does. Locations
are not unique across origins within a multi-site report, so consumers must not merge different
sites by location alone. Origins remain in the raw report; a stable logical-site mapping is not
yet part of this format. Secret-shaped values in normalized
fields are replaced with `[REDACTED]` before writing; the separate raw artifacts are unchanged.
JSON string escaping preserves document structure even when scanner text contains controls,
quotes or markup. The embedded coverage copy uses the same redaction as other result strings;
`unperformed-checks.txt` is stored without redaction for investigation and may contain sensitive
diagnostics. It holds what the adapter wrote, which for Nuclei includes logs cut as described above.

Check `status` before consuming `findings`. An incomplete result has a reason and no findings
array, so it cannot appear as a clean empty scan. A completed result still has coverage:
omitted active, authenticated or other checks are not evidence that those checks passed.
The CLI reserves three exit codes, all nonzero through a WSL shell. `70` means the result is
incomplete; with `--output` its reason is in `result.json`, and without it the reason is on
stderr, since nothing is persisted either way. `74` means the scan completed but its artifacts
did not reach the requested directory, which can still happen after the preflight check — a
full volume, or a directory removed mid-run. `73` means both: the scan is incomplete and no
`result.json` was published, so the reason is only on stderr. The responses differ: an
incomplete scan has to be run again; a completed one whose save failed has its report on stdout
and needs the output path looked at; and an incomplete unsaved one needs the output path fixed
before the rerun, or the rerun loses its result the same way.

A completed scanner that itself returns `70`, `73` or `74` maps to CLI exit `1`; its original status
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
| `Rules` | Optional `PinnedRules(RepositoryUrl, Commit, ContainerPath, Subpaths)` declaration; `Subpaths` stages only those directories, each at its original position. Null stages the whole tree; an empty, repeated or nested set is refused, and order does not matter |

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
verifies that `FETCH_HEAD` exactly equals the pin, and checks out that commit. Fetch and
checkout each have a five-minute budget because their work scales with the tree; metadata-only
init and ref resolution keep a 30-second bound. Missing or unrunnable git, a failed
fetch/checkout, a mismatched commit or an unusable rule tree stops preparation; there is no
scan without the declared rules. Git uses
`core.autocrlf=false` and `core.eol=lf`: disabling autocrlf alone leaves attribute-driven
default line endings dependent on the host, which would also make cached fingerprints
host-dependent. An explicit repository `eol=crlf` attribute still overrides this LF
default. Failed temporary staging is cleaned up without replacing the original error if
cleanup also fails.

Temporary staging keeps the publishable `entry/tree` and `entry/verified.json` separate from
sibling `git-dir`, `hooks` and `git-config`. Only `entry` moves into the commit-keyed cache;
git metadata remains in scratch until publication finishes, then cleanup runs. External
Windows antivirus or indexer handles can still temporarily deny the newly written entry's
move, so metadata separation alone does not establish reliable publication.

Only `IOException` and `UnauthorizedAccessException` from the publish move are retried:
five attempts with 100, 200, 300 and 400 ms delays, totaling one second of waiting. Fetch and
verification failures are not retried. If another preparation already published the pin,
its destination is verified and reused immediately. Exhaustion fails closed with an explicit
staging-publication error. Linux test passes do not verify this Windows handle behavior;
Windows verification remains required.

Verified trees are cached under `<config directory>/.wip/scanner-rules/<commit>/tree`, or
`<commit>-<digest of the sorted subpaths>/tree` when the adapter declares them, so one set of
subtrees never satisfies another set or a whole-tree pin, nor the reverse. The marker records
the set and reuse refuses a mismatch. The commit names immutable content; published verification metadata and a tree fingerprint
protect reuse against an incomplete or changed local entry. Fingerprinting sorts
slash-normalized relative paths in ordinal order, making the digest independent of native
separators and cache location. A second preparation of the same valid pin reuses the tree
without fetching. Verification metadata stays outside the copied tree, and symlinks are
refused. Version 2 cache markers reject older entries that may carry native line endings;
remove a rejected entry and prepare again. The same recovery instruction applies when an
existing commit directory is missing its tree or verification marker. Entries are not
deleted or migrated automatically, and incomplete entries cannot be reused. Declaring or
changing subpaths changes the entry an adapter asks for, so a complete whole-tree entry staged
beforehand is bypassed rather than rejected: it blocks nothing, and removing it is optional
cleanup that reclaims disk. Removal is required only for an incomplete or mismatched entry
under the key being prepared. `Run` requires
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
transfers. Setup, moves, cleanup and the mode-bit probes keep separate one-minute bounds; the
effective-access probes get the copy's five minutes, for the reason given below.

The nonempty `.staging` suffix cannot equal the validated rules path, and the adapter cannot
choose a separate staging destination. Moving the known `tree` directory puts its contents
directly at the declared path. Stdin-tar extraction also avoids nesting, but would require host
tar and backend stdin plumbing; this sequence needs neither. A failed copy, move or cleanup,
a missing path or an empty rules directory refuses execution. The copy uses the already
verified scanner image, with no separate mirror image or volume declaration.

Before executing the scanner, the engine probes every ancestor from `/` down to the rules
parent for traversal, and every directory and file in the rules tree for the access the scanner
needs there. An ancestor is asked only to be traversable: opening a file beneath it never lists
it, so requiring read would refuse a 0711 image directory the scanner could use. A mode that
grants every class what the entry needs -- traverse for an ancestor, read and traverse for a
directory in the tree, read for a file -- provides a fast acceptance path.
It checks rather than chmods ancestors, preserving the image's existing access policy.
Appending `/.` resolves an ancestor directory even when its path ends in a symlink, so the
`-type d` predicate cannot silently skip it. Each probe is a separate backend invocation
without a shell:

```text
find <ancestor>/. -maxdepth 0 -type d ! -perm -111 -print -quit
find <path> -type d ! -perm -555 -print -quit
find <path> -type f -print0
find <path> -type f ! -perm -444 -print -quit
```

When one of those reports a path, the same prefix runs again under the scanner's own
credentials, once per access the entry needs:

```text
find <ancestor>/. -maxdepth 0 -type d ! -exec test -x {} ; -print -quit
find <path> -type d ! -exec test -r {} ; -print -quit
find <path> -type d ! -exec test -x {} ; -print -quit
find <path> -type f ! -exec test -r {} ; -print -quit
```

The NUL-separated file list is counted against the exact number of regular files hashed in
the verified host tree. An empty copy refuses the scan; a shortfall is reported as an
incomplete copy and a surplus as extra files. Each refusal reports both expected and actual
counts, so one copied file cannot stand in for the complete rule set.

The fast path asks for `-perm -555` on directories -- read and execute for every class, since
listing needs read and traversal needs execute -- and `-perm -444` on files. Granting it to every
class means the answer holds whoever the scanner turns out to be. Other-user bits alone would
not settle it -- Linux resolves the owner class first and never falls back to other, so mode
`0004` is unreadable to its own owner while passing `-perm -o+r`.

If a permission-bit probe reports a path, the engine falls back to kernel access checks
under the image user's credentials, the same credentials used by the scanner (`exec`
without `--user`). Owner/group access and root capabilities therefore count. The fallback
uses `find <path> -type d ! -exec test -r {} \; -print -quit` and a separate `test -x`
probe for directories; files need only `test -r`. Ancestors use the same predicates with
`-maxdepth 0`. No per-entry processes are added when the fast path passes.

The fallback runs under the rules copy's five-minute budget rather than the single-probe
minute, because it forks one `test` per entry and a large template set staged under a restrictive
umask misses the fast path on its first entry.

BusyBox 1.36.1 in Alpine 3.20 rejects GNU `-readable`; `-exec test` works on both. The `;`
terminator is the first shell metacharacter the engine puts in a backend argv, so it was measured
rather than assumed: against WSLC 3.0.3.0, `wslc exec <container> find /tmp -maxdepth 0 -type d !
-exec test -r {} ; -print -quit` exits 0 with no output on an accessible directory, and prints the
path on a mode 000 one while running as uid 1000. The backend passes argv through without a shell.
The engine neither changes image permissions nor adds a user to the scanner contract.
Missing ancestor access, unreadable directories, an empty or partial tree, and unreadable
files have distinct refusals. A nonzero exit, timeout or output limit from either stage
also refuses the scan rather than assuming the rules are usable.

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
