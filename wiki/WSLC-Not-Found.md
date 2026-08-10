# WSLC Not Found

```
WSLC was not found.

Checked:
  wslc.exe
  wslc
  /mnt/c/Windows/System32/wslc.exe

Install or update the WSL container tooling, then run:

  wip doctor
```

Every wip command except [`wip version`](wip-version) fails with this when the `wslc` binary can't
be resolved.

## The lookup order

With `wslc.command: auto` (the default), wip tries, in order:

1. `wslc.exe` — searched across `PATH`
2. `wslc` — searched across `PATH`
3. `/mnt/c/Windows/System32/wslc.exe` — checked directly

The first one that is **executable** wins. A name containing a path separator is checked with
`File.executable?` directly; a bare name is looked up in every `PATH` entry.

With any other value, that exact value must be executable — there is no fallback:

```yaml
wslc:
  command: /mnt/c/Program Files/WSL/wslc.exe
```

```
WSLC was not found.

Checked:
  /mnt/c/Program Files/WSL/wslc.exe
```

## Diagnosing

**Is it there at all?**

```bash
which wslc.exe wslc
ls -l /mnt/c/Windows/System32/wslc.exe
wslc.exe version
```

**Is Windows interop enabled?** Without it, WSL can't execute `.exe` files at all — which looks
identical to "not installed":

```bash
wip doctor
```

```
[FAIL] Windows executable interoperability is disabled
```

Fix in `/etc/wsl.conf`, then `wsl --shutdown` from Windows:

```ini
[interop]
enabled = true
appendWindowsPath = true
```

**Is System32 on your PATH?** `appendWindowsPath = false` in `/etc/wsl.conf` removes Windows paths
from `PATH`, so `wslc.exe` won't be found by name. That's exactly what the third candidate covers —
but only for the default install location.

## Fixes

### Install or update the tooling

Install Microsoft's WSL container tooling on the Windows side, then confirm from Windows itself
before returning to WSL:

```powershell
wslc version
```

### Point at it explicitly

If it lives somewhere non-standard:

```yaml
wslc:
  command: /mnt/c/Tools/wslc/wslc.exe
```

Verify:

```bash
ls -l /mnt/c/Tools/wslc/wslc.exe    # must be executable
wip doctor
```

### On native Windows

wip runs there too. `wslc.exe` needs to be on `PATH`, or named absolutely with a Windows path:

```yaml
wslc:
  command: C:\Program Files\WSL\wslc.exe
```

## "Found" but "version failed"

```
[OK]   Found wslc.exe
[FAIL] WSLC version failed
```

Different problem: the binary exists and is executable but doesn't respond to `version`. Usually a
partial or broken install, or a stale shim on `PATH` shadowing the real binary. Check what you're
actually resolving:

```bash
which -a wslc.exe wslc
```

## Verify the fix

```console
$ wip doctor
[OK] Found wslc.exe
[OK] WSLC is available
```

## Related

- [Config File Discovery](Config-File-Discovery) — the `wslc:` block
- [wip doctor](wip-doctor)
- [wip version](wip-version)
