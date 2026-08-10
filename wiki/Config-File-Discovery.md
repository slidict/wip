# Config File Discovery

How wip finds `wip.yml`, what `version:` means, and how the `wslc:` block resolves the binary.

## Finding wip.yml

With no `--config`, wip starts in the current directory and walks **up** to the filesystem root,
taking the first `wip.yml` it finds. That's what makes this work:

```bash
cd my-project/app/models
wip rspec            # still uses my-project/wip.yml
```

If nothing is found:

```
wip.yml was not found (searched from /path/to/cwd to the filesystem root)
```

### Pointing at one explicitly

```bash
wip --config /path/to/wip.yml up
wip up --config /path/to/wip.yml
```

Both spellings work — wip pulls leading global switches off the front of `ARGV` and reinserts them
after the command name. See [Global Options](Global-Options).

The path is expanded against the current directory. `wip init --config PATH` writes there too.

### Paths are relative to wip.yml, not the cwd

Everything wip resolves from config is anchored to the directory holding `wip.yml`:

- `compose.file`
- `sync.source`
- a build command's `context`
- the `.env` file (unless `--env-file` overrides it)
- compose-native's default network name (the directory's basename)

This is what keeps behavior identical from any subdirectory.

## `version:`

```yaml
version: 1
```

Only `1` is supported. Omitting it is the same as `1`. Any other value:

```
Unsupported configuration version: 2
```

## YAML parsing rules

`wip.yml` is parsed with a safe loader: **no** custom Ruby classes and **no** YAML aliases. An
anchor/alias in `wip.yml` (`<<: *defaults`) is a parse error:

```
Could not parse /path/wip.yml: Unknown alias: defaults
```

`compose.yml`, by contrast, *is* parsed with aliases enabled, because merge keys are common and
long-standing in real compose files. See [Compose File Support](Compose-File-Support).

Mapping keys are stringified throughout, so `env: {PORT: 3000}` and `env: {"PORT": "3000"}` are
equivalent — and every `env` value is converted to a string.

## The `wslc:` block

```yaml
wslc:
  command: auto
```

| Value | Behavior |
|---|---|
| `auto` (default) | try `wslc.exe`, then `wslc`, then `/mnt/c/Windows/System32/wslc.exe` |
| any other name | must be executable on `PATH` |
| an absolute path | must be executable at that path |

A name containing a path separator is checked directly with `File.executable?`; a bare name is
looked up across `PATH`. Failure raises `CommandNotFoundError` listing everything it checked:

```
WSLC was not found.

Checked:
  wslc.exe
  wslc
  /mnt/c/Windows/System32/wslc.exe

Install or update the WSL container tooling, then run:

  wip doctor
```

See [WSLC Not Found](WSLC-Not-Found).

Note that `wip version` swallows this error — it still prints wip's own version when `wslc` is
missing. Every other command fails.

## Related

- [Global Options](Global-Options) — `--config`, `--env-file`
- [wip config](wip-config) — print the effective, resolved configuration
- [wip init](wip-init) — generate a starter file
