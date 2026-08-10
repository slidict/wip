# Compose Profiles

`profiles:` in a `compose.yml` service marks it as opt-in — real Compose only starts it when you
activate the profile with `--profile`. wip has **no `--profile` flag**, so profile-gated services
are parsed, validated, and then skipped.

## Behavior

```yaml
services:
  app:
    image: myapp:dev
  db:
    image: postgres:16
  mailcatcher:
    image: sj26/mailcatcher
    profiles: [dev-tools]
  seed:
    image: myapp:dev
    command: bundle exec rails db:seed
    profiles: [tools]
```

```bash
wip up -d     # starts db and app. mailcatcher and seed are ignored.
```

Profile-gated services are:

- **parsed and validated** like any other — an unsupported key in one is still an error
- **included in `depends_on` validation and cycle detection**
- **excluded** from `wip up`, `wip stop`, `wip down`, and `wip up --watch`
- **excluded** from image builds (a profile-gated service with `build:` is never built)
- **absent** from `wip config`'s `dependencies:` output

This matches what real Compose does with an unactivated profile: the service exists in the model
but isn't part of the run.

`profiles:` must be an array of strings:

```
compose.yml: services.mailcatcher.profiles must be an array of strings
```

## `compose.service` may not be profile-gated

The primary service is the one thing wip always starts, so pointing `compose.service` at a
profile-gated service is rejected up front rather than failing later with a confusing "no matching
service" message:

```
compose.service 'seed' is gated behind profiles: in compose.yml, but wip has no --profile flag
to activate one — pick a service with no profiles: or remove profiles: from it
```

## A startable service may not depend on a gated one

```yaml
services:
  app:
    depends_on: [mailcatcher]
  mailcatcher:
    profiles: [dev-tools]
```

```
compose.yml: services.app depends_on 'mailcatcher', gated behind profiles: (dev-tools) wip
never activates (no --profile flag)
```

Real Compose treats this as an invalid model too, since the dependency would never start. A gated
service depending on another gated service is fine — neither runs.

## Working with a profiled compose.yml

If your compose file uses profiles heavily, you have three options:

1. **Leave them.** Gated services are simply skipped, which is usually what you want in a dev
   container workflow — they exist for CI, seeding, or one-off tooling.
2. **Run the gated thing yourself.** Since it isn't managed by wip, use `wslc run` directly, or add
   an [interaction](Interactions) with `type: run`:
   ```yaml
   interaction:
     seed:
       type: run
       command: bundle exec rails db:seed
   ```
3. **Use [`mode: compose`](Compose-Mode).** The external tool has its own `--profile` support, and
   wip just bridges to it.

## Related

- [Compose File Support](Compose-File-Support)
- [Compose Depends On](Compose-Depends-On)
- [Compose Native Mode](Compose-Native-Mode)
