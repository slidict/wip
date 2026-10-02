# Declared volume lifecycle

This implements storage under [the sandbox/volume contract](sandbox-volume-contract.md).
Mount ordering and sandbox integration are implemented via `SandboxLifecycle`. There are no agent
roles or purpose modes. Declare explicit persistence and a canonical mount target:

```yaml
version: 1
resource_namespace: example
volumes:
  - name: data
    persistent: true
    mount: /data
  - name: scratch
    persistent: false
    mount: /scratch
```

```powershell
wip volume create data
wip volume status data
wip volume create scratch
wip volume reconcile scratch
wip volume destroy data
```

| Operation | Contract |
| --- | --- |
| create | Create a new generation if absent; reuse only ownership- and policy-verified storage; no data reset |
| status | Print logical name, exists/not found, policy, backend generation, reference count and observed-use flag |
| destroy | Explicitly delete owned, unreferenced storage, including persistent; missing is success |
| reconcile | Observe usage; delete ephemeral only after recorded use and the final confirmed detach; retain persistent |

Every command requires a current declaration. Removing a YAML entry never prunes anything.
Changing `persistent` for existing storage is rejected; it does not silently promote or
demote data. Keep the old declaration for explicit destruction or plan a data migration.
Configuration loading and `wip config` perform no storage operation.

## Identity and reference protection

Each generation is named `wip-v-<24 hex SHA-256 identity prefix>-<32 hex random UUID>`.
The hash input is `<namespace>:volume:<logical name>`; creation persists three labels:
`io.slidict.wip.owner=v1:<namespace>:volume:<name>`, `io.slidict.wip.persistent=true|false`,
and `io.slidict.wip.instance=<UUID>`. All three labels and the complete name are verified
on reuse and before deletion. Each later generation gets a new name, so a delayed old
cleanup cannot target a normal recreation. Multiple generations or conflicting metadata
are errors; the runtime never chooses or deletes an ambiguous candidate.

References are actual backend container mounts, including other clients' containers and
stopped containers; YAML references alone are not attached storage. The runtime lists all
containers, inspects each ID, validates short/full IDs, states and mount metadata, and
counts every named-volume reference. Deleted tombstones are absent. Failed or incomplete
scans and unknown states retain storage. Explicit destruction rejects any reference.
WSLC's non-force volume removal supplies the final in-use check if a new attach races the
scan. No force or session-wide prune is issued.

WSLC volumes have names rather than immutable deletion IDs. Wip reserves its generated
names and never reuses them. External clients may mount these volumes, but must not remove
and recreate the same backend name or alter ownership labels behind wip. The pre-removal
metadata recheck detects observed changes; WSLC does not offer atomic label-conditioned
deletion against external name replacement. Use the lifecycle API for storage changes.

## Ephemeral use cycles and durable reconciliation

1. Create before the first attach. An unused ephemeral volume is retained so an early
   reconciliation cannot remove storage prepared for a pending attach.
2. After successful attach, call `Status` or `Reconcile` to observe the backend reference.
   The complete successful scan records use of that immutable generation durably.
3. Retain while any reference remains. Container exit alone is not detach.
4. After removing the final referencing container, call `Reconcile`. It repeats ownership
   and reference checks, then removes only the previously observed ephemeral generation.

The future mount integration must invoke these hooks after attach and confirmed detach;
there is no background polling daemon. Direct backend users need the same explicit hooks.
Persistent volumes are always retained by reconciliation and sandbox destruction.

Usage observations live in `.wip/volume-usage/` next to the resolved `wip.yml`, keyed by a
hash of the complete generation name. This repository ignores the directory; consuming
projects must add `**/.wip/volume-usage/` to their own `.gitignore` to cover nested configs too. The journal contains versioned
JSON observations and an exclusive lease file, not volume contents. `FileVolumeUsageStore`
holds a lease through the whole CLI operation (10-second acquisition deadline), flushes
observations to disk and atomically publishes them. API callers must hold this shared
store/lease for the entire `VolumeLifecycle` call. Clients sharing a namespace should use
the same config directory/journal. Independent journals can race first creation and leave
multiple generations; the runtime then fails closed rather than choosing data to remove.

Missing observations after moving configuration, journal loss, or an unobserved attach
mean retain, not permission to clean up. Corrupt or inaccessible observations are errors.
Old observations cannot authorize deletion of a new UUID generation. Preserve the journal
across ordinary process restarts; data retention across sandbox recreation does not depend
on the journal for persistent volumes.

## Failure and recovery

Ownership probes have a 10-second deadline; the entire reference scan shares one 10-second
deadline and inspects IDs in batches of at most 100. Incomplete, duplicate or mismatched
batch responses retain storage. Create/remove have a 120-second deadline. Mutation exit
codes are returned unchanged. A failed/timed-out create may have left owned storage: run
status with the same declaration and repeat create to reuse it. There is no destructive
rollback. On failed remove, check status and references before retrying. If the reference
scan or journal is unavailable, restore access and reconcile; do not assume detach.
After confirmed backend absence, only that removed generation's usage marker is discarded.
A failed marker removal reports that storage is already gone; it never removes other markers.

If usage was never recorded and the generation is now unattached, automatic cleanup stays
disabled; explicit `volume destroy <name>` is the recovery action after checking the data
is disposable. Namespace changes do not authorize cleanup of old storage. Preserve the old
config/journal for recovery. Ambiguous generations or ownership mismatch require manual
investigation and backend metadata inspection; there is no automatic broad cleanup.

Snapshot/restore remains out of scope by the explicit slidict/workspace#194 decision:
retaining persistent storage is not a backup. A future snapshot unit must specify quiescence,
data scope, retention and transactional restore/rollback; live copying is not substituted.

## Verification and handoff

`VolumeLifecycleTests` cover repeat/missing operations, all references, stale generation
cleanup, ownership/policy mismatches, partial failures and unknown state. Journal tests
cover restart durability and corrupt observations. The dedicated backend E2E uses temporary
Alpine consumers and data: it verifies persistent data after container recreation, protects
two ephemeral references, reconciles after each detach, and confirms the next cycle is empty.
It calls storage CLI commands from separate processes to exercise journal durability.
Consumers are inspected for the fixture label before removal; finalizers surface cleanup
failures and preserve the scratch config/journal for recovery. No real user data is used.

For slidict/workspace#197, `SandboxLifecycle` uses `VolumeLifecycle.Create/Status/Destroy/Reconcile` and
backend names for volume attachment and post-detach cleanup. Sandboxes now mount declared volumes,
reconciling usage on creation and cleaning up unreferenced ephemeral storage on destruction.
