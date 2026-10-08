# Herdr agent status reporting from sandboxes

Design contract based on a measured sandbox FIFO channel and Herdr named-pipe IPC.
The transport works on the real host; relay and hook integration remain to implement.

## Goals and non-goals

Claude Code, Codex and agy run through `wip sandbox exec NAME --interactive`,
while Herdr runs on the host. Add explicit status reporting, especially approval
waits (`blocked`), through a restricted wip relay instead of relying only on screen
scraping. Keep existing interactive launches compatible.

This does not expose general Herdr access, manage sandboxes, install CLI hooks
automatically, or infer status when a CLI offers no suitable event. Screen scraping
remains a fallback.

## Transport and identity

Opt in per sandbox with `report_relay: true` in `wip.yml`. Start the separate,
long-running host process from the intended Herdr pane:

```sh
wip sandbox relay NAME --upstream HERDR_SOCKET_PATH
```

The relay idempotently creates `/run/wip/report.fifo` inside the sandbox with
`mkfifo -m 600`, owned by the sandbox CLI user. On reuse, verify it is a FIFO with
that owner and mode 0600; refuse a regular file, symlink or unsafe permissions.
The host relay runs `wip sandbox exec NAME -- cat /run/wip/report.fifo`
and reads one JSON line per report from stdout. When cat exits, the reader must
reopen the FIFO by starting cat again while the relay remains active.

The shim needs only `WIP_REPORT_FIFO`, containing the fixed in-sandbox FIFO path.
It writes one JSON line and exits. There is no endpoint state file, endpoint
variable, token registration/revocation control channel, per-launch token or
authentication frame.

The sandbox does not know its pane: do not pass `HERDR_PANE_ID` into it. The relay
captures `HERDR_PANE_ID` on the host and stamps both `params.pane_id` and
`params.source` (`wip:SANDBOX`) itself. Refuse requests supplying `pane_id` at all;
overwrite any supplied source. Without host pane context, do not forward reports.
A sandbox therefore cannot report about another pane. Concurrent launches in one
sandbox share the source; agent labels are display metadata, not authorization.

## Wire protocol

Use UTF-8 JSON Lines through the FIFO. Requests contain only `id` (optional string),
`method`, and `params` (object); refuse other top-level fields. Allow only
`pane.report_agent` and `pane.report_agent_session`, kept together in
`ReportRelay.AllowedMethods`.

```json
{"id":"r1","method":"pane.report_agent","params":{"agent":"claude-code","state":"blocked","seq":3}}
```

After host stamping, verified `pane.report_agent` params are required `pane_id`,
`source`, `agent`, `state`, with optional `message`, `seq`, `agent_session_id`,
`agent_session_path`. State is exactly `idle|working|blocked|unknown`. Herdr ignores
unknown params fields; the relay still forbids sandbox-supplied `pane_id`.
The session method name is verified; its params beyond `pane_id` are unenumerated.
Do not guess a session payload. Send no prompts, transcripts or credentials.

Limit each line to 16 KiB. Refuse invalid shapes, unsupported methods, supplied
`pane_id` and oversized lines without forwarding. Forward allowed requests to the
Herdr named pipe as structured JSON, never through shell evaluation, with a
five-second upstream deadline. Measured Herdr responses are:

```json
{"id":"r1","result":{"type":"ok"}}
{"id":"","error":{"code":"invalid_request","message":"..."}}
```

Herdr blanks the ID on rejection. Responses, local `relay_refused` errors and
upstream failures are handled and logged host-side, never returned through the FIFO.
The FIFO is one-way: the shim is fire and forget, writes and exits, and never waits
for a response. A hook cannot distinguish an accepted report from a refused one;
this is an accepted trade for simplicity. Sequence ordering still needs verification.

## In-sandbox shim

Provide a thin `herdr-report` client:

```sh
herdr-report report-agent --agent claude-code --state blocked --seq 3
herdr-report report-agent-session --agent claude-code --session session-abc --seq 4
```

Subcommands map to the dotted upstream methods; session argument mapping needs its
verified params contract before implementation. There are no pane, source, raw-command
or general API overrides. Hooks coordinate sequences per sandbox source. An approval
hook reports `blocked`, a resume event reports `working`, and completion reports
`idle`. Use actual CLI session IDs, never guesses from terminal output.

## Security boundary and fallback

There is no TCP listener or host port, so nothing is exposed on the LAN. There is
no dependency on `host.docker.internal`, `gateway.docker.internal` or any address;
the stale-hosts-entry failure is removed entirely. No bind-mounted Windows socket
is needed, which was impossible for this channel.

The FIFO is mode 0600 inside the sandbox: only processes in that sandbox running
as its owner (or with elevated sandbox privileges) can write it, and there is no
network path to it. No token or authentication frame is required. Treat reports as
untrusted advisory status; sandbox processes can impersonate agent labels, but
cannot select another pane. No host Herdr pipe, discovery file, Docker socket,
host credential or general host API is exposed inside the sandbox.

The `wip.yml` ownership classes remain unchanged: members cannot create or destroy
their own sandbox; configuration and authentication remain in member-private volumes.
The relay grants no lifecycle or volume permissions and stores no CLI auth data.

Without `report_relay: true`, existing interactive launches remain unchanged. Missing
FIFO, absent reader or unavailable upstream must not break an interactive launch.
Bound shim open/write attempts to two seconds, returning success without output
when reporting is unavailable; opening a FIFO must not block a hook indefinitely.
Invalid local arguments return nonzero; relay refusals are visible only in host logs.
Reporting never changes stdin, TTY behavior, signals or the interactive command's
exit status. Screen scraping continues when reports are unavailable; relay loss
must not fabricate `idle`.

## Acceptance procedure

1. Create a FIFO with `mkfifo -m 600` inside a sandbox. From the host, run
   `wip sandbox exec NAME -- cat FIFO`; write one JSON line inside the sandbox and
   verify the host receives it exactly. This channel was measured successfully.
2. Start the opted-in relay with host pane context. Verify FIFO type, owner and
   mode, idempotent creation and `WIP_REPORT_FIFO`. Confirm no pane, endpoint or
   token variables, network listener or host socket mounts were added. Let cat
   exit, then verify the relay reopens the FIFO and receives another report.
3. Send both allowed methods to a test upstream; verify host-stamped source and
   pane. Reject supplied `pane_id`, unknown methods, extra top-level fields, invalid
   shapes and lines over 16 KiB. Verify host logs and no refused upstream call;
   the shim must never wait for or claim upstream acceptance.
4. Against real Herdr, report `working -> blocked -> working -> idle` using verified
   params. Check success and blank-ID rejection responses in host logs and a real
   approval hook without screen scraping. Check the five-second upstream deadline.
   Verify session params and sequence behavior separately before claiming integration.
5. Stop the reader or remove the FIFO; verify hooks finish within two seconds and
   fallback remains available. Launch without opt-in or host pane context. Confirm
   unchanged input, Ctrl-C, terminal sizing, child exit status, private-volume mounts
   and ownership rules; members still cannot create/destroy their sandbox.

## Measured host facts and remaining verification

On Windows, `HERDR_SOCKET_PATH` points to a 25-byte regular file containing
`pid:nonce`. Herdr's real IPC is a Windows named pipe whose name is the literal
value of `HERDR_SOCKET_PATH`; the file is not an AF_UNIX listener.

The sandbox FIFO write and host exec/cat read both worked on this machine, as did
the Herdr named-pipe upstream. No unverified network connectivity gate remains.
The method names, report params and response shapes above are measured; session
params beyond `pane_id` and sequence ordering remain to verify.
