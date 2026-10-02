# Sandbox and volume contract

This is the configuration and ownership contract for workspace slidict/workspace#194 (parent slidict/workspace#192).
The configuration model, validation, [named sandbox lifecycle](sandbox-lifecycle.md),
[declared volume storage](volume-lifecycle.md) and mount integration are implemented.
Loading YAML or running `wip config` never creates, mounts, snapshots or deletes resources.
The existing `up`, `down`, `run`, `exec` and `sync` commands still target legacy dependencies;
they do not operate on these new declarations. Sandbox execution belongs to slidict/workspace#195,
volume lifecycle to slidict/workspace#196, mount integration to slidict/workspace#197, and workspace adoption to slidict/workspace#198.
Those management Issues live in the private workspace repository. Public implementation
PRs should describe the technical change without publishing team credentials or local paths.

## Schema, version 1 additive extension

```yaml
version: 1
resource_namespace: example-project
volumes:
  - name: project
    persistent: true
    mount: /workspace
  - name: private-state
    persistent: true
    mount: /home/user/state
  - name: scratch
    persistent: false
    mount: /tmp/work
sandboxes:
  - name: first
    image: example/tool:1
    volumes: [project, private-state, scratch]
  - name: second
    image: example/tool:1
    volumes: [project]
```

`SandboxSettings`, reached through `Config.SandboxResources`, is the validated typed
contract. `ConfigLoader` validates it on load and `Config.ToMapping` preserves it.
Both collections are sequences with explicit names: duplicate resource names can be
rejected before a YAML mapping overwrites them. The mapping-shaped `sandboxes` in the
parent's illustrative proposal must be rewritten as the sequence above. There is no
inference from an agent name or a `shared`, `agent` or `sandbox` purpose mode.

| Field | Contract |
|---|---|
| `resource_namespace` | Required for nonempty resource declarations; explicit stable project identity |
| `volumes` | Optional sequence; each entry has exactly `name`, `persistent`, `mount` |
| `persistent` | Required YAML boolean; never silently default to destructive ephemeral storage |
| `mount` | Required canonical absolute Linux path; no control characters, empty segments, trailing slash (except `/`), `.` or `..` |
| `sandboxes` | Optional sequence; each entry has `name`, `image`, optional `volumes` |
| `image` | Required nonempty string without control characters; backend support is checked by execution later |
| sandbox `volumes` | Optional sequence of declared volume names; omitted means no mounts |

Names and namespace match `[a-z][a-z0-9_-]{0,62}` and compare ordinally. Duplicate
names, repeated references, undefined volumes and equal mount destinations **within
one sandbox** are rejected. Equal destinations in different sandboxes are valid.
Ancestor/descendant mounts are not equal-path conflicts; nested mounts are ordered
so shallower paths precede deeper paths before container creation. Unknown fields inside either resource
declaration are rejected, including agent/purpose/mode labels. Backend errors do not
permit silently changing the declared mount path or persistence.

## Responsibility and ownership

Resource identity is `(resource_namespace, resource kind, name)`. slidict/workspace#195/#196 must map it
to collision-safe backend names and persist ownership metadata; deleting by an unscoped
name or adopting an unrelated existing resource is prohibited. Choose separate namespaces
for independent projects. Moving the config directory does not change identity; changing
namespace is an explicit migration, not permission to delete the old data.

Sandbox owns its container and process lifetime. It may reference many volumes and execute
an arbitrary argv command. Creation/exit/destruction never implicitly deletes persistent
volumes. Volume owns its storage lifetime; mounting it does not transfer ownership to a
sandbox. Configuration declaration alone is not creation, deletion or permission to delete.

Persistent volumes survive sandbox stop, destruction and recreation. Deletion must be an
explicit scoped volume operation, reject active users, and report failures without removing
unrelated storage. Ephemeral volumes exist for a use cycle: create before the first attach,
retain while any sandbox has a live reference, and clean up after the last confirmed detach.
Multiple sandbox references are allowed for either kind. Failed creation or unknown backend
state must not be treated as a confirmed detach. Durable ownership and
reconciliation are implemented before cleanup; removing a declaration from YAML is not an implicit prune.

Mount owns the relationship, target path and attach/detach ordering. Sandbox lifecycle resolves the
validated name references; it neither chooses storage policy nor gives a volume an agent
role. Shared `project` above emerges from two references, and `private-state` from one.
An ephemeral volume's data is not promoted to persistent on a failed cleanup.

## Compatibility and migration

Legacy `mode: container/compose/compose-native`, `container`, `dependencies`, command
inheritance and `sync` retain their behavior. This does not add a new mode or remove those
existing orchestration choices. `dependencies.<name>.volumes` remains the existing raw
bind-mount argument list; it is distinct from the new top-level resource declarations.
Compose-native's compose.yml volumes are not automatically converted or adopted.
Old normalized config output gets no extra resource keys when the extension is absent.

Migrating is deliberate: choose a namespace; declare storage and explicit persistence;
list sandbox images and volume references; validate via `wip config`; then
opt into their explicit resource commands (`wip volume` / `wip sandbox`). Do not silently reinterpret legacy bind
mounts or `sync.volume`. Existing CommandBuilder and SyncSettings continue their current
contracts; they are not lifecycle implementations for the new resources. This PR preserves
legacy execution rather than prematurely connecting the new schema to it.

## Snapshot/restore decision for slidict/workspace#196

Snapshot/restore is **not required for the initial slidict/workspace#192 acceptance criteria**: retaining
persistent data across sandbox recreation and deleting ephemeral data are lifecycle
guarantees, not backups. No snapshot fields or promised command are introduced here.
Existing config/CommandBuilder/SyncSettings expose neither a generic storage snapshot
contract nor a restore transaction; a source sync is not a consistent backup.

slidict/workspace#196 should first implement and verify persistence, reference protection and cleanup.
If recovery beyond retained storage becomes a requirement, create a separately estimated
snapshot/restore unit before implementing it. Its data scope must be one explicitly selected
owned volume; it must define quiescing all users, destination/retention, backend capability,
and restore into an inactive volume with rollback. It must not capture whole host homes,
credentials or unrelated mounted volumes. Unsupported backend capability must fail explicitly;
do not substitute a live filesystem copy and claim snapshot consistency. This decision and
its rationale satisfy the conditional snapshot/restore handoff in slidict/workspace#194/#196 without silently
dropping a mandatory parent criterion.

## Verification and handoff

`SandboxSettingsTests` exercises resource sharing, invalid configuration, canonical mount
paths, namespace identity, normalized YAML round-trip and coexistence with legacy bind mounts.
Run `dotnet test tests/Wip.Tests/Wip.Tests.csproj --configuration Release` and the existing
build/AOT CI. No live sandbox or credential access is needed for this schema-only Issue.
slidict/workspace#195/#196 can use the typed definitions and this ownership contract independently;
slidict/workspace#197 integrates their runtimes; slidict/workspace#198 adds actual workspace config and verifies all clients.
Completing a child Issue does not close parent slidict/workspace#192 or claim the remaining runtimes exist.
