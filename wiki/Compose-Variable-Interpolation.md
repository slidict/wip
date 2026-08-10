# Compose Variable Interpolation

`${VAR}` references inside `compose.yml` are substituted the way `docker compose` does, before wip
looks at the file at all. This is what lets `user: ${USER_ID}:${GROUP_ID}` reach `wslc` already
resolved instead of literally.

Applies to [`mode: compose-native`](Compose-Native-Mode) only — under
[`mode: compose`](Compose-Mode) the external tool does its own interpolation.

## Supported syntax

| Form | Behavior |
|---|---|
| `${VAR}` | the value, or an empty string if unset |
| `$VAR` | same as `${VAR}` |
| `${VAR:-default}` | `default` if `VAR` is unset **or empty** |
| `${VAR-default}` | `default` only if `VAR` is unset (an empty value stays empty) |
| `$$` | an escaped literal `$` |

```yaml
services:
  app:
    image: myapp:${TAG:-dev}
    user: "${USER_ID}:${GROUP_ID}"
    environment:
      RAILS_ENV: ${RAILS_ENV:-development}
      PROMPT: "cost: $$5"        # → cost: $5
```

## Not supported — passed through unchanged

`${VAR:?error}` and `${VAR:+alternate}` aren't recognized. Unlike an unset `${VAR}` (which becomes
an empty string), these pass through **completely untouched**, braces and all:

```yaml
environment:
  MODE: ${MODE:+production}     # the container literally sees ${MODE:+production}
```

If you were relying on `:?` to enforce a required variable, that check won't happen — use
[`wip doctor`](wip-doctor) plus a sane `:-` default instead.

## Where values come from

Two sources, merged, with the **host shell winning**:

```
.env (next to wip.yml, or --env-file)  <  the process environment
```

That's Compose's own precedence rule. Using the same `.env` file wip passes to containers means
interpolation and container env never see two different files. See [Env Files](Env-Files).

```dotenv
# .env
TAG=dev
```

```bash
wip up -d              # image: myapp:dev
TAG=ci wip up -d       # image: myapp:ci  (shell wins)
```

Note this is the opposite direction from container env, where `wip.yml`'s `env:` beats `.env` and
the host shell isn't consulted at all.

## What gets interpolated

**Values only, never mapping keys** — Compose documents the same restriction. And substitution
happens *after* YAML parsing, on the already-parsed structure, which means a substituted value can
never introduce YAML syntax:

```dotenv
NOTE=hello # not a comment
```

```yaml
environment:
  NOTE: ${NOTE}     # the whole string survives, "#" included
```

Strings inside nested mappings and arrays are all covered.

## YAML aliases

`compose.yml` is parsed with aliases enabled, so anchors and merge keys work. A **self-referential**
alias is rejected rather than recursing until the stack blows:

```
compose.yml contains a self-referential YAML alias
```

Re-using the same anchor from several places is fine — that's a shared node, not a cycle.

## Debugging what was substituted

```bash
wip config
```

prints the resolved services, post-interpolation. If a value came out empty, the variable was unset
in both the shell and `.env`.

## Related

- [Env Files](Env-Files)
- [Compose File Support](Compose-File-Support)
- [wip config](wip-config)
