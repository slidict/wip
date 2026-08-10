# Secret Masking

wip masks secret-looking values in two places: `wip config` output, and `--debug` command logs.
Neither is encryption — they exist so that pasting output into an issue or a chat doesn't leak
credentials.

## In `wip config`

Any **key** matching this pattern, case-insensitively, has its value replaced with `[REDACTED]`:

```
token | password | secret | credential | auth
```

The match is a substring match on the key name, applied recursively through the whole config —
nested hashes and arrays included.

```console
$ wip config
---
dependencies:
  development.mysql:
    env:
      MYSQL_ROOT_PASSWORD: "[REDACTED]"
      MYSQL_DATABASE: development
```

`MYSQL_ROOT_PASSWORD` matches (`password`). `MYSQL_DATABASE` doesn't, and is printed as-is.

### What it misses

The pattern is a heuristic on key names. These are **not** masked:

```yaml
env:
  API_KEY: sk-live-…          # "key" isn't in the pattern
  DATABASE_URL: postgres://user:pw@db/app   # secret is inside the value
  PRIVATE_PEM: "-----BEGIN…"
```

Don't treat unmasked output as "safe to share" — read it before pasting.

## In `--debug` output

Every logged command masks `-e KEY=value` pairs, regardless of the key name:

```console
$ wip rails c --debug
wip: [debug] running: wslc.exe exec -it -w /app -e RAILS_ENV=*** -e DATABASE_URL=*** app bin/rails c
```

This is a blanket rule on the flag, not a pattern on the key — so it covers `API_KEY` and
`DATABASE_URL` too. Other flags (`-v`, `-p`, `-u`) are printed verbatim, so a credential embedded
in a volume path or a URL passed as a positional argument would still show.

## What is not protected

- **`wip.yml` on disk** is a plain file. Committing secrets there commits them.
- **The container's environment.** Anything you pass reaches the process; `wslc exec … env` shows it.
- **Shell history**, if you pass secrets as CLI arguments to an interaction.
- **The `.env` file**, unless it's gitignored.

## Recommended handling

1. Keep real secrets in `.env`, not `wip.yml`.
2. Make sure `.env` is ignored:
   ```bash
   echo '.env' >> .gitignore
   git check-ignore .env
   ```
3. Reference them from `wip.yml` only by name (they're merged in automatically — see
   [Env Files](Env-Files)).
4. For anything genuinely sensitive, prefer your runtime environment or a secret manager over a
   file in the repo at all.

## Related

- [Env Files](Env-Files)
- [wip config](wip-config)
- [Debug Output](Debug-Output)
- [Reporting Issues](Reporting-Issues) — what's safe to attach to a bug report
