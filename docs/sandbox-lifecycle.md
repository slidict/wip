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
wip sandbox exec first --interactive -- bash
wip sandbox stop first
wip sandbox create first # resume the same stopped container
wip sandbox destroy first
```

Use `--` before the executable so its options remain operands. Execution forwards each argv
element directly to WSLC, streams stdout/stderr, and returns the child exit code. A shell is
used only when explicitly supplied by the caller, as in the `sh` example. Execution defaults to
non-interactive with a 300-second deadline; `--timeout` accepts 1–2147483 seconds.
Timeout returns 124. It terminates the WSLC client; a remote process may still be running.
Check status and, if necessary, destroy the dedicated sandbox before retrying non-idempotent work.
`--interactive` runs the same argv as a session the caller drives; see below.

| Operation | Existing owned resource | Missing resource |
| --- | --- | --- |
| create | Running: success without another container; created/exited: start the verified ID; unknown: recovery error | Run the configured image and verify ownership/state |
| status | Print logical name, state, backend name and ID | Print `not found`, success |
| stop | Running: stop the verified ID, then confirm the same ID is exited; created/exited: success without mutation; other states: error | Success |
| destroy | Force-remove the verified container ID and confirm absence | Success |
| exec | Requires a running container; preserve exit code | Error; never create implicitly |
| exec --interactive | Requires a running container; attach stdin (`-i`, plus `-t` with a terminal), no deadline, preserve exit code | Error; never create implicitly |

Unknown declarations are configuration errors. Sandboxes may reference declared volumes
by name (`volumes: [vol1, vol2]`). On `sandbox create`, referenced volumes are ensured via
`VolumeLifecycle.Create` and mounted to the container using `--mount type=volume,source=<backendName>,target=<mountPath>`.
Mount destinations preserve the declared canonical Linux paths and sort shallower paths before deeper
descendant paths to prevent shadowing. After a sandbox is running, `VolumeLifecycle.Reconcile` is invoked
for each attached volume to record usage durably. Sharing and isolation emerge naturally from configuration:
mounting the same volume across multiple sandboxes shares the underlying storage, while mounting a volume
in only one sandbox isolates it.

`sandbox stop` retains the container and every mount, including persistent and ephemeral volumes.
It neither removes nor reconciles storage. `sandbox create` resumes the owned stopped ID using
the existing ownership/mount checks; no separate `sandbox start` command is needed.
Never use the top-level `stop` as a substitute: it operates on a different configured container/stack.

On `sandbox destroy`, the container is removed by its verified ID without volume-removal flags.
After confirmed container removal, `VolumeLifecycle.Reconcile` is invoked for each referenced volume:
persistent volumes are retained; ephemeral volumes remain intact while any referencing container
is still active, and are cleaned up only after the last referencing container detaches.
Re-creating a sandbox reuses retained persistent volumes while provisioning fresh ephemeral storage.
The image default command controls sandbox lifetime; an immediately exiting image is reported as
unsuccessful creation with a recoverable residue.

## Interactive sessions

`sandbox exec --interactive` is the same ownership-checked execution as `sandbox exec`,
with wip's own stdin, stdout and stderr handed to the child instead of being streamed through
wip. That is what a shell, a REPL, a debugger, an interactive installer or an interactive CLI
such as Claude Code or Codex needs, and it is a general terminal transport: no tool-specific
protocol or API is involved, and the sandbox only ever sees the argv the caller passed.

`--interactive`, `-i`, `-t` and `-it` are four spellings of one flag. WSLC separates
`-i` (attach stdin) from `-t` (allocate a TTY), and wip decides between them rather than
asking: `-i` is always passed, because a session nothing can be typed into is not
interactive, and `-t` is added when wip's own stdin and stdout are a terminal — the same
condition `wip exec` applies. Piping a script into `--interactive` therefore reaches the
child and simply runs without a pty. Dropping `-i` in that case would leave the child with
no stdin at all: it would read EOF immediately, print nothing and exit 0.

```powershell
wip sandbox exec first --interactive -- bash          # a shell, for as long as you keep it open
wip sandbox exec first -it -- python3                 # the bundled spelling, same flag
'echo from stdin', 'exit 7' | wip sandbox exec first --interactive -- sh   # piped, no pty
```

- **No deadline.** `--timeout` exists so an automated call cannot hang a script; a session
  being typed into has no such bound. Passing both is a usage error rather than a silently
  ignored option, so a caller is never left believing the session is bounded.
- **EOF ends the session.** Ctrl-D, or a closed stdin pipe, reaches the child and ends the
  session the way it would outside a sandbox; wip then returns the status the child chose.
- **Signals are the terminal's to deliver.** The child shares wip's console, so Ctrl-C comes
  from the terminal rather than from wip, and wip declines to tear itself down first so it can
  still report the child's status. This is the console-inheriting path `wip exec` and
  `wip shell` already use; nothing here forwards or synthesises a signal.
- **Terminal size comes from the same console.** The child inherits the real console rather
  than a pipe, so the size it reads is the console's own and wip forwards nothing. With `-t`
  the resize path is WSLC's; wip adds no handling of its own.
- **Exit code.** The child's status is returned unchanged, exactly as in the non-interactive
  path. Timeout's 124 cannot occur, because there is no deadline to exceed.
- **No error hints, and `--quiet` does nothing.** Both read the captured transcript, and an
  interactive child writes straight to the terminal, so there is nothing to capture. This is
  the same trade `wip exec` already makes.
- **Non-interactive execution is unchanged.** Without the flag, argv, the 300-second default
  deadline, streaming and exit codes are exactly as before.

Unit tests pin the argv WSLC receives (`exec -i -t <id> …` with a terminal, `exec -i <id> …`
without one), the absence of a deadline, and that argv and a running container are still
required before anything reaches the backend. The e2e suite drives a real container through a
piped interactive `sh`: stdin reaching a still-running shell, its output coming back, EOF
ending it and its exit status surviving the round trip. What neither suite covers is a real
pty — terminal allocation, Ctrl-C and resize — because GitHub Actions has no terminal to
allocate; that part is a manual check in a real terminal.

## Ownership and recovery

Backend names are `wip-s-` plus 40 lowercase SHA-256 hex characters derived from
`<namespace>:sandbox:<name>`. Creation persists `io.slidict.wip.owner=v1:<namespace>:sandbox:<name>`
as an explicit WSLC container label. Status lists the exact name, then inspects its ID and
checks both name and label. Subsequent start/stop/exec/remove use that verified ID, so replacing
the name cannot redirect a destructive operation to the replacement. A collision or an
unlabelled container is rejected, never adopted. Namespace changes designate different resources;
keep the previous configuration to clean up previously created sandboxes.

WSLC must support run labels and JSON list/inspect, including IDs, names, state and labels.
Explicit labels are read from top-level `Labels` (WSLC) or `Config.Labels` (Docker-compatible
inspect). Successful list may return an empty stream for zero rows; empty inspect is an error.
Failed, timed-out or malformed probes mean unknown existence, not absence. Probes
have a 10-second deadline, create/start/stop/remove a 120-second deadline. A mutation failure
preserves its exit code and may leave a resource; there is no automatic destructive rollback.
For stop, a successful backend exit is not enough: readback must confirm the original container
ID in `exited` state. Disappearance, replacement, still-running/unknown state or a failed probe
returns an error. Failure/timeout is never automatically retried. Inspect status before deciding
whether another explicit stop or create is appropriate; do not blindly resend uncertain operations.
Stop returns 0 for verified stopped/missing resources, the backend's nonzero exit for mutation
failure (124 for timeout), and 1 for configuration/ownership/readback errors.

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
