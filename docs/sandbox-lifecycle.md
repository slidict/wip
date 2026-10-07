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
wip sandbox attach first
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
| attach | Requires a running container; join the main process's streams, no argv, no deadline, preserve its exit code | Error; never create implicitly |

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
  `wip shell` already use; nothing here forwards or synthesises a signal. Asserted in CI: a
  `trap … INT` in the container fires on Ctrl-C, and the status it chooses comes back.
- **Terminal size comes from the same console.** The child inherits the real console rather
  than a pipe, so the size it reads is the console's own and wip forwards nothing. With `-t`
  the resize path is WSLC's; wip adds no handling of its own. Both halves are asserted in CI:
  a shell started in an 80x24 pseudo console reads `24 80`, and resizing that console to
  120x40 makes the same shell read `40 120` — the size reaches the container pty without wip
  taking part. The container reads its size when asked rather than being told, so a query
  immediately after a resize can still answer with the old one.
- **Exit code.** The child's status is returned unchanged, exactly as in the non-interactive
  path. Timeout's 124 cannot occur, because there is no deadline to exceed.
- **No error hints, and `--quiet` does nothing.** Both read the captured transcript, and an
  interactive child writes straight to the terminal, so there is nothing to capture. This is
  the same trade `wip exec` already makes.
- **Non-interactive execution is unchanged.** Without the flag, argv, the 300-second default
  deadline, streaming and exit codes are exactly as before.

### `attach` joins the main process instead of starting one

`sandbox exec --interactive` starts a new process beside the sandbox's main one and ends when
that new process ends. `sandbox attach` joins the process that is already running — the
image's own CMD/ENTRYPOINT, whose lifetime *is* the sandbox's lifetime:

```powershell
wip sandbox attach first
```

- **No command, and none is accepted.** WSLC's `attach` takes only a container, and the
  streams it joins are the ones the main process was started with. `wip sandbox attach first
  -- bash` is a usage error, not a silently dropped argument: that request is
  `exec --interactive`.
- **What you send reaches the process the sandbox exists to run.** Ctrl-C there goes to the
  main process, and ending it ends the sandbox — which is why this is a separate command
  rather than a flag on `exec`. WSLC exposes no detach key sequence, so leaving an attached
  session without stopping that process is not something wip can offer.
- **It needs a real terminal.** With no console to join, WSLC's own `attach` fails with
  `ERROR_INVALID_HANDLE` (reproduced directly with `wslc attach`, independent of wip); wip
  passes that exit status through unchanged. A created console counts: inside the e2e suite's
  pseudo console, attach reaches the main process and the session behaves normally.
- **The main process outlives the terminal.** Ending an attached session — Ctrl-C, or the
  terminal going away — does not stop the sandbox: the image's CMD runs as PID 1, which
  ignores SIGINT unless it handles it. Asserted in CI. What the session itself reports is the
  control event's own status rather than anything the container chose, so an attached
  session's exit code is not a status to read meaning into.
- **Exit code.** The status the main process ended with, unchanged.

Unit tests pin the argv WSLC receives — `exec -i -t <id> …` with a terminal, `exec -i <id> …`
without one, `attach <id>` with neither argv nor flags — the absence of a deadline on both,
and that argv and a running container are still required before anything reaches the backend.

The e2e suite covers both halves of the real Windows → WSLC → container path:

- **Through a pipe**: stdin reaching a still-running shell, its output coming back, EOF
  ending it, and its exit status surviving the round trip.
- **Through a real pseudo console**: a CI runner has no terminal, but that only means nobody
  hands one over — `tests/e2e/PtyHarness` creates one with `CreatePseudoConsole` (ConPTY) and
  drives wip inside it. That covers `test -t 0 && test -t 1` inside the container, the
  console's size, a resize reaching the container pty, Ctrl-C arriving as an interrupt (a
  `trap … INT` in the container fires and its chosen status, 42, comes back through the
  chain), and the shell's own exit status, 7, on a clean exit.
- **`attach` through that same console**: the main process's own output reaching the session
  is what proves it joined rather than started something, and the sandbox is still running
  after the session ends. The fixture image ticks once a second for exactly this.

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
real-WSLC lifecycle CI builds an Alpine fixture whose CMD ticks for 600 seconds, assigns a unique
namespace, then checks repeated create, status, literal argv, exit 7, repeated destroy and absence.
It never updates or shuts down WSL on a developer machine; runner provisioning remains confined
to the isolated GitHub Actions runner.
