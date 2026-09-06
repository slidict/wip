# Compose upstream compatibility

wip's `mode: compose-native` reads compose.yml itself and drives `wslc` directly, because
`wslc` had no Compose support when it was written. It now has some: `microsoft/WSL`'s
[`feature/compose`](https://github.com/microsoft/WSL/tree/feature/compose) branch carries a
`wslc compose` POC. This directory is what makes the eventual move off `compose-native` onto
that implementation a measured change rather than a leap.

It answers one question, and keeps answering it as upstream moves:

> **When Microsoft's Compose implementation changes, which fixture shows me the difference?**

## What is here

| Path | What it is |
|---|---|
| `fixtures/<name>/compose.yml` | A shared Compose fixture — the *input* both implementations are asked about |
| `fixtures/<name>/wip.yml` | The `mode: compose-native` config that points wip at it |
| `fixtures/<name>/expected.json` | Both readings of that fixture, plus the argv wip would run |
| `matrix.json` | Per fixture, per feature: `compatible` / `known-difference` / `upstream-unsupported` / `wip-only`, each with a note |
| `differences.md` | **Generated.** The human-readable difference list, rendered from the matrix and the two readings |
| `upstream/PINNED.md` | Which upstream commit was read, which files, and the rules extracted from them |

The code lives in [`../Wip.Tests/ComposeCompat/`](../Wip.Tests/ComposeCompat/) and
[`../Wip.Tests/ComposeUpstreamCompatTests.cs`](../Wip.Tests/ComposeUpstreamCompatTests.cs).

## What a fixture is compared on

Loading the YAML is not the interesting part — what each side *makes of it* is. Both
implementations are projected onto one semantic model:

```
project, network, port_binding_default, start_order, services_excluded_by_profiles
services.<name>:
  container_name, image, command_argv, environment, working_dir,
  mounts, ports, network_aliases, build, healthcheck, restart
```

wip's half is read back out of the argv it would hand `wslc` — `wslc run --name … -e … -p …
-v … IMAGE ARGV…` — so nothing can drift between what is recorded here and what actually runs.
That argv is kept verbatim in `expected.json` under `wip_invocation` so a reviewer can check
the model against the command line. A `-p` or `-v` string wip forwards is then read with
`wslc`'s *own* CLI parser rules, since that is what decides what the string ends up meaning.

Upstream's half comes from `UpstreamComposeInterpreter`, a reading of the pinned commit. It is
a comparison aid, never a source of behaviour: nothing under `src/` references it.

## Statuses

| Status | Means | What the suite asserts |
|---|---|---|
| `compatible` | Both readings agree | They must still agree |
| `known-difference` | Both have an opinion and they differ | They must still differ |
| `upstream-unsupported` | A Compose Specification behaviour `wslc compose` rejects today | Upstream must still reject the fixture |
| `wip-only` | wip implements it; upstream has no notion of it | Upstream must still reject it, and wip must still accept it |

**Matching upstream is deliberately not the pass condition.** `feature/compose` is an
in-progress POC and freezing wip against it would pin wip to upstream's TODOs. What has to hold
is that every difference is one `matrix.json` already names — including in the direction that
usually goes unnoticed, a difference that *stops* being a difference. If upstream fixes its
scalar-`command` splitting, `02-command-shell-form` fails with "recorded as a known difference,
but they now agree", which is the signal to go and read upstream again.

## When the two disagree, who is right?

In this order:

1. **The Compose Specification.**
2. **Behaviour Microsoft has deliberately settled** — a validation it chose, not a stub.
3. **wip's existing behaviour.**

A `// TODO` or an obvious placeholder in upstream is never a compatibility target. Those are
listed in [`upstream/PINNED.md`](upstream/PINNED.md) so they can be recognised on sight; the
note on each matrix entry says which of the three applies and, where wip is the one that is
wrong, says so.

## Working with it

Run the suite (it is part of the normal test run — no separate command needed):

```bash
dotnet test --project tests/Wip.Tests/Wip.Tests.csproj --filter ComposeUpstreamCompatTests
```

Re-record every `expected.json` and `differences.md` from the current interpreters:

```bash
WIP_COMPOSE_COMPAT_UPDATE=1 dotnet test --project tests/Wip.Tests/Wip.Tests.csproj
```

Every comparison skips while that variable is set, so a run either records or checks, never
half of each. **Read the diff before committing a re-recording** — that diff is the whole
point of the corpus.

### Adding a fixture

1. `mkdir fixtures/NN-slug`, add `compose.yml` and a `wip.yml` naming one of its services.
2. Add a `matrix.json` entry: a `description`, and a `features` block with a status and a note
   for each feature the fixture is there to exercise. Only the features you list are asserted,
   so a fixture that only exists to pin one key stays readable.
3. Re-record. Check the recording says what you expected, then commit fixture, recording,
   matrix entry and regenerated `differences.md` together.

### After upstream changes

1. Re-read `feature/compose` against the numbered rules in [`upstream/PINNED.md`](upstream/PINNED.md).
2. Update `UpstreamComposeInterpreter` and the pin.
3. Re-record and read the diff: every fixture whose relationship to upstream changed shows up
   there. Re-classify those matrix entries.
4. A `wip-only` or `upstream-unsupported` fixture that upstream now accepts fails loudly rather
   than passing quietly — that is upstream having implemented the feature, and the entry needs
   a new status.

### Adding a feature upstream doesn't have yet

Add the fixture, mark it `wip-only`, and say in the note what would have to be true upstream
for a project using it to migrate. `UpstreamComposeInterpreter` needs no change: upstream's
parser rejects unknown keys, so the fixture records as an error on that side on its own.

## Not covered here

This corpus is about *meaning*, not execution. It does not start containers — that is
[`tests/e2e`](../e2e/README.md) — and it does not pin wip's own argv against the Ruby
implementation that preceded it, which is [`tests/golden`](../golden/README.md).
