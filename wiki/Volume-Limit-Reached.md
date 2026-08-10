# Volume Limit Reached

```
0x8007000e
```

or "too many mounted volumes" (in whichever language your Windows is set to). The WSLC session has
run out of mount slots.

## The hint wip prints

```
The WSLC session has reached its mounted-volume limit.

Stop any containers you no longer need, then restart the session:

  wslc container list
  wslc container stop <container-name>
  wslc system session terminate

Then retry the command.
```

Triggered by output containing `0x8007000e`, "too many mounted volumes", or its Japanese
equivalent.

## Why it happens

Every bind mount and named volume attached to a running container consumes a slot in the WSLC
session, and the session has a fixed ceiling. Slots aren't always released promptly when a
container stops, so they accumulate over a long working session — especially with:

- several projects up at once, each with a handful of volumes
- `sync:` configured, which adds two mounts per container (read-only source + named volume)
- repeated `wip run` invocations that each attach the full volume set
- containers left running from a previous project

## Fix

**1. See what's running:**

```bash
wslc container list
```

**2. Stop what you don't need:**

```bash
wip down                        # this project
wslc container stop <name>      # anything else
```

**3. Restart the session** — this is the step that actually reclaims the slots:

```bash
wslc system session terminate
```

**4. Retry:**

```bash
wip up -d
```

Terminating the session stops every WSLC container on the machine, not just yours. Check with other
people if you share it.

## Reducing pressure

**Trim your volume list.** Every entry in `volumes:` costs a slot on every container that declares
it. Consolidate where you can.

**Don't declare volumes on sidecars that don't need them.** A Postgres container needs its data
volume; it doesn't need your source tree.

**Tear down projects you're not using**, rather than leaving three stacks up all day:

```bash
cd ~/project-a && wip down
```

**With `sync:`, drop the now-redundant bind mount.** wip already replaces `.:/app` with the
read-only mount plus the volume — but any *other* mount of the same tree you declared by hand is
extra. Check `wip config`.

**Prefer `sync.mode: exec` over `run`** where you can. A throwaway container per mirror attaches
the mount set each time; exec'ing into the existing one doesn't. See [Sync Modes](Sync-Modes).

## Related

- [Source Sync](Source-Sync)
- [Dependencies](Dependencies)
- [wip down](wip-down)
