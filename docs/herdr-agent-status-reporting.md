# Herdr agent status reporting from sandboxes

Design contract updated from real-host measurements; the TCP relay changes below
still require implementation and connectivity verification.

## Goals and non-goals

Claude Code, Codex and agy run through `wip sandbox exec NAME --interactive`,
while Herdr runs on the host. Add explicit status reporting, especially approval
waits (`blocked`), through a restricted wip relay instead of relying only on screen
scraping. Keep existing interactive launches compatible.

This does not expose general Herdr access, manage sandboxes, install CLI hooks
automatically, or infer status when a CLI offers no suitable event. Screen scraping
remains a fallback.

## Transport and identity

Opt in per sandbox with `report_relay: true` in `wip.yml`. The sandbox-to-host leg
must use TCP at `host.docker.internal:PORT`. Windows wip cannot create an AF_UNIX
socket reachable by a Linux container through a bind-mounted Windows directory.
Drop `/run/wip/herdr`, its socket mount, overlap reservation and mount-mismatch
checks: the directory has no remaining reporting purpose.

Start the separate, long-running host process with:

```sh
wip sandbox relay NAME --upstream HERDR_SOCKET_PATH
```

The upstream argument identifies a Windows named pipe, not a Unix socket. For each
exec, wip generates a cryptographically random shared secret, registers it with
that relay for the launch lifetime, and exports:

- `WIP_REPORT_SOCKET`: the TCP endpoint `host.docker.internal:PORT` (legacy variable name).
- `WIP_REPORT_SOURCE`: `wip:SANDBOX`, using the sandbox name.
- `WIP_REPORT_TOKEN`: the per-launch shared secret.
- `HERDR_PANE_ID`: passed through from the host when present.

Only the token is a credential; never log it or persist it in shared configuration.
Revoke it when exec ends. The relay stamps `params.source` itself as
`wip:SANDBOX`, so a sandbox cannot report under another source name. Concurrent
launches share that source but have separate tokens. The shim requires `--agent`
(for example `claude-code`, `codex` or `agy`); labels are display metadata.

## Wire protocol

Use UTF-8 JSON Lines over TCP. The proposed first line authenticates the connection:

```json
{"token":"<WIP_REPORT_TOKEN>"}
```

The relay must refuse missing, wrong or expired tokens before forwarding anything;
this authentication frame is consumed locally and never sent to Herdr. Require
it within two seconds. After authentication, each request contains only `id`
(optional string), `method`, and `params` (object); refuse other top-level fields.
Allow only `pane.report_agent` and `pane.report_agent_session`, kept together in
`ReportRelay.AllowedMethods`.

```json
{"id":"r1","method":"pane.report_agent","params":{"pane_id":"pane-7","agent":"claude-code","state":"blocked","seq":3}}
```

After the relay stamps `source`, verified `pane.report_agent` params are required
`pane_id`, `source`, `agent`, `state`, with optional `message`, `seq`,
`agent_session_id`, `agent_session_path`. State is exactly
`idle|working|blocked|unknown`. Herdr ignores unknown params fields. The session
method name is verified; its params contract is not established by these measurements.
Do not guess a session payload. Send no prompts, transcripts or credentials.

Forward allowed requests to the named pipe using structured JSON, never shell
evaluation. Return upstream responses unchanged. Measured Herdr responses are:

```json
{"id":"r1","result":{"type":"ok"}}
{"id":"","error":{"code":"invalid_request","message":"..."}}
```

Herdr blanks the ID on rejection, so clients cannot rely on it for error correlation.
A local relay refusal remains distinct:

```json
{"id":"r1","error":{"code":"relay_refused","message":"Unsupported method"}}
```

Limit each line to 16 KiB. Refuse invalid shapes, unsupported methods and oversized
lines with `relay_refused`; unauthenticated connections are refused and closed.
Sequence ordering semantics still require verification against Herdr.

## In-sandbox shim

Provide a thin `herdr-report` client in the sandbox:

```sh
herdr-report report-agent --agent claude-code --state blocked --seq 3
herdr-report report-agent-session --agent claude-code --session session-abc --seq 4
```

These shim subcommands map to the dotted upstream method names; session argument
mapping needs its verified params contract before implementation. Pane, source,
endpoint and token come only from the environment above; there are no target,
raw-command or general API overrides. The shim authenticates each connection.
CLI hooks coordinate sequences per sandbox source. An approval hook reports
`blocked`, a resume event reports `working`, and a completion event reports `idle`.
Report sessions only when the CLI provides an actual session ID; never guess one
from terminal output.

## Security boundary and fallback

Binding the relay to a LAN-visible address without a token would let anything on
the LAN report agent state. Require a valid per-launch token on every connection
and restrict access to the two allowed methods. The relay controls source identity.
Treat sandbox messages as untrusted advisory status: processes able to read the
launch token can impersonate its agent label. No host Herdr pipe, discovery file,
Docker socket, host credential or general host API is exposed inside the sandbox;
only the restricted relay token is passed to the child.

The `wip.yml` ownership classes remain unchanged: members cannot create or destroy
their own sandbox; configuration and authentication remain in member-private volumes.
The relay grants no lifecycle or volume permissions and stores no CLI auth data.

Without `report_relay: true`, existing interactive launches remain unchanged.
`HERDR_PANE_ID` is optional; pass it through only when present. An unavailable relay
or upstream must not prevent normal interactive command execution. The shim returns
success without output when reporting is unavailable, including a disconnected
relay or a two-second timeout. Invalid local arguments or rejected requests return
nonzero; hooks treat those errors as advisory. Reporting never changes stdin, TTY
behavior, signal handling or the interactive command's exit status. Screen scraping
continues when explicit reports are unavailable; relay loss must not fabricate `idle`.

## Acceptance procedure

1. Opt in, start the host relay with the named-pipe upstream, and open a listening
   TCP port on the Windows host. From the container, resolve `host.docker.internal`
   and test an inbound connection. This connectivity step is still unverified.
2. Launch each CLI interactively. Check endpoint, source, token and optional pane
   variables; confirm tokens differ per exec, private-volume mounts and ownership
   rules are unchanged, and no host pipe, discovery file or credentials were added.
3. Reject missing, wrong and expired tokens without any upstream call. Try unknown
   methods, extra top-level fields, invalid shapes and lines over 16 KiB; verify
   `relay_refused`. Supply another source and verify it is replaced by `wip:SANDBOX`.
4. Against real Herdr, send `pane.report_agent` with verified params and observe
   `working ↁEblocked ↁEworking ↁEidle`, success and blank-ID rejection responses.
   Trigger a real approval hook without screen scraping. Verify session params and
   sequence behavior separately before claiming session integration works.
5. Run concurrent launches, end one and verify its token is revoked. Disconnect the
   relay and launch without opt-in or pane context. Verify hooks finish within two
   seconds, fallback remains available, and input, Ctrl-C, terminal sizing and child
   exit status match existing execution. Confirm members cannot create/destroy sandboxes.

## Measured host facts and remaining verification

On Windows, `HERDR_SOCKET_PATH` points to a 25-byte regular file containing
`pid:nonce`. Herdr's real IPC is a Windows named pipe whose name is the literal
value of `HERDR_SOCKET_PATH`; the file is not an AF_UNIX listener.

The real methods are `pane.report_agent` and `pane.report_agent_session`; the
report params and response shapes above were measured against real Herdr.

The container's default gateway is `172.17.0.1`; `host.docker.internal` resolves to
the host LAN address, not loopback. Whether the host actually accepts an inbound
connection from the container is **UNVERIFIED**: it requires a listening host port.
