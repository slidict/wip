# Env Files

wip loads a `.env` file the way `docker compose` does, so values don't have to be duplicated into
`wip.yml` just to reach the container.

## Where it looks

By default: `.env` next to `wip.yml` — **not** next to your current directory. Override it:

```bash
wip --env-file config/dev.env up
wip up --env-file config/dev.env
```

The path is expanded against the current directory. A missing file is not an error; it simply
contributes nothing.

## Syntax

```dotenv
# comments and blank lines are ignored
DATABASE_URL=postgres://db/app
export RAILS_ENV=development
QUOTED="value with spaces"
SINGLE='also fine'
TRAILING=value   # this comment is stripped
```

| Rule | Behavior |
|---|---|
| `KEY=VALUE` | keys must start with a letter or `_`, then letters/digits/`_` |
| `export KEY=VALUE` | the `export ` prefix is accepted and stripped |
| `# …` | full-line comments ignored |
| blank lines | ignored |
| `"…"` / `'…'` | surrounding quotes stripped; contents kept verbatim |
| unquoted values | trailing ` #comment` is stripped, then whitespace trimmed |
| anything else | silently skipped (no error) |

Note the asymmetry: an inline `#` comment is only stripped from **unquoted** values. If your value
legitimately contains ` #`, quote it.

## Precedence

`.env` supplies **defaults only**. Anything set in `wip.yml` wins:

```
.env  <  dependencies.<name>.env  <  interaction.<name>.env
```

```yaml
# .env
RAILS_ENV=development
```

```yaml
# wip.yml
dependencies:
  app:
    env:
      RAILS_ENV: test      # wins → container sees test
```

Every value reaches the container as an explicit `-e KEY=VALUE` flag, so what you see in
`wip config` is what the container gets.

## Which commands load it

All of them that create or enter a container: `build`, `up`, `run`, `exec`, `shell`, and every
interaction.

## Also used for compose.yml interpolation

Under [Compose Native Mode](Compose-Native-Mode), the same file resolves `${VAR}` references inside
`compose.yml` — with the **shell environment winning** over `.env`, matching Compose's own rule.
Using one file for both means compose interpolation and the env actually passed to containers
never disagree. See [Compose Variable Interpolation](Compose-Variable-Interpolation).

Note the difference in precedence between the two uses:

| Use | Precedence |
|---|---|
| Env passed to containers | `wip.yml` `env:` beats `.env`; the host shell is not consulted |
| compose.yml `${VAR}` | host shell env beats `.env` |

Host environment pass-through (a bare `KEY` with no value) is **not** supported for container env —
see [Compose File Support](Compose-File-Support).

## Secrets

`.env` is the right place for real secrets, but only if it's actually untracked:

```bash
echo '.env' >> .gitignore
git check-ignore .env      # should print .env
```

`wip config` masks secret-looking keys when printing, and `--debug` masks `-e` values, but neither
encrypts anything. See [Secret Masking](Secret-Masking).

## Related

- [Secret Masking](Secret-Masking)
- [Global Options](Global-Options)
- [Compose Variable Interpolation](Compose-Variable-Interpolation)
