# wip version

Prints wip's own version, then asks `wslc` for its version.

```
wip version
```

```console
$ wip version
wip 1.1.3
wslc version 0.1.x
```

## Behavior

1. Prints `wip <VERSION>` — always, unconditionally.
2. Loads `wip.yml` and resolves `wslc.command`.
3. Runs `<resolved wslc> version`, whose output goes straight to your terminal.

## It degrades gracefully

Step 2 or 3 failing is **not** an error here. If `wip.yml` is missing, unparsable, or `wslc` can't
be found, `wip version` still prints wip's own version and exits `0`:

```console
$ cd /tmp && wip version
wip 1.1.3
```

That's deliberate — `wip version` is what you run to check the install, so it must work before
anything else does. Every other command fails loudly in the same situation.

## Uses

**Confirming an install or upgrade:**

```bash
gem install wslc-wip
wip version
```

**Running from a source checkout:**

```bash
bundle exec exe/wip version
```

**In a bug report** — always include this output; see [Reporting Issues](Reporting-Issues).

## Related

- [wip doctor](wip-doctor) — the full environment diagnosis, when `version` isn't enough
- [WSLC Not Found](WSLC-Not-Found) — if the second line never appears
- [Config File Discovery](Config-File-Discovery) — how `wslc.command` is resolved
