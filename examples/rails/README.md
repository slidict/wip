# Rails example (`mode: container`)

A Rails app with Postgres and Redis sidecars, driven directly by `wip` with no `compose.yml`
involved — see [Which mode should you use?](../../README.md#which-mode-should-you-use) for when
this mode fits.

## Files

- [`wip.yml`](wip.yml) — the primary container (`app`), its `db` and `redis` dependencies, and
  the commands you'd actually run day to day
- [`Dockerfile`](Dockerfile) — a minimal placeholder image so `wip build` has something to run;
  replace it with your app's real Dockerfile

> **The `postgres` / `password` credential here is for local development only.** `db` publishes
> `127.0.0.1:5432` so it stays on the loopback interface rather than every host interface. Swap in
> a real secret before running this anywhere shared or reachable.

## Setup

1. Copy `wip.yml` (and `Dockerfile`, if you don't have one yet) into the root of your Rails app.
2. Point `dependencies.app.image` / `commands.build.tag` at your own image name, and adjust
   `DATABASE_URL` / `POSTGRES_DB` if your app uses a different database name.
3. Run the checks:

   ```powershell
   wip doctor
   wip build
   wip up -d
   wip console
   ```

   `wip up -d` waits for Postgres's `healthcheck:` to pass before it's considered ready — no
   manual "wait for the database" step needed.

4. Everyday commands:

   ```powershell
   wip bundle install
   wip rspec
   wip migrate
   wip psql          # opens psql against the db dependency, not app
   wip shell
   ```

5. `wip down` stops and removes the stack; `wip logs -f` follows the app container's logs.

See the [Configuration Reference](https://github.com/slidict/wip/wiki/Configuration-Reference)
for every key `wip.yml` accepts.
