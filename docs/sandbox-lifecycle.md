# Named sandbox lifecycle

The runtime implements the sandbox portion of [the resource contract](sandbox-volume-contract.md).
It has no built-in agent roles. Declare an explicit namespace and a sandbox image whose default
CMD/ENTRYPOINT keeps the container running:

```yaml
version: 1
resource_namespace: example
sandboxes:
  - name: first
    image: example/tool:1
```

```powershell
wip sandbox create first
wip sandbox status first
wip sandbox exec first -- printf '%s\n' 'an argument with spaces'
wip sandbox exec first --timeout 30 -- sh -c 'exit 7'
wip sandbox destroy first
```

Use `--` before the executable so its options remain operands. Execution forwards each argv
element directly to WSLC, streams stdout/stderr, and returns the child exit code. A shell is
used only when explicitly supplied by the caller, as in the `sh` example. Execution is
non-interactive with a 300-second default deadline; `--timeout` accepts 1–2147483 seconds.
Timeout returns 124. It terminates the WSLC client; a remote process may still be running.
Check status and, if necessary, destroy the dedicated sandbox before retrying non-idempotent work.

| Operation | Existing owned resource | Missing resource |
| --- | --- | --- |
| create | Running: success without another container; created/exited: start the verified ID; unknown: recovery error | Run the configured image and verify ownership/state |
| status | Print logical name, state, backend name and ID | Print `not found`, success |
| destroy | Force-remove the verified container ID and confirm absence | Success |
| exec | Requires a running container; preserve exit code | Error; never create implicitly |

Unknown declarations are configuration errors. Sandboxes may reference declared volumes
by name (`volumes: [vol1, vol2]`). On `sandbox create`, referenced volumes are ensured via
`VolumeLifecycle.Create` and mounted to the container using `--mount type=volume,source=<backendName>,target=<mountPath>`.
Mount destinations preserve the declared canonical Linux paths and sort shallower paths before deeper
descendant paths to prevent shadowing. After a sandbox is running, `VolumeLifecycle.Reconcile` is invoked
for each attached volume to record usage durably. Sharing and isolation emerge naturally from configuration:
mounting the same volume across multiple sandboxes shares the underlying storage, while mounting a volume
in only one sandbox isolates it.

On `sandbox destroy`, the container is removed by its verified ID without volume-removal flags.
After confirmed container removal, `VolumeLifecycle.Reconcile` is invoked for each referenced volume:
persistent volumes are retained; ephemeral volumes remain intact while any referencing container
is still active, and are cleaned up only after the last referencing container detaches.
Re-creating a sandbox reuses retained persistent volumes while provisioning fresh ephemeral storage.
The image default command controls sandbox lifetime; an immediately exiting image is reported as
unsuccessful creation with a recoverable residue.

## Ownership and recovery

Backend names are `wip-s-` plus 40 lowercase SHA-256 hex characters derived from
`<namespace>:sandbox:<name>`. Creation persists `io.slidict.wip.owner=v1:<namespace>:sandbox:<name>`
as an explicit WSLC container label. Status lists the exact name, then inspects its ID and
checks both name and label. Subsequent start/exec/remove use that verified ID, so replacing
the name cannot redirect a destructive operation to the replacement. A collision or an
unlabelled container is rejected, never adopted. Namespace changes designate different resources;
keep the previous configuration to clean up previously created sandboxes.

WSLC must support run labels and JSON list/inspect, including IDs, names, state and labels.
Explicit labels are read from top-level `Labels` (WSLC) or `Config.Labels` (Docker-compatible
inspect). Successful list may return an empty stream for zero rows; empty inspect is an error.
Failed, timed-out or malformed probes mean unknown existence, not absence. Probes
have a 10-second deadline, create/start/remove a 120-second deadline. A mutation failure
preserves its exit code and may leave a resource; there is no automatic destructive rollback.

After a failed create, run status with the same declaration. An owned running residue makes
repeated create succeed; an owned stopped residue is started. Fix an invalid image/CMD by
destroying the owned sandbox, updating configuration and creating again. Image changes do not
replace an existing container implicitly. After failed destroy, inspect status and repeat destroy.
Ownership mismatch requires investigation outside this automatic lifecycle; do not remove the
conflicting resource merely because its name matches.

Older WSLC may retain a `deleted` record. It is a tombstone, treated as absent without
inspection, execution or removal. Create can issue a new run; a concurrent live replacement
still causes WSLC's name conflict rather than being adopted. Unknown live states require
ownership-checked destroy, absence confirmation and create; investigate failed removal first.

Unit tests cover ownership, partial failure and argv behavior without an agent. The Windows
real-WSLC lifecycle CI builds an Alpine fixture with `CMD ["sleep", "600"]`, assigns a unique
namespace, then checks repeated create, status, literal argv, exit 7, repeated destroy and absence.
It never updates or shuts down WSL on a developer machine; runner provisioning remains confined
to the isolated GitHub Actions runner.
