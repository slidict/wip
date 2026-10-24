# Security scan engine

One diagnostic tool, run inside one declared sandbox, behind the boundary that sandbox
declares. The engine owns the lifecycle; a tool adapter owns only its own command line and
its own report format.

The second part of the single-tool penetration testing platform. The boundary itself is
[network isolation](sandbox-network-isolation.md). No adapter exists yet; ZAP is the first one
planned.

## The command

```sh
wip security tools                    # what this build can run
wip security scan <sandbox> --tool <name> --target <address> [--timeout <seconds>]
```

The sandbox and its internal network stay declared in `wip.yml`. The command chooses a tool
and a target, not an environment: the boundary belongs to the configuration, which is what
keeps it out of reach of whatever is being scanned.

**The sandbox must declare the image the tool pins.** The scan is an exec inside that
sandbox, so its image is the tool that actually runs; a plan pinning anything else would
pin nothing, and the recorded provenance would name an image that never executed. Both
`Prepare` and `Run` refuse the pair when they differ, so the scanner sandbox declares the
scanner image and the pull keeps that image current.

`--target` is an address or alias reachable **only inside the declared network** — a
`--network-alias` of another sandbox on it, or its address. A host or public name does not
resolve there, and that is the boundary working rather than a misconfiguration.

The tool's own exit status is forwarded. Scanners use it to report findings, so flattening it
to 0 would hide what a scan found. The report is written to stdout exactly as the tool wrote
it; provenance (tool, sandbox, network, pinned image, rules version, status) goes to stderr as
one line.

A scanner's own streamed progress goes to stderr too. The scan path asks the backend for
that, rather than changing the shared runner, because the same backend serves the sandbox,
volume and network commands, where stdout is the right place for it.

## The adapter contract

`ISecurityScanner` and `ScanEngine` are `internal`, visible only to wip's own CLI and test
assemblies. #692 rules out an external plugin API or SDK, so this is a shape for the adapters
compiled into wip — not something anyone outside it can implement or discover.

An adapter returns a `ScanPlan` and nothing else:

| Field | What it is |
| --- | --- |
| `Image` | The pinned image the tool comes from |
| `Argv` | The command to run inside the sandbox, passed to the backend directly, never through a shell |
| `ReportPath` | Where inside the container the tool writes its report |
| `RulesVersion` | The version or digest of the rule, template or add-on set applied |

**There is no field through which an adapter reaches the backend's own options.** It cannot
attach a network, mount a path, or set an environment, because the engine builds every backend
invocation itself from the declared sandbox and the plan. That is how #692's "アダプターから
隔離ポリシーを緩和できない" is enforced — by the type, not by a rule to remember. Whatever an
adapter puts in `Argv` lands after the container id, where the backend reads it as the
command's arguments and never as its own flags.

Registration is a static table in `ScannerRegistry`: no reflection, no assembly scanning, no
configuration key that can add an entry. A tool arrives by a reviewed pull request that adds a
line there, which is what makes its code, dependencies, image, permissions, traffic and result
handling reviewable at all.

Exactly one tool per run is a property of the signature rather than a check: `Prepare` and
`Run` take one `ISecurityScanner`, so two at once is not expressible.

## Phases

#692 asks for preparation, diagnosis, collection and teardown to be separated. The separation
is what makes a fail-closed network workable: a pinned image has to be fetched where there is
egress, and a scan sandbox has none.

| Phase | Network | What happens |
| --- | --- | --- |
| `Prepare` | Outside the isolated sandbox | The plan is validated and the pinned image is pulled into the local image store |
| `Run` | Inside it | The tool executes, behind a boundary verified immediately beforehand |
| Collect | None needed | The report is read from `ReportPath` |
| Cleanup | — | The sandbox is stopped, on the failure path too |

`Run` requires the plan `Prepare` returned, so a caller cannot reach a scan without having
prepared — and re-validates it, because being handed a plan is not evidence that it is a valid
one.

Cleanup stops the container and does not remove it. Stopping takes the tool and whatever it
spawned with it, which is what "子プロセスを含む停止" needs; removal stays `sandbox destroy`'s,
so a failed scan leaves the sandbox inspectable. A failure while stopping is swallowed rather
than rethrown, because replacing the scan's own outcome with a cleanup error would lose the
result.

## Fail closed

Nothing executes in the sandbox until every one of these holds. The state a bug would
otherwise produce is a scan that ran somewhere it could reach the internet.

| Condition | Result |
| --- | --- |
| `--tool` names nothing registered, or is absent | Refused, listing what is registered |
| The sandbox declares no network | Refused; there is no unrestricted mode to fall back to |
| The sandbox's declared image is not the one the plan pins | Refused before the pull, and again in `Run` |
| The image is not pinned (`latest`, `stable`, `edge`, `main`, `master`, `nightly`, or no tag) | Refused before the pull |
| No rules version is given | Refused; an unpinned rule set is fetched at scan time, which the boundary forbids |
| The report path is not a canonical absolute path | Refused |
| `Argv` is empty, or carries control characters | Refused |
| The pull fails | Refused; an isolated sandbox cannot fetch the image later |
| The network cannot be verified as owned and internal | Refused |
| The sandbox is not running | Refused |
| The sandbox is attached to anything other than exactly its declared network | Refused |
| The timeout is not positive | Refused |

The boundary checks close a gap the isolation work left open (slidict/wip#263): `sandbox
create` refuses a container that is not behind its declared boundary, but `exec` did not,
and a scan is an `exec`. A container created before `network:` was added to `wip.yml` is
still on whatever it was given.

That check now lives in `SandboxLifecycle.RunningId`, the guard `exec`, `capture`,
`attach`, the relay and this engine all pass through, rather than in the engine alone — so
it holds for every way into a running sandbox and not only for a scan. The engine keeps its
own check as well, which is where the refusal names the scan. A sandbox declaring no
network is unaffected, so this costs nothing where there is no boundary to verify.

## What is tested, and what is not

`tests/Wip.Tests/ScanEngineTests.cs` covers every row above against a fake backend and a stub
scanner, so it runs with no WSLC and no registered tool. It also asserts the structural
property directly: an adapter whose `Argv` contains `--network bridge` and `-v /:/host` has
those land as the command's arguments after the container id, with no `run` invocation and no
backend flag of the adapter's anywhere.

The registry is asserted to be empty, so an entry appearing without a pull request to that
file is a test failure rather than a surprise.

**There is no end-to-end test with a real scanner here, because there is no adapter yet.**
Registering a test-only one at build time would be exactly the arbitrary tool addition #692
rules out, so the real-WSLC scan e2e lands with the first adapter. What this change
does keep green is the existing isolation e2e.
