# Node example: Web + MySQL + Redis (`mode: compose-native`)

A Node.js web service with MySQL and Redis, described in a normal `compose.yml` that `wip`
parses itself — no third-party compose-for-`wslc` tool required. See
[Compose Native Mode](https://github.com/slidict/wip/wiki/Compose-Native-Mode) for how this
differs from `mode: compose`.

## Files

- [`compose.yml`](compose.yml) — `web`, `db` (MySQL), and `cache` (Redis) services, with a
  `healthcheck:` on `db` that `wip up` waits on before starting `web`
- [`wip.yml`](wip.yml) — points at the `web` service and adds a few day-to-day commands
- [`Dockerfile.dev`](Dockerfile.dev) — a minimal placeholder image so `wip build` has something
  to run; replace it with your app's real Dockerfile

## Setup

1. Copy `compose.yml`, `wip.yml`, and `Dockerfile.dev` into the root of your Node app (or point
   `compose.yml`'s `build.dockerfile` at your own).
2. Adjust `MYSQL_DATABASE` / `DATABASE_URL` if your app uses a different database name.
3. Run the checks:

   ```powershell
   wip doctor
   wip build
   wip up -d
   ```

   `wip up -d` waits for `db`'s `healthcheck:` to pass (`depends_on: condition: service_healthy`)
   before starting `web`.

4. Everyday commands:

   ```powershell
   wip npm install
   wip test
   wip mysql          # opens the mysql client against the db service
   wip redis-cli       # opens redis-cli against the cache service
   wip shell
   ```

5. `wip logs web` (or any service name) follows that service's logs under `mode: compose-native`;
   `wip down` stops and removes the whole stack.

See the [Configuration Reference](https://github.com/slidict/wip/wiki/Configuration-Reference)
and the compose.yml service-key table in the
[main README](../../README.md#architecture) for which Compose keys are read.
