# FAQ

Questions rather than errors. For errors, start at
[Troubleshooting & FAQ](Troubleshooting-and-FAQ).

## Modes

**Which mode should I start with?**
No `compose.yml` → [`mode: container`](Container-Mode). Have one → try
[`mode: compose-native`](Compose-Native-Mode) first, and fall back to
[`mode: compose`](Compose-Mode) if your file needs more than the
[supported subset](Compose-File-Support). Full breakdown:
[Choosing a Mode](Choosing-a-Mode).

**Can I use `dependencies:` and `compose:` together?**
No. They're mutually exclusive — one orchestration path per project. `network:` is likewise
rejected alongside `compose:`. See [Configuration Errors](Configuration-Errors).

**What's the difference between `mode: compose` and `mode: compose-native`?**
`compose` delegates to a third-party compose-for-`wslc` binary you install yourself
(`compose.command`). `compose-native` parses `compose.yml` itself and drives `wslc` directly, with
no external tool — and gets a real `wslc run --rm` for `wip run` instead of the exec fallback.
`compose`'s coverage is whatever your tool supports; `compose-native`'s is maintained in this repo
and actively extended.

**`dependencies:` already gives me sidecars — why would I need `compose-native`?**
They're for different starting points, not alternatives. No `compose.yml`? Declare containers in
`wip.yml`'s own shape with `dependencies:`. Already have one? Reusing it is what `compose-native`
and `mode: compose` are both for — so the real comparison is between *those two*, not against
`dependencies:`.

**What happens to `compose-native` once `wslc` gets official Compose support?**
It exists to close that gap (tracked in
[microsoft/WSL#40948](https://github.com/microsoft/WSL/issues/40948)), and its coverage keeps
growing until that lands. `wip.yml`'s shape (`mode:`, `compose:`) isn't planned to change for
existing setups, so whatever happens next won't require rewriting your config.

**Can I switch modes later?**
Yes. Going `compose` → `compose-native` usually just means deleting `compose.command`; the reverse
means adding it. `interaction:`, `.env`, and `sync:` carry over unchanged.

## Configuration

**Do I have to set `container:`?**
Only under `mode: container`, and only once `dependencies:` has entries — but then it's required.
There's deliberately no default. Under compose modes, `compose.service` plays that role instead.
See [Dependencies](Dependencies).

**Do I need to rename `interaction:` when migrating from dip?**
No. `interaction:` is wip's primary spelling too. `commands:` is an accepted alias, but declaring
both in one file is a `ConfigError`. See [Migrating from dip](Migrating-from-dip).

**Why does `wip config` print `commands:` when I wrote `interaction:`?**
They're two names for one feature, and the effective-config output normalizes to `commands:`.
Nothing changed about your file.

**Can I use YAML anchors?**
In `compose.yml`, yes — aliases are enabled, so merge keys work. In `wip.yml`, no — it's parsed
with aliases disabled, and an anchor is a parse error. See
[Config File Discovery](Config-File-Discovery).

**Where do relative paths resolve from?**
The directory holding `wip.yml`, never your current directory. That's what makes wip behave
identically from any subdirectory. (`compose.yml`'s `build.context` resolves against `compose.yml`
instead — Compose's own rule.)

## Environment and secrets

**Is `.env` loaded automatically?**
Yes, from next to `wip.yml`, like `docker compose`. `--env-file PATH` overrides it. `env:` in
`wip.yml` always wins over `.env`. See [Env Files](Env-Files).

**Is it safe to put passwords in `wip.yml`?**
`wip config` masks keys matching token/password/secret/credential/auth, but the file itself is
plain text and the masking is a key-name heuristic — `API_KEY` and `DATABASE_URL` aren't caught.
Keep real secrets in `.env` (gitignored — verify with `git check-ignore .env`) or your runtime
environment. See [Secret Masking](Secret-Masking).

**Can I pass a host environment variable through without naming its value?**
Not in `compose.yml` — a bare `KEY` or a null value is rejected. Put it in `.env` instead. Note
that compose.yml's `${VAR}` interpolation *does* read your shell environment, so
`FOO: ${FOO}` achieves it. See [Compose Variable Interpolation](Compose-Variable-Interpolation).

## Sync

**Is `sync:` required?**
No, entirely optional. Add it when boot times feel slow with a bind-mounted app directory. See
[Fixing a Slow Boot](Fixing-a-Slow-Boot).

**How does `sync:` actually fix the slow boot?**
The slowness is `.:/app`-style bind mounts crossing virtiofs, where a framework that stats/opens
thousands of files at startup pays a round trip per file. `sync:` moves the app off that path: the
host source is mounted read-only, the app runs off a named volume (fast native storage inside the
VM), and wip mirrors one into the other with `rsync`. The trade-off is a one-way, slightly-delayed
mirror instead of a live view.

**Do I need `rsync` in my image?**
Under the default `sync.mode: exec`, yes. Avoid it with `sync.mode: run` plus `sync.build`. See
[rsync Not Found](rsync-Not-Found).

**Why do files my app writes keep disappearing?**
The mirror is one-way with `--delete`. Exclude the path, give it its own volume, or set
`delete: false`. See [Continuous Sync](Continuous-Sync).

**Do I have to run `wip sync --watch`?**
No — `wip sync` on demand is often enough. The watcher is for fast iteration or for a dev server
inside the container that needs to see changes on its own.

## Commands

**Why does `wip <name>` run a built-in instead of my command?**
Built-ins always win. Use `wip dispatch <name>`, or rename your entry. See
[wip dispatch](wip-dispatch).

**Why doesn't `wip logs` work?**
It's compose-modes-only. Under `mode: container`, use `wslc logs -f <name>` directly. See
[wip logs](wip-logs).

**Why did `wip run` say it's exec'ing instead?**
`mode: compose`'s vocabulary is exec-only, so `run` falls back. `compose-native` and `container`
both get a real `wslc run --rm`. See [wip run](wip-run).

**My new `ports:` entry isn't listening. Why?**
Ports apply at container **creation**. An existing container predates the change:
`wip down && wip up -d`.

**Does `wip down` delete my database volume?**
No. `wip down` removes containers only — named volumes and the network survive. Remove volumes
explicitly with `wslc volume remove`. See [wip down](wip-down).

**Why does `rails console` exit immediately?**
No TTY was allocated. Set `interactive: true` on the interaction, and check you're in a real
terminal — wip requires both stdin and stdout to be TTYs. See [TTY Allocation](TTY-Allocation).

## Operations

**Can wip restart containers automatically?**
Approximately, via `wip up --watch` — a foreground poll loop, not a daemon, that restarts exited
containers whose `restart:` allows it. It doesn't read exit codes and can race with manual stops.
See [Restart Policies](Restart-Policies).

**Is there a background daemon?**
No, deliberately. Every `--watch` variant is a foreground loop tied to an open terminal. See
[Concepts](Concepts#design-stance).

**Can I run wip in CI?**
Yes, if the runner has WSL2 and WSLC. TTY handling degrades automatically and exit codes
propagate. See [Using wip in CI](Using-wip-in-CI).

**`wslc.exe` isn't found — what do I do?**
See [WSLC Not Found](WSLC-Not-Found).

## Project

**How do I report a bug?**
See [Reporting Issues](Reporting-Issues).

**How do I contribute?**
See [Development](Development).
