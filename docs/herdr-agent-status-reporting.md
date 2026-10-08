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

Opt in per sandbox with `report_relay: true` in `wip.yml`. Mount the dedicated
relay directory read-only at `/run/wip/herdr`; its socket is
`/run/wip/herdr/report.sock`, never Herdr's own host socket. Config loading rejects
any declared mount or volume overlapping `/run/wip/herdr`. Provision the mount
through the authorized host lifecycle; exec cannot change container mounts. An
existing container whose relay mount does not match `report_relay` is an error.

Start the separate, long-running host process with:

```sh
wip sandbox relay NAME --upstream HERDR_SOCKET_PATH
```

Wip exports these values into the sandbox:

- `WIP_REPORT_SOCKET`: `/run/wip/herdr/report.sock`.
- `WIP_REPORT_SOURCE`: `wip:SANDBOX`, using the sandbox name.
- `HERDR_PANE_ID`: passed through from the host when present.

These values are identifiers, not credentials. The relay stamps `params.source`
itself as `wip:SANDBOX`, so a sandbox cannot report under another source name.
Concurrent launches in the same sandbox share that source. The shim requires
`--agent` (for example `claude-code`, `codex` or `agy`); labels are display
metadata, not authorization.

## Wire protocol

Use UTF-8 JSON Lines over the socket: one request object and one response object
per line, no banners. Requests contain only `id` (optional string), `method`, and
`params` (object); any other top-level field is refused. Allowed methods are
`report-agent` and `report-agent-session`.

```json
{"id":"r1","method":"report-agent","params":{"pane":"pane-7","agent":"claude-code","state":"blocked","seq":3}}
{"id":"r2","method":"report-agent-session","params":{"pane":"pane-7","agent":"claude-code","session":"session-abc","seq":4}}
```

The session method associates an opaque CLI session ID with the source; it does
not include prompts, transcripts, credentials or host paths. The relay forwards
allowed requests to the upstream socket after stamping `params.source`, never
using shell evaluation. The illustrative params above need real Herdr verification.

Upstream responses are returned to the client. A local refusal has this shape:

```json
{"id":"r1","error":{"code":"relay_refused","message":"Unsupported method"}}
```

Limit each line to 16 KiB. Refuse invalid request shapes, unsupported methods and
oversized lines with `relay_refused`. State reporting uses
`idle|working|blocked|unknown`; sequence handling and successful response semantics
belong to the upstream Herdr contract and need verification.

## In-sandbox shim

Provide a thin `herdr-report` client in the sandbox:

```sh
herdr-report report-agent --agent claude-code --state blocked --seq 3
herdr-report report-agent-session --agent claude-code --session session-abc --seq 4
```

Pane, source and socket come only from the environment above; there are no target,
raw-command or general API overrides. CLI hooks supply the sequence, coordinated
per sandbox source. A Claude Code approval hook reports `blocked`, a resume event reports
`working`, and a completion event reports `idle`. Report sessions only when the CLI
provides an actual session ID; never guess one from terminal output.

## Security boundary and fallback

The dedicated socket grants access only to the allowed reporting methods. Restrict
socket access to the intended sandbox; the relay controls the source identity.
Treat all sandbox messages as untrusted advisory status: processes in the same
sandbox can impersonate its agent label. No host Herdr socket, Docker socket, host
credential or general host API is mounted or passed through.

The `wip.yml` ownership classes remain unchanged: members cannot create or destroy
their own sandbox; configuration and authentication remain in member-private volumes.
The relay grants no lifecycle or volume permissions and stores no CLI auth data.

Without `report_relay: true`, existing interactive launches remain unchanged.
`HERDR_PANE_ID` is optional; pass it through only when present. An opted-in mount
mismatch is an error, not a silent fallback. An unavailable relay or upstream must
not prevent normal interactive command execution. The shim returns success without output
when reporting is unavailable, including a disconnected relay or a two-second timeout.
Invalid local arguments or rejected requests return nonzero; hooks must treat those
errors as advisory. Reporting never changes stdin, TTY behavior, signal handling or
the interactive command's exit status. Screen scraping continues when explicit
reports are unavailable; relay loss must not fabricate `idle`.

## Acceptance procedure

1. On a supported host, opt in with `report_relay: true`, provision the sandbox
   through the authorized lifecycle, and start `wip sandbox relay NAME --upstream
   HERDR_SOCKET_PATH`. Launch each CLI with `wip sandbox exec NAME --interactive`.
   Check the read-only directory, socket and exported values; confirm private-volume
   mounts and ownership rules are unchanged and no host API sockets or credentials
   were added. Check overlapping mounts fail config load and mount mismatches fail.
2. With a test upstream, send both allowed methods and verify `params.source` is
   always `wip:SANDBOX`, even if supplied differently. Check forwarded responses.
   Against real Herdr, verify session and `working ↁEblocked ↁEworking ↁEidle`
   reports, sequencing and an approval hook without screen scraping.
3. Try unknown methods, extra top-level fields, invalid request shapes and lines
   over 16 KiB. Verify `id` plus `error.code: relay_refused` and no upstream call.
4. Run concurrent launches; verify their shared sandbox source and coordinated
   sequences. Stop the relay and verify reporting becomes unavailable.
5. Launch without opt-in or pane context, then disconnect the relay during a session.
   Verify hooks finish within two seconds, fallback detection remains available,
   and shell input, Ctrl-C, terminal sizing and child exit status match existing
   interactive execution. Confirm a member still cannot create/destroy a sandbox.

## Open questions

- The upstream socket request shape and exact method names `report-agent` and
  `report-agent-session` are not yet verified against real Herdr. Both names live
  in the single constant `ReportRelay.AllowedMethods`.
- Can a Windows AF_UNIX socket path be bind-mounted through the wslc VM into the
  container at all? This is not yet verified.
