# Auto Restarting Containers

Real Compose restarts a container tagged `restart: always` when it exits. `wslc` has no such
policy, and no push-based "container exited" notification for wip to hook into — so the closest
approximation is polling: `wip up --watch`.

Reference for the values and their limits: [Restart Policies](Restart-Policies).

## Setup

Tag the containers you want supervised:

```yaml
# wip.yml — mode: container
container: app
network: app-tier
dependencies:
  app:
    image: myapp:dev
    restart: unless-stopped
    ports:
      - "3000:3000"
  worker:
    image: myapp:dev
    command: bundle exec sidekiq
    restart: on-failure:5
  postgres:
    image: postgres:16
    restart: always
    env:
      POSTGRES_PASSWORD: password
  mailcatcher:
    image: sj26/mailcatcher
    # no restart: → never auto-restarted
```

Or in `compose.yml` under [compose-native mode](Compose-Native-Mode):

```yaml
services:
  worker:
    image: myapp:dev
    command: bundle exec sidekiq
    restart: unless-stopped
```

## Running the supervisor

```console
$ wip up --watch
wip: creating network 'app-tier'
wip: dependency 'postgres' not found, creating it
wip: dependency 'worker' not found, creating it
wip: container 'app' not found, creating it
wip: watching app, worker, postgres, mailcatcher for exited restart: containers every 5s (running detached; Ctrl-C to stop)
```

Every 5 seconds it checks each dependency's state and restarts the exited ones whose `restart:`
allows it:

```console
wip: 'worker' has exited, restarting it (restart: on-failure:5)
```

`--interval N` changes the period:

```bash
wip up --watch --interval 10
```

## Things that will surprise you

### `--watch` implies `-d`

The primary container can't hold an attached TTY while the loop polls on the same thread, so it
always runs detached under `--watch`. To see its output:

```bash
# terminal 2 — compose modes only
wip logs -f
# or, any mode
wslc logs -f app
```

### Exit codes are not read

All three restarting values behave identically: an exited container is restarted regardless of exit
status. Real `on-failure` skips a clean (zero) exit; this loop doesn't, because reading the code
would need a heavier call per tick. `on-failure:5`'s retry count is likewise not enforced.

If your worker exits `0` on purpose when it's done, `--watch` will restart it forever. Use
`restart: no` (or omit it) for anything that's meant to finish.

### It races with manual stops

The loop asks "is this container exited right now?", not "did it just exit?" It cannot tell a crash
from a `wip stop` you ran in another terminal — so it may restart what you deliberately stopped.

**Ctrl-C the watch loop first**, then `wip stop` / `wip down`.

### Removed containers are not recreated

A removed container reports state `deleted`, not `exited`, and `--watch` only ever runs `start`.
Recovering from a `wip down` needs a fresh `wip up`.

### Not available under `mode: compose`

```
`wip up --watch` is not supported under mode: compose (wip never parses a compose.yml
service list in that mode, so there is nothing to poll)
```

Use your compose tool's own restart handling. See [Compose Mode](Compose-Mode).

## When it isn't restarting anything

Run the loop with `--debug` — wip logs the raw `wslc list` entry it read for each dependency:

```console
$ wip up --watch --debug
wip: [debug] 'worker': {"Name"=>"worker", "State"=>3, …}
```

Compare `State` against the enum: `0` invalid, `1` created, `2` running, `3` exited, `4` deleted.
Only `3` triggers a restart.

Checklist:

1. Is `restart:` an exact match for `always` / `unless-stopped` / `on-failure[:N]`? Typos are
   silently inert.
2. Did you quote `restart: "no"` when you meant something else? Unquoted `no` is a YAML boolean.
3. Is the container actually `exited` (`3`), or `deleted` (`4`)?
4. Is the loop still running? It's a foreground process.

## Should you use this?

It's a development convenience for the case where a sidecar occasionally dies and you'd rather not
notice. It is not a production supervisor: there's no daemon, no backoff, no exit-code awareness,
and no logging beyond your terminal. For anything that must stay up unattended, use a real process
supervisor on the host.

## Related

- [Restart Policies](Restart-Policies)
- [wip up](wip-up)
- [Dependencies](Dependencies)
