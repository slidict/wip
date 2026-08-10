# wip Wiki

`wip` is a Ruby-built CLI that brings a [`dip`](https://github.com/bibendi/dip)-like workflow to
Microsoft WSLC. This wiki is where the deeper explanations, guides, and reference material that
don't fit in the [README](https://github.com/slidict/wip#quick-start) live.

> Rule of thumb: the README is the fastest path to "get it running"; the wiki is for daily use,
> troubleshooting, and contributing. Every page here covers exactly one feature, so you can link
> a teammate straight at the thing they asked about.

## Start here

- [Getting Started](Getting-Started) — install through your first `wip up`
- [Choosing a Mode](Choosing-a-Mode) — the one decision you have to make before writing `wip.yml`
- [Concepts](Concepts) — what `wip` is, the three modes, and the design stance behind them
- [Glossary](Glossary) — primary container, sidecar, interaction, sync volume, shadow context

## The three modes

| Mode | wip's role | Page |
|---|---|---|
| `container` (default) | wip declares and drives containers itself from `wip.yml` | [Container Mode](Container-Mode) |
| `compose-native` | wip parses your `compose.yml` and drives `wslc` directly | [Compose Native Mode](Compose-Native-Mode) |
| `compose` | wip is a thin bridge to a third-party compose-for-`wslc` binary | [Compose Mode](Compose-Mode) |

## Reference

- [Configuration Reference](Configuration-Reference) — every `wip.yml` key, one page per feature
- [CLI Command Reference](CLI-Command-Reference) — every command, one page each
- [Global Options](Global-Options) — `--config`, `--env-file`, `--debug`, `--debug-log`
- [Configuration Errors](Configuration-Errors) — every `ConfigError` and what triggers it

## Usage

- [Guides](Guides) — task-oriented how-tos
- [Troubleshooting & FAQ](Troubleshooting-and-FAQ) — errors, diagnostics, frequently asked questions
- [Comparison](Comparison) — how `wip` relates to `dip`, `docker compose`, and other tools

## Project

- [Development](Development) — dev setup, tests, and contributing
- [Architecture](Architecture) — how the codebase is laid out
- [Release Process](Release-Process) — versioning and how releases are cut

## Related links

- [GitHub repository](https://github.com/slidict/wip)
- [Homepage](https://wslc-wip.slidict.com/)
- [RubyGems](https://rubygems.org/gems/wslc-wip)
- [CONTRIBUTING.md](https://github.com/slidict/wip/blob/main/CONTRIBUTING.md)
