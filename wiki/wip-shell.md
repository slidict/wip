# wip shell

Opens an interactive shell in the primary container.

```
wip shell
```

## Resolution order

1. **If `interaction.shell` is defined in `wip.yml`, it wins** — `wip shell` runs your entry
   verbatim, exactly as `wip dispatch shell` would.
2. Otherwise, try `bash` via `wslc exec -it`.
3. If that exits non-zero, try `sh`.

The `bash` → `sh` fallback covers Alpine-based images, which typically ship only `sh`.

## Customizing it

```yaml
interaction:
  shell:
    command: zsh
    interactive: true
```

Or with a different working directory / user:

```yaml
interaction:
  shell:
    command: bash -l
    workdir: /app
    user: "1000:1000"
    interactive: true
```

Since your entry replaces the built-in behavior entirely, the `bash` → `sh` fallback no longer
applies — you're naming the shell yourself.

## Requires a running container

`wip shell` is an `exec`, so the container must be up:

```bash
wip up -d
wip shell
```

For a shell in a fresh container instead:

```bash
wip run bash
```

## Interactive behavior

The shell runs behind a pseudo-terminal, which is what makes job control, `Ctrl-C`, editors, and
pagers work correctly. wip also keeps the pty sized to your real terminal and re-syncs on resize,
so full-screen programs (`less`, `vim`, `htop`) render properly when you resize the window.

wip's own terminal is switched to raw mode for the duration, so only the pty's line discipline
echoes your input — otherwise every keystroke would appear twice.

On native Windows, where Ruby has no `openpty`, wip falls back to letting the child inherit its
real stdio instead. Everything still works; wip just can't observe the output, so error hints
aren't generated on that path.

If stdin or stdout isn't a real TTY (piped, redirected, CI), no TTY is allocated at all. See
[TTY Allocation](TTY-Allocation).

## Under compose mode

Bridged, with the same fallback:

```
<compose command> -f FILE [-p PROJECT] exec <compose.service> bash
<compose command> -f FILE [-p PROJECT] exec <compose.service> sh
```

## Related

- [wip exec](wip-exec)
- [wip run](wip-run)
- [TTY Allocation](TTY-Allocation)
- [Interactions](Interactions)
