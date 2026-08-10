# wip vs docker compose

wip is not a Compose implementation and doesn't try to be. This page is about what wip adds *on
top of* driving `wslc` yourself, and how its `compose.yml` handling compares to the real thing.

## wip vs. driving `wslc` by hand

Without wip, a typical project command looks like:

```bash
wslc exec -it -w /app \
  -e RAILS_ENV=development \
  -e DATABASE_URL=postgres://db/app \
  app bin/rails console
```

With wip:

```bash
wip rails console
```

What you get beyond the shorter line:

| | Benefit |
|---|---|
| **Argument-array safety** | Every value is passed as one literal argument — no shell re-interpretation of spaces, quotes, `;`, or `$(...)` in an env value or path |
| **`.env` support** | Loaded automatically, like Compose; `wslc` has none — see [Env Files](Env-Files) |
| **`.dockerignore` support** | `wslc build` sends the context as-is; wip filters it first — see [Dockerignore](Dockerignore) |
| **Build-context caching** | A persistent Windows-side shadow copy, incrementally updated — see [Shadow Build Context](Shadow-Build-Context) |
| **Source sync** | The fix for slow bind-mounted boots — see [Fixing a Slow Boot](Fixing-a-Slow-Boot) |
| **Sidecar orchestration** | Network creation and ordered startup, so `db`/`redis` resolve by name — see [Networking](Networking) |
| **Restart approximation** | `wip up --watch` — see [Restart Policies](Restart-Policies) |
| **Diagnostics** | `wip doctor`, `--debug` timings, resource snapshots, error hints |
| **One place for commands** | A fresh clone runs `wip rspec` without tribal knowledge |

## wip vs. real Docker Compose

Where `compose-native` deliberately stops short:

| Compose feature | wip `compose-native` |
|---|---|
| `image`, `command`, `environment`, `working_dir`, `user` | ✔ |
| `ports`, `volumes` | short syntax only |
| `build` (context, dockerfile, args) | ✔ — see [Compose Build](Compose-Build) |
| `depends_on` ordering | ✔ — see [Compose Depends On](Compose-Depends-On) |
| `depends_on` health conditions | ✘ |
| `healthcheck` | ✘ |
| `restart` | stored; approximated by `--watch` |
| `profiles` | parsed; gated services skipped — see [Compose Profiles](Compose-Profiles) |
| `${VAR}` interpolation | ✔ — see [Compose Variable Interpolation](Compose-Variable-Interpolation) |
| YAML anchors / merge keys | ✔ |
| `deploy` / scaling | ✘ |
| `env_file` | ✘ (use `.env` next to `wip.yml`) |
| `extends` | ✘ |
| `entrypoint` | ✘ |
| Top-level `networks:` / `volumes:` / `configs:` / `secrets:` | ignored (not rejected) |
| Multi-service `logs` | one at a time |
| `tty` / `stdin_open` / `networks` / `cap_add` per service | accepted and ignored |

Full detail: [Compose File Support](Compose-File-Support).

Anything unsupported **inside a service** is a load-time error naming the key, rather than a silent
drop — so you find out at `wip doctor`, not three hours into debugging.

## Can I keep using both?

Yes, and it's a common setup: `compose.yml` stays the source of truth for services, `wip.yml` adds
your commands on top. Teammates on plain Docker keep using `docker compose` against the same file;
nothing about the services is duplicated.

```yaml
# wip.yml
version: 1
mode: compose-native
compose:
  service: app

interaction:
  rspec:
    command: bundle exec rspec
```

The constraint is that your `compose.yml` must stay within the supported subset for
`compose-native` to read it — or you use [`mode: compose`](Compose-Mode), which has no such limit
because it delegates.

## The honest summary

If you're on Docker, use Docker Compose — it's more complete, more mature, and wip has no Docker
backend. wip exists because WSLC has no equivalent, and the gaps around it (no `.dockerignore`,
no `.env`, no Compose, slow bind mounts) are real enough to need filling.

## Related

- [Comparison](Comparison)
- [Reusing an Existing compose.yml](Reusing-an-Existing-compose-yml)
- [Third Party Compose Tools](Third-Party-Compose-Tools)
