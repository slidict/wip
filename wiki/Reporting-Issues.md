# Reporting Issues

Bug reports and feature requests go to
[github.com/slidict/wip/issues](https://github.com/slidict/wip/issues).

## Before filing

1. Check [Troubleshooting & FAQ](Troubleshooting-and-FAQ) and
   [Configuration Errors](Configuration-Errors) — most messages are documented with their cause.
2. Run `wip doctor`. It catches environment and configuration problems that look like bugs.
3. Search existing issues.

## What to include

### 1. Versions

```bash
wip version
```

Plus your OS, WSL, and WSLC versions:

```powershell
wsl --version
wslc version
```

### 2. `wip doctor` output

```bash
wip doctor
```

Paste it whole, including the `[OK]` lines — they rule things out.

### 3. Your `wip.yml`

Redact secrets first. `wip config` is often better than the raw file, since it shows what wip
actually resolved (defaults filled in, `${VAR}` substituted) and masks obvious secrets:

```bash
wip config
```

⚠️ Masking is a key-name heuristic — `API_KEY`, `DATABASE_URL`, and secrets embedded in values are
**not** caught. Read the output before pasting. See [Secret Masking](Secret-Masking).

Under compose-native mode, include the relevant part of `compose.yml` too.

### 4. The exact command and its full output

```console
$ wip up -d
wip: creating network 'app-tier'
…the error…
```

Not a paraphrase — the exact text, including anything `wslc` printed.

### 5. Debug output

The single most useful attachment:

```bash
WIP_DEBUG=1 wip <the failing command> --debug-log=-
```

`--debug-log=-` forces resource snapshots inline instead of into a temp file, so one paste has
everything. Environment values are masked as `KEY=***`. See [Debug Output](Debug-Output).

### 6. What you expected

One line. "I expected `wip run` to start a new container, but it exec'd into the running one" is
enough — and sometimes reveals the behavior is documented rather than broken.

## A template

```markdown
### Environment
- wip: 1.1.3
- Ruby: 3.4.1
- WSL: 2.x
- WSLC: 0.x
- Host: Windows 11 / x86_64

### What I did
`wip up -d`

### What I expected
The app container to start.

### What happened
<full output>

### wip doctor
<full output>

### wip config
<redacted output>

### Debug output
<WIP_DEBUG=1 output>
```

## Feature requests

Say what you're trying to do, not only what API you'd like. Several existing behaviors exist
because a specific workflow demanded them — `sync:`, `--watch`, `shadow_context:` — and knowing the
workflow makes it much easier to judge a proposal.

Some things are deliberate non-goals; check [Concepts](Concepts#design-stance) first:

- a background daemon or service
- full Compose spec coverage in `compose-native` (bounded by the upstream gap it exists to fill)
- picking a default third-party compose-for-`wslc` tool or rsync image

## Contributing a fix

See [Development](Development) and
[CONTRIBUTING.md](https://github.com/slidict/wip/blob/main/CONTRIBUTING.md).

## Related

- [Troubleshooting & FAQ](Troubleshooting-and-FAQ)
- [Debug Output](Debug-Output)
- [Secret Masking](Secret-Masking)
