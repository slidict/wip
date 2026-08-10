# wip doctor

Diagnoses the environment and the configuration, one line per check.

```
wip doctor
```

```console
$ wip doctor
[OK] Running on WSL2
[OK] Windows executable interoperability is enabled
[OK] Architecture: linux/amd64
[OK] Loaded wip.yml
[OK] Found wslc.exe
[OK] WSLC is available
[OK] Sync source /home/me/app mirrors into volume app-src at /app
[OK] Git is available
```

## Levels and exit code

| Level | Meaning |
|---|---|
| `[OK]` | fine |
| `[WARN]` | worth knowing, doesn't block |
| `[FAIL]` | blocks execution |

**Exit `1` if any check failed; otherwise `0`** — warnings alone still exit `0`, so
`wip doctor && wip up -d` is safe in a script.

## The checks, in order

### 1. WSL2

```
[OK]   Running on WSL2          / [OK]   WSL2 is available      (on native Windows)
[FAIL] Not running on WSL2      / [FAIL] WSL2 is not available
```

On Linux/WSL, read from `/proc/version`. On native Windows, wip asks Windows itself via
`wsl.exe --status`, since there's no `/proc/version` to read.

### 2. Windows interop

Skipped entirely on native Windows.

```
[OK]   Windows executable interoperability is enabled
[FAIL] Windows executable interoperability is disabled
```

Detected via `/proc/sys/fs/binfmt_misc/WSLInterop` or the `WSL_INTEROP` environment variable.
Without it, WSL can't run `wslc.exe` at all. Usually caused by `interop.enabled=false` in
`/etc/wsl.conf`.

### 3. Architecture

```
[OK] Architecture: linux/amd64
```

Informational, **always `[OK]`**. Reported as `linux/amd64` / `linux/arm64` (or `linux/<host_cpu>`
for anything else). Compare it against your image when a container refuses to start — see
[Architecture Mismatch](Architecture-Mismatch).

### 4. Configuration

```
[OK]   Loaded wip.yml
[FAIL] wip.yml was not found (searched from … to the filesystem root)
[FAIL] container: must be set when dependencies: has entries
[FAIL] No dependencies.app entry
[FAIL] compose.service 'app' has no matching service in compose.yml
```

Any `ConfigError` surfaces here as a `[FAIL]` with the exact message, and the remaining
config-dependent checks are skipped. Catalog: [Configuration Errors](Configuration-Errors).

### 5. WSLC binary

```
[OK]   Found wslc.exe
[OK]   WSLC is available
[FAIL] WSLC was not found. Checked: …
[FAIL] WSLC version failed
```

First resolves `wslc.command`, then actually runs `<binary> version`. "Found" but "version failed"
means the file exists and is executable but doesn't respond — usually a broken or partial install.
See [WSLC Not Found](WSLC-Not-Found).

### 6. Compose (`mode: compose` only)

```
[OK]   Found wslc-compose
[OK]   compose command is available
[OK]   Found compose file /home/me/app/compose.yml
[FAIL] compose command was not found. Checked: …
[FAIL] Compose file not found: /home/me/app/compose.yml
```

Resolves and version-checks `compose.command`, then confirms the compose file exists. wip does
**not** parse the file in this mode — that's the external tool's job.

### 7. Compose (`mode: compose-native` only)

```
[OK]   Found compose file /home/me/app/compose.yml
[OK]   Parsed compose file
[FAIL] compose.yml: services.app has unsupported key(s): healthcheck
```

Here wip does parse it, so every rule on [Compose File Support](Compose-File-Support) is checked.
Parsing is skipped if the file wasn't found.

### 8. Sync (only when `sync:` is configured)

```
[OK]   Sync source /home/me/app mirrors into volume app-src at /app
[FAIL] Sync source not found: /home/me/app/src
[WARN] sync.image/sync.build only cover `wip up`'s one-time pre-boot mirror …
```

The warning fires when `sync.mode: exec` is combined with `sync.image` / `sync.build` — a common
misunderstanding worth its own explanation on [Sync Modes](Sync-Modes).

### 9. Git

```
[OK]   Git is available
[WARN] Git is not available to the WSLC build environment
```

**A warning, never a failure.** Many images fetch dependencies from Git during a build (Bundler
`git:` gems, Go modules, npm `git+https:` deps), and those builds fail confusingly without it — but
plenty of projects never touch Git in a build, so it can't be fatal.

## Reading a failure

`wip doctor` reports; it doesn't fix. Each `[FAIL]` maps to a page:

| Failure | Page |
|---|---|
| WSL2 / interop | [Troubleshooting & FAQ](Troubleshooting-and-FAQ) |
| WSLC not found | [WSLC Not Found](WSLC-Not-Found) |
| Config errors | [Configuration Errors](Configuration-Errors) |
| compose parse errors | [Compose File Support](Compose-File-Support) |
| Sync source missing | [Source Sync](Source-Sync) |

## Related

- [wip config](wip-config) — what wip resolved, once doctor says the config loads
- [Reporting Issues](Reporting-Issues) — `wip doctor` output belongs in every bug report
