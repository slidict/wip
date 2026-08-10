# Restart Policies

`restart:` records what should happen when a container exits. `wslc` has no restart policy of its
own, so the value is inert until something acts on it — and the only thing that does is
[`wip up --watch`](wip-up).

## Declaring it

```yaml
# mode: container
dependencies:
  app:
    image: your/image:tag
    restart: unless-stopped
  worker:
    image: your/image:tag
    command: bundle exec sidekiq
    restart: on-failure:3
```

```yaml
# compose.yml, under mode: compose-native
services:
  worker:
    image: your/image:tag
    restart: always
```

## Accepted values

| Value | `--watch` restarts an exited container? |
|---|---|
| `no` (default) | no |
| `always` | yes |
| `unless-stopped` | yes |
| `on-failure` | yes |
| `on-failure:MAX_RETRIES` | yes |
| anything else | no |

Matching is exact — a typo like `always-restart` stays inert rather than accidentally matching via
a prefix check. `on-failure` accepts an optional numeric suffix (`on-failure:3`); the number is
accepted but not currently enforced as a retry cap.

### `restart: no` and YAML

An unquoted `no` parses as the boolean `false` in YAML, not the string `"no"`. wip normalizes
`false`, `null`, and `""` to `"no"` in both `wip.yml` and `compose.yml`, so all of these are
equivalent:

```yaml
restart: no
restart: "no"
restart:
# (key omitted entirely)
```

### compose.yml values are not policed

The compose parser accepts any `restart:` value as-is, including ones outside
`always`/`unless-stopped`/`on-failure[:N]`. A `compose.yml` predates wip and is read by other
tools; rejecting a valid Compose value would break projects that work today. `wip up --watch`
simply doesn't act on values it doesn't recognize.

## How `wip up --watch` acts on it

```console
$ wip up --watch
wip: watching app, mysql for exited restart: containers every 5s (running detached; Ctrl-C to stop)
```

Each tick, for every dependency (the primary container included):

1. read its `restart:` — skip unless it's one of the restarting values
2. probe its current state via `wslc list --all --filter name=… --format json`
3. if `State` is `3` (exited), run `wslc start <name>`

```console
wip: 'worker' has exited, restarting it (restart: on-failure:3)
```

`--interval N` sets the poll period (default `5` seconds). A non-positive value is rejected
*before* anything is started:

```
--interval must be a positive number
```

## Known limitations

These follow directly from polling, and are worth knowing before you rely on it:

- **Status-based, not event-based.** Each tick asks "is it exited right now?", not "did it just
  exit?" A container that crashes and is restarted by something else between ticks is invisible.
- **Exit codes are not read.** All three restarting values behave identically — an exited
  container is restarted regardless of exit status, unlike real `on-failure`, which skips a clean
  (zero) exit. Reading the code would need a heavier call this loop doesn't make.
- **Races with manual `stop`/`down`.** The loop can't tell "crashed on its own" from "you ran
  `wip stop` in another terminal", so it may restart what you just stopped. `Ctrl-C` the watch
  loop first.
- **Foreground only.** It's a loop in your terminal, not a daemon. Closing the terminal stops
  supervision. See [Concepts](Concepts#design-stance).
- **`--watch` implies `-d`.** The primary container can't hold an attached TTY and be polled on the
  same thread, so it always runs detached under `--watch`, whether or not you passed `-d`.
- **Not available under `mode: compose`.** wip never parses a service list there, so there's
  nothing to poll:
  ```
  `wip up --watch` is not supported under mode: compose (wip never parses a compose.yml
  service list in that mode, so there is nothing to poll)
  ```
  Use whatever restart support your compose tool offers.
- **`deleted` is not `exited`.** A removed container (state `4`) needs a fresh `wip up` to be
  recreated; `--watch` will never bring it back.

## Debugging "it never restarts"

Run the loop with `--debug`. wip logs the raw `wslc list` entry it read for each dependency, so you
can compare the reported `State` against the enum:

```console
$ wip up --watch --debug
wip: [debug] 'worker': {"Name"=>"worker", "State"=>3, …}
```

`0` invalid, `1` created, `2` running, `3` exited, `4` deleted — see
[Glossary](Glossary#wslccontainerstate).

## Related

- [wip up](wip-up)
- [Auto Restarting Containers](Auto-Restarting-Containers) — worked example
- [Dependencies](Dependencies)
