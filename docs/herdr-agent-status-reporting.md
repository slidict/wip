# Herdr agent status reporting from sandboxes

Design proposal; no relay or hook integration is implemented by this document.

## Goals and non-goals

Claude Code, Codex and agy run through `wip sandbox exec NAME --interactive`,
while Herdr runs on the host. Add explicit status reporting, especially approval
waits (`blocked`), through a restricted wip relay instead of relying only on screen
scraping. Keep existing interactive launches compatible.

This does not expose general Herdr access, manage sandboxes, install CLI hooks
automatically, or infer status when a CLI offers no suitable event. Screen scraping
remains a fallback.

## Transport and identity

Prefer a host-side relay listening on a dedicated Unix socket, bind-mounted at
`/run/wip/herdr-report.sock`. This is a new reporting endpoint, never Herdr's own
host socket. Provision the mount through the authorized host lifecycle; exec cannot
add a mount to an existing container. If unavailable, use the fallback below.

For each interactive launch, wip takes `HERDR_PANE_ID` from the host environment,
validates it with the host integration, and binds the relay capability to that pane
and sandbox. Wip generates a fresh opaque source ID per launch and passes only:

- `HERDR_REPORT_SOCKET`: the container socket path.
- `HERDR_PANE_ID`: the bound pane ID.
- `HERDR_SOURCE_ID`: the launch source ID.

These values are identifiers, not credentials. The relay registers each source
against its pane and launch lifetime; caller-supplied IDs cannot widen that binding.
Concurrent launches use separate sources. Revoke the source when its exec ends.
The shim requires `--agent` (for example `claude-code`, `codex` or `agy`); labels
are display metadata, not authorization.

## Wire protocol

Use UTF-8 JSON Lines over the socket: one request object and one response object
per line, no banners. Version 1 allows only these two methods:

```json
{"v":1,"id":"r1","method":"report-agent","pane":"pane-7","source":"launch-42","agent":"claude-code","state":"blocked","seq":3}
{"v":1,"id":"r2","method":"report-agent-session","pane":"pane-7","source":"launch-42","agent":"claude-code","session":"session-abc","seq":4}
```

The session method associates an opaque CLI session ID with the source; it does
not include prompts, transcripts, credentials or host paths. The relay maps validated
fields to Herdr's `pane report-agent` or `pane report-agent-session` adapter using
structured arguments, never shell evaluation. Confirm the host session command's
argument mapping when implementing the adapter; the wire contract stays independent.

```json
{"v":1,"id":"r1","ok":true,"result":{"applied":true}}
{"v":1,"id":"r2","ok":false,"error":{"code":"invalid_request","message":"Unsupported method"}}
```

Require exactly the fields shown for each method; reject unknown fields, methods,
versions and invalid types. IDs, pane, source, agent and session are nonempty strings
of at most 256 UTF-8 bytes without control characters. `state` is exactly
`idle|working|blocked|unknown`; `seq` is an integer from 1 to 9007199254740991.
Limit each line to 4 KiB, connection idle time to 2 seconds, and each source to
20 requests/second. Malformed or oversized frames receive `invalid_request`
(`id: null` when unparseable) and close the connection.

Sequence numbers increase across both methods within one source. Serialize requests
per source; acknowledge success only after host acceptance. Duplicate or older
numbers succeed with `applied: false` and must not overwrite newer information.
Other error codes are `forbidden`, `unavailable`, and `rate_limited`; messages
contain no host internals. Host failures do not advance the accepted sequence.

## In-sandbox shim

Provide a thin `herdr-report` client in the sandbox:

```sh
herdr-report report-agent --agent claude-code --state blocked --seq 3
herdr-report report-agent-session --agent claude-code --session session-abc --seq 4
```

Pane, source and socket come only from the environment above; there are no target,
raw-command or general API overrides. CLI hooks supply the sequence, coordinated
per launch. A Claude Code approval hook reports `blocked`, a resume event reports
`working`, and a completion event reports `idle`. Report sessions only when the CLI
provides an actual session ID; never guess one from terminal output.

## Security boundary and fallback

The dedicated socket grants reporting for registered sources only. Validate pane
and source binding on every request, restrict socket access to the intended sandbox,
and revoke it on teardown. Treat all sandbox messages as untrusted advisory status:
processes in the same sandbox can impersonate its agent label. No host Herdr socket,
Docker socket, host credential or general host API is mounted or passed through.

The `wip.yml` ownership classes remain unchanged: members cannot create or destroy
their own sandbox; configuration and authentication remain in member-private volumes.
The relay grants no lifecycle or volume permissions and stores no CLI auth data.

If Herdr, the relay mount or pane context is absent, omit relay environment variables
and launch the requested command normally. The shim returns success without output
when reporting is unavailable, including a disconnected relay or a two-second timeout.
Invalid local arguments or rejected requests return nonzero; hooks must treat those
errors as advisory. Reporting never changes stdin, TTY behavior, signal handling or
the interactive command's exit status. Screen scraping continues when explicit
reports are unavailable; relay loss must not fabricate `idle`.

## Acceptance procedure

1. On a supported host, provision a reporting-enabled sandbox through the authorized
   lifecycle, then launch each CLI with `wip sandbox exec NAME --interactive`.
   Check the socket and three identifiers; confirm no host API sockets or credentials
   were added and existing private-volume mounts and ownership rules are unchanged.
2. Send session and `working ↁEblocked ↁEworking ↁEidle` reports with increasing
   sequences. Verify Herdr shows the bound pane, source, agent and session; trigger
   a real approval hook and verify `blocked` without screen scraping.
3. Repeat and reorder sequences; verify newer status survives. Try another pane or
   source, an unknown method, extra fields, invalid state, malformed/oversized JSON
   and excessive requests. Verify rejection and no unrelated host action.
4. Run concurrent launches; verify independent sources and sequence tracking. End
   one launch and verify its source can no longer report.
5. Launch without Herdr/context/mount, then disconnect the relay during a session.
   Verify hooks finish within two seconds, fallback detection remains available,
   and shell input, Ctrl-C, terminal sizing and child exit status match existing
   interactive execution. Confirm a member still cannot create/destroy a sandbox.
