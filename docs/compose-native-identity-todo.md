# TODO: reconcile `compose-native` resource identity with `wslc compose`

## Status: deferred deliberately — keep wip's scheme, with the conditions to revisit below

`wslc compose` fixed a resource-identity scheme upstream in
[microsoft/WSL#42086](https://github.com/microsoft/WSL/pull/42086), merged 2026-11-02 onto the
unreleased `feature/compose` branch. wip's `compose-native` mode uses a different one. Nothing
is being changed now; this note exists because the divergence has a migration cost that is
cheap to write down and expensive to discover during a migration.

**Upstream facts below are as recorded by the tracking check in #120 on 2026-11-14**
(`feature/compose` head `dcf514b5`, latest stable release `3.0.5` of 2026-11-03, which ships no
`wslc compose`, and [#41526](https://github.com/microsoft/WSL/pull/41526) still open and draft).
Re-read that Issue rather than trusting this paragraph's date: it is the tracking Issue, this is
a snapshot. Every wip-side claim is cited to `file:line` and was verified against `f466afa`.

## The two schemes

| | wip `compose-native` | upstream as of `#42086` |
|---|---|---|
| network name | `compose.project` if set, else the name of the directory holding `wip.yml`, **verbatim** | `<project>_default`, from a **normalized** project directory name |
| container name | the compose service name, **verbatim** | `<project>-<service>-1` |
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

Leaving it unrecorded has a cost of its own. Once `wslc compose` ships, wip-created resources
carry no WSLC labels and different names, so `wslc compose` cannot see them. A project that
switches would **strand** its old containers and network and get a second set alongside them,
with both sets running. That is the hazard worth having on record before someone meets it.

### Rejected

- **Align names and labels now.** Breaks every running project, to match a convention that is
  not released and not frozen.
- **Add only the labels, keep the names.** Cheaper, and still wrong in the same direction:
  upstream identifies a project by its normalized directory name, so labels whose values do not
  match that identity would not make the resources recognizable to `wslc compose` either. It
  would be churn that looks like progress.
- **Record nothing and re-check later.** The divergence is discoverable from the code; the
  migration hazard is not, and that is the part that costs time.

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
