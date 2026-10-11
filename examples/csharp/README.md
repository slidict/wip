# C# example (`mode: container`)

An ASP.NET Core app with a Postgres sidecar, driven directly by `wip` with no `compose.yml`
involved — see [Which mode should you use?](../../README.md#which-mode-should-you-use) for when
this mode fits.

## Files

- [`wip.yml`](wip.yml) — the primary container (`app`), its `db` dependency, and the `dotnet`
  commands you'd actually run day to day
- [`Dockerfile`](Dockerfile) — a minimal placeholder image so `wip build` has something to run;
  replace it with your app's real Dockerfile

> **The `postgres` / `password` credential here is for local development only.** `db` publishes
> `127.0.0.1:5432` so it stays on the loopback interface rather than every host interface. Swap in
> a real secret before running this anywhere shared or reachable.

## Setup

1. Copy `wip.yml` (and `Dockerfile`, if you don't have one yet) into the root of your solution.
2. Point `dependencies.app.image` / `commands.build.tag` at your own image name, and adjust
   `ConnectionStrings__Default` / `POSTGRES_DB` if your app uses a different database name or
   configuration key.
3. Run the checks:

   ```powershell
   wip doctor
   wip build
   wip up -d
   wip test
   ```

   `wip up -d` waits for Postgres's `healthcheck:` to pass before it's considered ready — no
   manual "wait for the database" step needed.

4. Everyday commands:

   ```powershell
   wip restore
   wip test
   wip migrate
   wip ef migrations add AddWidgets
   wip psql          # opens psql against the db dependency, not app
   wip shell
   ```

5. `wip down` stops and removes the stack; `wip logs -f` follows the app container's logs.

## Notes for .NET projects

- `DOTNET_USE_POLLING_FILE_WATCHER` is set because `dotnet watch` gets no file-change
  notifications for a bind-mounted host tree. Without it, saving a file on Windows doesn't
  trigger a rebuild.
- The `nuget` named volume keeps restored packages out of the bind mount, so `wip down` doesn't
  cost you a full `dotnet restore`.
- The `api-bin` and `api-obj` volumes are there because `.:/app` hides the image's own `bin/` and
  `obj/`: without them, output built on the host and output built in the container land in the
  same paths and overwrite each other. If you'd rather not bind-mount the source at all,
  `wip init --template csharp` writes a starter `wip.yml` whose `sync.exclude` already skips those
  directories and mirrors the tree into a volume instead.

See the [Configuration Reference](https://github.com/slidict/wip/wiki/Configuration-Reference)
for every key `wip.yml` accepts.
