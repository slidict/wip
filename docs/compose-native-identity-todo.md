# TODO: reconcile `compose-native` resource identity with `wslc compose`

## Status: deferred deliberately — keep wip's scheme, with the conditions to revisit below

`wslc compose` fixed a resource-identity scheme upstream in
[microsoft/WSL#42086](https://github.com/microsoft/WSL/pull/42086), merged 2026-11-02 onto the
unreleased `feature/compose` branch. wip's `compose-native` mode uses a different one. Nothing
is being changed now; this note exists because the divergence has a migration cost that is
cheap to write down and expensive to discover during a migration.

Three kinds of claim appear below, and they are not equally strong — read the distinction before
relying on any of them.

- **wip-side claims** are cited to `file:line` and were verified against `f466afa`.
- **Upstream naming and discovery claims** were read from the branch at `dcf514b5`:
  `ComposeNormalizer.cpp` for the network and container name formats and the project-name
  normalization, `ComposeSpec.cpp` for `name` / `container_name`, and `WSLCSession.cpp` for how
  a project's containers are discovered. Files are named where each claim is used.
- **Release and branch state** is as recorded by the tracking check in #120 on 2026-11-14:
  `feature/compose` head `dcf514b5`, latest stable release `3.0.5` of 2026-11-03 shipping no
  `wslc compose`, and [#41526](https://github.com/microsoft/WSL/pull/41526) open and draft.
  Re-read that Issue rather than trusting this paragraph's date — it is the tracking record and
  this is a snapshot of it.

One claim is explicitly weaker than the rest and is marked where it appears: what upstream does
with an existing network that has the right name and none of its labels.

## The two schemes

| | wip `compose-native` | upstream as of `#42086` |
|---|---|---|
| network name | `compose.project` if set, else the name of the directory holding `wip.yml`, **verbatim** | `<project>_default` — `std::format("{}_default", projectName)` in `ComposeNormalizer.cpp` |
| project name | not derived; the directory name is used as-is | the directory's filename through `NormalizeProjectName()`: lower-cased, every character except alphanumerics, `-` and `_` removed, leading non-alphanumerics stripped, length capped |
| container name | the compose service name, **verbatim** | `<project>-<service>-1` — `"{}-{}-1"`, and **only as a default**: it is applied `if (container.Name.empty())`, so a service's own `name` or `container_name` (`ComposeSpec.cpp` prefers `name`) overrides it |
| ownership labels | **none** | `com.microsoft.wslc.compose.*` (WSLC-owned, not `com.docker.compose.*`) |

### Where wip decides this

- **Network name** — `src/Wip.Core/Configuration/Config.cs:102`:
  `Network` returns `ComposeProject ?? DefaultComposeNetwork` when
  `IsComposeNative` (`:113`, `mode: compose-native`). `ComposeProject` is `compose.project`
  from `wip.yml` (`:121`). `DefaultComposeNetwork` (`:283`) is
  `new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(Path))!).Name` — the directory name
  as it is on disk, with no normalization and no suffix.
- **Container name** — `src/Wip.Core/Execution/CommandBuilder.cs:84` passes
  `RequiredContainer()` to `wslc run --name`, and `RequiredContainer()` (`:443`) is
  `config.Container`, which under `compose-native` is `ComposeService` — the service name
  verbatim (`Config.cs:94`). Dependencies take the same shape at `CommandBuilder.cs:212`,
  where the service name is passed straight through as `--name`.
- **Labels** — there are none. `CommandBuilder.Up` (`:81`) and `DependencyUp` (`:209`) build
  `wslc run` with `--name` and `--network` and no `--label` at all.

### wip already has an ownership convention — `compose-native` just does not use it

Everything else wip creates carries `io.slidict.wip.owner=v1:<namespace>:<kind>:<name>` on a
hashed backend name:

| Resource | Label constant | Identity value | Backend name | Applied at |
|---|---|---|---|---|
| network | `NetworkLifecycle.cs:37` | `:47` | `:41` (`wip-n-` + 40 hex of SHA-256) | `:80` (`network create --label`) |
| sandbox | `SandboxLifecycle.cs:72` | `:82` | `:76` (`wip-s-` + 40 hex of SHA-256) | `:170` (`run --label`) |
| volume | `VolumeLifecycle.cs:31`, plus `:32`-`:34` for policy, instance and mirror | — | — | `:94` (`volume create --label`) |

So `wslc run --label` and `wslc network create --label` are already known to work here. The gap
is not capability.

## Why this is a decision rather than a rename

Aligning the names today is a **breaking change for every running project**: the network and
every container would be renamed, so existing resources are orphaned at the moment a user
upgrades. And the target is not yet fixed — the upstream scheme lives only on a branch that is
in no release, with the `#41526` stack still partly unmerged, so wip would be made
breaking-compatible with something that can still change.

Leaving it unrecorded has a cost of its own. There are two distinct migration hazards, and they
fail in opposite directions.

**A second set alongside the first.** Once `wslc compose` ships, wip-created resources carry no
WSLC labels and names that usually differ, so `wslc compose` does not recognize them. A project
that switches gets a second network and a second set of containers while the originals keep
running, and nothing points at the first set any more.

**A name collision that blocks instead.** The two schemes can also land on the *same* network
name, because wip uses `compose.project` verbatim and upstream appends `_default`: a project in
a directory named `foo` that sets `compose.project: foo_default` already occupies the name
upstream will want. That network carries none of the WSLC labels, and upstream validates
ownership and metadata-version labels on the network it finds. So the likely outcome there is a
refusal rather than a duplicate — `wslc compose` stops on a network it will not adopt.

The second hazard's *consequence* is the one claim here taken on report rather than read: the
collision itself follows from wip's own code (`Config.cs:102`), but which branch upstream takes
for an unlabeled network of the right name was not verified in this repository. It is recorded
because a refusal and a duplicate need different recovery, and a reader meeting either should
know both are possible.

### Rejected

- **Align names and labels now.** Breaks every running project, to match a convention that is
  not released and not frozen.
- **Add only the labels, keep the names.** This is a genuine partial step, not a useless one,
  and it is worth being accurate about why it is still not the move. Upstream discovers a
  project's containers by filtering on the compose **project label** and then validating the
  metadata version and the `service` / `containerNumber` labels — it does not require the
  generated physical name (`WSLCSession.cpp`). So labels carrying the normalized project name
  would make wip's containers discoverable even while their names differ. What it does not fix
  is the network, which upstream resolves by name (`<project>_default`), and the rest of the
  lifecycle: reconciliation, the container-number convention, and the operation state upstream
  keeps per project. Adding labels alone would leave a project half-adopted, which is harder to
  reason about during a migration than a clean separation — and it would still be guessing at
  label values that are not in any release.
- **Record nothing and re-check later.** The divergence is discoverable from the code; the two
  hazards above are not, and that is the part that costs time.

## What would make this worth doing

Alignment becomes a real task when all of these hold:

1. `wslc compose` is in a **stable** release, not only on `feature/compose`.
2. The identity scheme is unchanged from `#42086` in that release, or its final form is known.
3. Upstream Compose covers enough of wip's feature set for a project to actually move —
   `build`, `depends_on`, `healthcheck`, `entrypoint`, `run`, `exec` and `logs` are all absent
   today, so there is nothing to move to yet.

When they do, the change is not only a rename: it needs a migration path for resources already
created under the current scheme, so that upgrading does not silently leave a second set behind.
Decide that path at the same time, not afterwards.

Delegating `compose-native` to `wslc compose` is tracked separately in #120 and stays out of
scope here. This note records identity and ownership only, and changes no behavior.
