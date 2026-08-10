# TTY Allocation

Whether wip passes `-it` to `wslc` — and therefore whether you get an interactive terminal — is
decided by combining three things.

## The rule

```
TTY allocated  =  (config says interactive)  AND  (CLI didn't say otherwise)  AND  (stdin and stdout are both real TTYs)
```

All three must hold. Any one of them false means no `-it`.

| Input | Where it comes from |
|---|---|
| **Config** | `interactive:` on the interaction, or on the `dependencies:` entry |
| **CLI** | `--no-interactive` on `exec` / `run` (interactive is the default flag value) |
| **Reality** | both `$stdin` and `$stdout` report as TTYs |

## Defaults

| Context | Default `interactive` |
|---|---|
| `dependencies:` entry | `false` |
| `interaction:` entry | `false` |
| `wip exec` / `wip run` CLI flag | `true` (disable with `--no-interactive`) |
| `wip shell` | `true` |
| `wip up` (attached, i.e. no `-d`) | `true` |
| `wip up -d` | `false` |

So `wip exec bash` gets a TTY in a terminal, but `wip rspec` (an interaction with no
`interactive: true`) does not — unless you ask for it:

```yaml
interaction:
  rails:
    command: bin/rails
    interactive: true      # needed for `wip rails console`
  rspec:
    command: bundle exec rspec   # no TTY needed
```

## The reality check exists so pipes work

Because wip verifies that both streams are real TTYs, this behaves correctly with no extra flags:

```bash
wip rspec                    # interactive terminal → TTY if configured
wip rspec | tee out.txt      # stdout is a pipe → no TTY
wip rspec > out.txt          # same
echo y | wip exec bin/thing  # stdin is a pipe → no TTY
```

In CI, neither stream is a TTY, so nothing is allocated even if `interactive: true` is set. You
generally don't need `--no-interactive` in CI — though passing it is harmless and explicit. See
[Using wip in CI](Using-wip-in-CI).

## compose.yml `tty:` / `stdin_open:` are ignored

Under [compose-native mode](Compose-Native-Mode), those keys are accepted and silently ignored.
TTY allocation is a per-invocation decision — `wip rspec` and `wip rails console` against the same
service want different answers — not a fixed property of a service. See
[Compose File Support](Compose-File-Support).

## What "interactive" changes mechanically

When a TTY is allocated, wip runs the child behind a **pseudo-terminal** rather than piping its
streams:

- the child gets a genuine controlling terminal: job control, `Ctrl-C` → `SIGINT`, `isatty()`-gated
  colored output all work
- output still routes through wip first, so error hints can be generated — inherited file
  descriptors would bypass wip entirely
- wip's own terminal switches to raw mode so only the pty echoes your keystrokes
- the pty is sized to your terminal and re-synced on `SIGWINCH`, so `less` / `vim` / `htop` render
  correctly across a window resize

Without a TTY, streams are piped and pumped, which closes the child's stdin immediately — fine for
`rspec`, fatal for `rails console`.

On native Windows there's no `openpty`, so wip lets the child inherit its real stdio instead.
Everything still works; wip just can't observe the output, so no error hints on that path.

## Under compose mode

The bridge inverts the flag: `-T` (disable pseudo-TTY) is added when **non**-interactive, matching
the Compose CLI's own convention.

## Debugging

`--debug` shows the resolved command, `-it` included or not:

```console
wip: [debug] running: wslc.exe exec -it -w /app app bin/rails c
wip: [debug] running: wslc.exe exec -w /app app bundle exec rspec
```

If a console exits immediately with an EOF-ish error, that's a missing `-it` — check
`interactive: true` and whether your terminal is really a TTY.

## Related

- [Interactions](Interactions)
- [wip exec](wip-exec) / [wip run](wip-run) / [wip shell](wip-shell)
- [Using wip in CI](Using-wip-in-CI)
