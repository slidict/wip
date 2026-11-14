# TODO: reconcile `compose-native` resource identity with `wslc compose`

## Status: deferred deliberately — keep wip's scheme, with the conditions to revisit below

`wslc compose` fixed a resource-identity scheme upstream in
[microsoft/WSL#42086](https://github.com/microsoft/WSL/pull/42086), merged 2026-11-02 onto the
unreleased `feature/compose` branch. wip's `compose-native` mode uses a different one. Nothing
is being changed now; this note exists because the divergence has a migration cost that is
cheap to write down and expensive to discover during a migration.

Three kinds of claim appear below, and they are not equally strong — read the distinction before
relying on any of them.

- **wip-side claims** are cited to `file:line`, verified against `f466afa`. Verify them the
  same way they were written, with `git show f466afa:<path> | grep -n`, which counts the exact
  bytes of the blob. A line number read off a rendered or converted copy of the file is not the
  same measurement and has already disagreed with the blob twice.
- **Upstream behaviour claims** were read from the branch at `dcf514b5`:
  `ComposeNormalizer.cpp` for the network and container name formats and the project-name
  normalization, `ComposeSpec.cpp` for `name` / `container_name`, and `WSLCSession.cpp` for
  discovery, the labels set at creation, and both collision paths. Each claim names its file,
  and the `WSLCSession.cpp` line numbers are from the 4887-line blob at that revision.
- **Release and branch state** is as recorded by the tracking check in #120 on 2026-11-14:
  `feature/compose` head `dcf514b5`, latest stable release `3.0.5` of 2026-11-03 shipping no
  `wslc compose`, and [#41526](https://github.com/microsoft/WSL/pull/41526) open and draft.
  Re-read that Issue rather than trusting this paragraph's date — it is the tracking record and
  this is a snapshot of it.

Nothing here is left as inference. An earlier revision of this note hedged the two collision
outcomes because they had not been read; both have since been read at the revision above and
are stated with the code that decides them.

## The two schemes

| | wip `compose-native` | upstream as of `#42086` |
|---|---|---|
| network name | `compose.project` if set, else the name of the directory holding `wip.yml`, **verbatim** | `<project>_default` — `std::format("{}_default", projectName)` in `ComposeNormalizer.cpp` |
| project name | not derived; `compose.project`, else the directory name, used as-is | `ComposeNormalizer::Normalize` prefers an explicit project name when one is supplied (`if (documents.ExplicitProjectName.has_value())`) and derives it from the directory's filename only otherwise — so **both** schemes have an override, and both inputs land in the same places |
| project-name normalization | none | `NormalizeProjectName()`: lower-cased, every character except alphanumerics, `-` and `_` removed, leading non-alphanumerics stripped. It is **validated, not trimmed** — an empty or overlong result throws `E_INVALIDARG` (`result.empty() \|\| result.size() > WSLC_MAX_COMPOSE_PROJECT_NAME_LENGTH`), so a long directory name fails rather than being shortened |
| container name | the compose service name, **verbatim** | `<project>-<service>-1` — `"{}-{}-1"`, and **only as a default**: it is applied `if (container.Name.empty())`, so a service's own `name` or `container_name` (`ComposeSpec.cpp` prefers `name`) overrides it |
| ownership labels | **none** | set at creation in `WSLCSession.cpp:2705`-`:2709`: `c_project` (the project name), `c_service` (the service name), `c_containerNumber` (`"1"`) and `c_metadataVersion`. WSLC-owned keys, not `com.docker.compose.*` |

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

**A network-name collision that blocks instead.** The two schemes can also land on the *same*
network name, because wip uses `compose.project` verbatim and upstream appends `_default`: a
project in a directory named `foo` that sets `compose.project: foo_default` already occupies the
name upstream will want, carrying none of the labels upstream expects. That case is refused, not
adopted, and it is refused before anything is created:

- `WSLCSession::CreateComposeContainers` (`WSLCSession.cpp:2653`) calls
  `DeleteNetworkImpl(networkName, spec.ProjectName)` and aborts on any failure other than
  `WSLC_E_NETWORK_NOT_FOUND`, before it builds the new network.
- `WSLCSession::DeleteNetworkImpl` (`:3808`, check at `:3836`-`:3840`) requires all three of
  `c_project` (the expected project), `c_network` (`"default"`) and `c_metadataVersion` to match
  on the network it found. If any is missing or different it throws
  `HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)` with `MessageWslcComposeNetworkConflict`.

So a correctly labelled network is removed and recreated — that is upstream's adopt path — while
wip's unlabelled one of the same name stops the whole operation.

**A container-name collision, independently of the network.** Upstream generates
`<project>-<service>-1` only when a service names nothing, so a `compose.yml` that sets
`container_name` to the string wip already uses — the service name, verbatim from
`CommandBuilder.cs:84` — asks upstream to create a container that exists. That also fails rather
than producing a second set: `CreateContainerImpl` (`WSLCSession.cpp:2763`) maps a Docker HTTP
`409` to `HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)` (`:2847`), and `CreateComposeContainers`
retries only the image-not-found case, propagating everything else with `THROW_IF_FAILED`
(`:2752`). It fails **even when the two network names differ**, so it has to be checked
separately rather than assumed to be covered by the network case.

Both paths were read at `dcf514b5`; neither consequence is inferred.

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
