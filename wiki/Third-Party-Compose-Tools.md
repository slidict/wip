# Third Party Compose Tools

`wslc` has no native Compose support yet — tracked upstream in
[microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948). Independent projects fill the
gap, and [`mode: compose`](Compose-Mode) bridges to whichever one you install.

## Known implementations

| Project | Language |
|---|---|
| [bacarndiaye/wslc-compose](https://github.com/bacarndiaye/wslc-compose) | Python |
| [inuyume/wslc-compose](https://github.com/inuyume/wslc-compose) | Go |

Others exist and more will appear — `wslc` is new and still evolving. This list is a starting
point, not an endorsement or an exhaustive survey.

## Why wip doesn't pick one

`compose.command` has **no default**, unlike `wslc.command`, which defaults to `auto` and searches
a candidate list. That asymmetry is deliberate:

- The ecosystem is young and moving. Baking in a favorite would make wip's behavior depend on
  someone else's release cadence.
- Every implementation is treated equally. `compose.command` takes a binary name or an absolute
  path — nothing about wip prefers one shape of tool.
- Choosing a third-party dependency for your project isn't wip's call to make.

The same reasoning applies to `sync.image`: wip doesn't publish or default to an rsync image
either — see [Sync Modes](Sync-Modes).

## What wip requires of one

Whichever tool you point at must understand this subset of the Compose CLI vocabulary:

```
<command> -f FILE [-p PROJECT] up [-d]
<command> -f FILE [-p PROJECT] stop
<command> -f FILE [-p PROJECT] down
<command> -f FILE [-p PROJECT] exec [-T] SERVICE COMMAND...
<command> -f FILE [-p PROJECT] logs [-f] [SERVICE...]
```

That's all wip drives. Notably absent: `run`, which is why `wip run` falls back to `exec` under
this mode.

## Configuring it

```yaml
version: 1
mode: compose
compose:
  service: app
  command: wslc-compose        # binary name on PATH, or an absolute path
  file: compose.yml            # optional
  project: myapp               # optional
```

```bash
wip doctor
```

```console
[OK] Found wslc-compose
[OK] compose command is available
[OK] Found compose file /home/me/app/compose.yml
```

If the binary can't be resolved, wip says so and points you here:

```
compose command was not found.

Checked:
  wslc-compose

wip doesn't bundle or pin a compose-for-wslc implementation — install one and set
compose.command in wip.yml to its binary name or path, e.g.:

  https://github.com/bacarndiaye/wslc-compose
  https://github.com/inuyume/wslc-compose
```

## Do you need one at all?

Often not. [`mode: compose-native`](Compose-Native-Mode) reads the same `compose.yml` with no
external binary, and gives you a real `wslc run --rm` plus `wip up --watch` that `mode: compose`
can't. Reach for an external tool when your compose file needs features outside
[the supported subset](Compose-File-Support) — health checks, scaling, long-syntax mounts,
`extends`.

Decision help: [Reusing an Existing compose.yml](Reusing-an-Existing-compose-yml).

## When wslc ships Compose support

Both compose modes exist because of the upstream gap. wip's stated intent is that `wip.yml`'s shape
(`mode:`, `compose:`) stays stable for existing `container` / `compose` / `compose-native` setups,
so whatever happens once `wslc` catches up won't require rewriting your config.

## Related

- [Compose Mode](Compose-Mode)
- [Compose Native Mode](Compose-Native-Mode)
- [Comparison](Comparison)
