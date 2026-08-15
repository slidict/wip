# wip C# / Native AOT Migration Plan

Target pipeline:

```text
C# → Native AOT → wip.exe → ZIP → GitHub Releases → WinGet
```

This document is the plan the port followed. Phases 1 through 5 have landed; what remains
open is recorded here rather than edited out, because the reasoning is what makes the
remaining decisions reviewable.

**Still open:** the path model in §3 — Phase 0 Spikes 1 and 2 need Windows with WSL2 and wslc
present, so `Platform/WslPath.ForWslc` currently implements branch (a) of §3.2 as a
provisional answer, isolated to that one function. §11 lists everything else awaiting a
first-hand check.

---

## 1. Settled Premises

| # | Decision | Detail |
|---|---|---|
| 1 | **Target** | **Windows native (win-x64) only.** No Linux binary. From a WSL2 shell, `wip.exe` is invoked over interop |
| 2 | **Ruby implementation** | Copied out to its own repository, then deleted from this one |
| 3 | **Compatibility** | **Breaking changes are acceptable.** Backward compatibility with existing `wip.yml` files is not a requirement |
| 4 | **Repository** | The C# implementation takes over `slidict/wip`, keeping the wiki, issues, and release history |

These four settle what was the biggest open question in the earlier draft — whether to keep running inside WSL — and reduce the work to a straightforward single-RID, single-OS port.

### Execution model

```text
┌─ Windows ─────────────────────────────────────────┐
│  winget install Slidict.Wip                       │
│         ↓                                         │
│      wip.exe ──invokes──> wslc.exe                │
│         ↑                                         │
│         │ WSL interop (the Windows PATH is folded │
│         │ into the Linux PATH, so bash can call   │
│         │ it directly)                            │
│  ┌──────┴──── WSL2 (Ubuntu, etc.) ──────────────┐  │
│  │  $ cd ~/myproject && wip.exe up -d          │  │
│  └─────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────┘
```

**The wip.exe process always runs on the Windows side.** WSL2 is where the command is typed, not where it executes. That single fact is what creates the problem in §3.

### Inventory of what exists today

| Item | Current state |
|---|---|
| Implementation | Ruby 3.2+ / Thor 1.3 |
| Size | 23 files, ~3,000 lines across `lib/` and `exe/` |
| Tests | 17 RSpec files, ~3,400 lines |
| Distribution | RubyGems (`wslc-wip`) + GitHub Packages |
| Dependencies | Thor, and nothing else |

Thor is the only dependency, and YAML/JSON are handled as plain hashes rather than mapped onto types — `Config#stringify` normalizes every key to a string, and everything downstream is dictionary access like `@raw['dependencies']`. The biggest AOT hazard, a reflection-driven serializer, was never in the picture. AOT suitability is high.

---

## 2. Work That Disappears With Windows-Only

Compared to the earlier four-RID proposal, all of the following drops out. **Total port effort falls by roughly 20–30%.**

| Removed | Why |
|---|---|
| **openpty / forkpty P/Invoke** | The Linux `CommandRunner#run_attached` path is gone. See §4.2 |
| **flock(2) P/Invoke** | `FileStream.Lock` works on Windows, so `BuildContext` locking is pure BCL |
| **`UnixFileMode` permission preservation** | No executable bit to carry through — the intent behind `copy_entry(preserve)` vanishes |
| **`/proc/version` parsing** | `Environment#wsl2?` reduces to a single `wsl.exe --status` call |
| **`windows_interop?` check** | Self-evident when running on Windows. Drop it from `Doctor` |
| **`/mnt/c/...` path special-casing** | Remove `/mnt/c/Windows/System32/wslc.exe` from `CommandResolver::CANDIDATES` |
| **linux-x64 / arm64 RIDs** | The CI matrix shrinks to one or two jobs |
| **tar.gz artifacts and an install script** | ZIP and WinGet only |
| **`Gem.win_platform?` branches (5 sites)** | The branches themselves disappear |

---

## 3. 🔴 Core Risk: The Path Model

**This is the one area where the plan cannot yet fix an implementation approach.** Everything else is a mechanical port.

### 3.1 The problem

wip hands host absolute paths to wslc in three places:

| Site | What it produces | Code |
|---|---|---|
| `sync:` source mount | `-v <host abs path>:/host-src:ro` | `SyncSettings#volume_specs` |
| `volumes:` bind mounts | `-v <host abs path>:/app` | `CommandBuilder#volume_specs` |
| Build context | the cwd for `wslc build` | `CLI#run_staged_build` |

When a Windows executable is launched from a WSL2 shell, that process's working directory becomes a **UNC path** — `\\wsl.localhost\<distro>\home\user\proj` (older form: `\\wsl$\...`).

So running `wip.exe up` from `~/myproject` yields:

```text
sync.source   = \\wsl.localhost\Ubuntu\home\user\myproject
generated -v  = \\wsl.localhost\Ubuntu\home\user\myproject:/host-src:ro
```

**Whether wslc accepts that `-v` is unknown**, and it seems unlikely. The current Ruby implementation ran inside WSL, so `source` was a plain `/home/user/myproject` and the question never arose.

Worth noting: the existing code already carries a workaround comment about `wslc build` crashing (`ERROR_UNHANDLED_EXCEPTION`) when handed an absolute context path, worked around by chdir-ing in and passing `"."`. wslc's path handling was never straightforward to begin with.

### 3.2 Decision tree

**The top-priority Phase 0 spike is to measure what wslc.exe actually accepts.** Three branches follow from the result:

| Measured result | Approach |
|---|---|
| **(a) wslc accepts Linux paths** | wip.exe translates UNC → Linux (`\\wsl.localhost\Ubuntu\home\u\p` → `/home/u/p`). **Cleanest option** — self-contained inside wip.exe, no extra process launches |
| **(b) wslc accepts only Windows-local paths** | The build context is solved by the unconditional staging in §3.3, but WSL-side bind mounts for `volumes:` / `sync.source` **cannot work** → projects must live on the Windows filesystem (`C:\...`). **A constraint that has to be documented** |
| **(c) wslc handles UNC directly** | No translation needed, but I/O performance over 9p needs separate measurement before calling it usable |

**Current expectation is (a) or (b).** Since wslc runs containers inside a WSL2 VM it presumably has some Linux path representation, which favors (a) — but **no implementation decision gets made without confirming this.**

### 3.3 Stage the build context locally, always

Of the three path sites, **the build context is the one that can be settled now.**

`shadow_context` currently exists as an opt-in: mirror a WSL-side source tree to the Windows side to make it fast. Under the new model, the same mechanism stops being optional — a UNC source read directly by wslc is exactly the case it was built to avoid.

So:

- `BuildContext` **always** stages to a Windows-local directory (`%LOCALAPPDATA%\wip\contexts\<sha256>`)
- Retire the `shadow_context` config key; either drop it entirely or replace it with an optional `context_cache` that only chooses the cache location
- Keep the existing incremental manifest machinery (copy only changed files) as-is
- `wslc build` always receives a local cwd plus `"."`, which satisfies the §3.1 crash workaround naturally

**Performance note:** walking files over UNC goes through the 9p protocol and is slow. Computing manifest fingerprints with a per-file `stat` would be crippling on a large tree. `Directory.EnumerateFileSystemEntries` surfaces the `FindFirstFile` data (size, mtime, attributes) as part of enumeration, so **take attributes from the enumeration result rather than statting each entry.** Measure this in Phase 4.

### 3.4 mtime precision changes

Ruby uses `stat.mtime.nsec` (nanoseconds); .NET uses `File.GetLastWriteTimeUtc().Ticks` (100ns). The manifest format changes as a result, so **give the manifest a schema version and do a single full rebuild on mismatch** to converge.

### 3.5 `wip` vs `wip.exe` (UX)

A WinGet portable install drops a `wip.exe` shim into the links directory and puts it on PATH.

- **From PowerShell / cmd:** `wip` works (PATHEXT handles it)
- **From WSL2 bash:** **the `.exe` has to be typed** — bash knows nothing about PATHEXT

Typing it without the extension requires the user to add something like `alias wip=wip.exe`. Options for absorbing that:

- Ship a `wip.exe install-wsl-shim` subcommand that writes a two-line `/usr/local/bin/wip` script inside WSL (`exec "$(which wip.exe)" "$@"`)
- Or just document the alias in the README

**Decide in Phase 3.** It's a small feature, but WSL2 is the primary place this gets typed, so the difference in feel is not small.

---

## 4. Remaining Technical Work

Outside the path model, only two items need real judgment.

### 4.1 YAML parsing (low risk)

Because the current implementation treats YAML as plain hashes, **parsing into the representation model (node tree) avoids reflection entirely**:

- Use YamlDotNet's `YamlStream` / `YamlMappingNode` and never go through a deserializer
- The Ruby code's shape transfers almost verbatim, which also reduces the chance of introducing bugs during the port

`ConfigLoader` already forbids anchors and aliases via `YAML.safe_load_file(permitted_classes: [], aliases: false)`. **Keep that restriction in C#** — it makes the implementation simpler.

JSON (reading `wslc list --format json`) is handled by `System.Text.Json`'s `JsonDocument`, which is reflection-free and works under AOT as-is. No POCO deserialization.

### 4.2 Terminal handling for interactive commands

`CommandRunner` currently has three paths (piped / Linux openpty / Windows stdio inheritance). **Windows-only removes the openpty path, leaving two.**

What remains to decide is how interactive commands (`wip shell`, `wip exec -it`, `wip run rails console`) behave:

| Option | Detail | Assessment |
|---|---|---|
| **A (recommended)** | **Inherit stdio** (`RedirectStandard* = false`), same as the current Windows path | Job control, Ctrl-C, and isatty detection all work correctly. **Cost: no `ErrorInterpreter` hints on interactive commands** — but Windows doesn't produce them today either, so this is not a regression |
| B (later) | P/Invoke ConPTY (`CreatePseudoConsole`) | Gets both output capture and interactivity, at a significantly higher implementation cost |

**Recommendation: A.** The non-interactive paths (`probe`, `resource_exists?`, ordinary `execute`) keep capturing output, so hints from `wip up` and `wip doctor` continue to work as before.

**Needs a spike:** whether a Windows process launched from WSL2 bash gets a console good enough for interaction. It should arrive via Windows Terminal's ConPTY, but whether `wslc exec -it` correctly detects a TTY has to be measured (Phase 0).

### 4.3 Smaller details

| Ruby | C# | Notes |
|---|---|---|
| `Shellwords.split` | **Hand-written (~50 lines)** | No BCL equivalent. Needed in 4 places to split `command:` strings into argv. Cross-checked via golden tests |
| `File.rename` (atomic replace) | `File.Move(src, dst, overwrite: true)` | |
| `Find.prune` tree pruning | Manual recursion over `EnumerateFileSystemEntries` | `RecurseSubdirectories` gives no way to prune |
| `Data.define` | `record` | |
| `Open3.popen3` | `Process` + `ArgumentList` | **Preserves wip's design-critical property: arguments passed as an array, never through a shell** |
| `Signal.trap('WINCH')` | Not needed | The PTY path is gone |
| Regexes (`ErrorInterpreter` etc.) | `[GeneratedRegex]` | Source-generated; excellent AOT fit |

---

## 5. Decisions Still Open

### Decision A: CLI framework

| Candidate | AOT fit | Notes |
|---|---|---|
| **System.CommandLine 2.0 (recommended)** | ◎ AOT support is an advertised goal | Covers the Thor equivalents: subcommands, global options, generated help |
| Spectre.Console.Cli | △ | Resolves command types via reflection in places, needing extra AOT work |
| Hand-rolled parser | ◎ | Zero dependencies and smallest binary, but every help string and error message is yours to write |

**Recommendation: System.CommandLine 2.0**, confirming the current version and AOT status at kickoff (§11).

The two Thor workarounds in `cli.rb` should be redesigned rather than ported:

1. `reorder_global_options` (rewriting `wip --config foo up` into `wip up --config foo`) — System.CommandLine's global options are position-independent to begin with, so **this may well be deleted outright**
2. The `dispatch` fallback (resolving an unknown command name against `commands:` in `wip.yml`) — needs a custom unmatched-input handler. **Needs a spike**

### Decision B: Ship arm64?

WSL2 runs on Windows on ARM, and a WinGet manifest can list x64 and arm64 side by side.

**Recommendation: win-x64 only for the first release.** Keep the naming scheme (§9.3) shaped so arm64 can be added later as one extra job. Availability of arm64 Windows runners needs checking.

### Decision C: Code signing

An unsigned executable can draw SmartScreen's "Windows protected your PC" prompt. Packaging as a ZIP does **not** mitigate that: SmartScreen judges the downloaded file and its publisher reputation, not the container it arrived in, and enterprise policy can stop a user dismissing the warning at all. Shipping unsigned is therefore an explicit risk acceptance, not something the packaging choice avoids.

**Recommendation: accept that risk for now; revisit Azure Trusted Signing if it causes real friction.**

If signing is adopted, **it has to happen before packaging**: sign `wip.exe`, then zip, then hash, then attest, and publish that hash to WinGet. Signing after any of those steps changes the bytes the checksum and the attestation describe.

### Decision D: Version number

The implementation language, the minimum requirements, and `wip.yml` all break at once, so **start at v2.0.0**.

Relatedly, in the Ruby repository the gemspec's `source_code_uri` and `bug_tracker_uri` need repointing — they currently reference `slidict/wip`.

---

## 6. Repository Layout

```text
wip/
├── Directory.Build.props        # net10.0 / LangVersion / AOT settings / Version
├── Directory.Packages.props     # Central Package Management
├── wip.slnx
├── src/
│   ├── Wip.Core/                # Logic
│   │   ├── Configuration/       # Config, ConfigLoader, DotenvLoader, SyncSettings
│   │   ├── Compose/             # ComposeFile, ComposeBridge, VariableInterpolation
│   │   ├── Build/               # BuildContext, DockerIgnore, StagingProgress
│   │   ├── Execution/           # CommandBuilder, CommandRunner, CommandResolver, CommandDisplay
│   │   ├── Diagnostics/         # Doctor, ErrorInterpreter, DebugReporter, ResourceMonitor
│   │   └── Platform/            # WindowsEnvironment, WslPath, Shellwords
│   └── Wip.Cli/                 # PublishAot=true. Entry point and command definitions only
├── tests/
│   ├── Wip.Tests/               # xUnit (runs on ordinary CoreCLR)
│   └── golden/                  # Migration parity fixtures (§8)
└── packaging/winget/            # WinGet manifest templates
```

Splitting `Wip.Core` from `Wip.Cli` is for testability: AOT publishing applies only to `Wip.Cli`, while `Wip.Core` is referenced normally from xUnit. Still, set `<IsAotCompatible>true</IsAotCompatible>` on `Wip.Core` too, so reflection dependencies surface at compile time.

**`Platform/WslPath` is the one genuinely new component** — the UNC ↔ Linux path translation from §3.

### Shared build settings (`Directory.Build.props`, key entries)

```xml
<TargetFramework>net10.0</TargetFramework>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<InvariantGlobalization>true</InvariantGlobalization>   <!-- drops ICU, shrinks the binary -->
<UseSystemResourceKeys>true</UseSystemResourceKeys>
<PublishAot>true</PublishAot>                            <!-- Wip.Cli only -->
<StripSymbols>true</StripSymbols>
<IlcOptimizationPreference>Size</IlcOptimizationPreference>
```

Whether `InvariantGlobalization=true` is appropriate needs confirming (§11). Case-insensitive comparisons like `SECRET_PATTERN` are fine as long as `StringComparison.OrdinalIgnoreCase` is explicit.

### Size and startup targets

| Metric | Target |
|---|---|
| `wip.exe` size | < 10 MB uncompressed / < 5 MB zipped |
| `wip version` wall time | < 30 ms (Ruby today: 200–400 ms) |
| ZIP contents | `wip.exe` alone |

---

## 7. Module-by-Module Port Map

| Ruby | Lines | C# destination | Difficulty | Notes |
|---|---:|---|:---:|---|
| `version.rb` | 5 | `<Version>` in `Directory.Build.props` | Easy | Single source of truth moves here |
| `errors.rb` | 7 | `WipException` hierarchy | Easy | |
| `command_display.rb` | 19 | `CommandDisplay` | Easy | |
| `dotenv_loader.rb` | 39 | `DotenvLoader` | Easy | Regexes port directly |
| `environment.rb` | 43 | `Platform/WindowsEnvironment` | **Easy** (↓) | `/proc/version` and the interop check are gone; only `wsl.exe --status` remains |
| `command_resolver.rb` | 48 | `CommandResolver` | **Easy** (↓) | PATHEXT only; no Unix executable-bit check |
| `variable_interpolation.rb` | 60 | `VariableInterpolation` | Easy | |
| `staging_progress.rb` | 63 | `StagingProgress` | Easy | |
| `debug_reporter.rb` | 66 | `DebugReporter` | Easy | |
| `compose_bridge.rb` | 72 | `ComposeBridge` | Easy | |
| `docker_ignore.rb` | 75 | `DockerIgnore` | Medium | Hand-written glob matching; behavioral parity matters |
| `error_interpreter.rb` | 90 | `ErrorInterpreter` | Easy | Convert to `[GeneratedRegex]` |
| `resource_monitor.rb` | 94 | `ResourceMonitor` | Medium | |
| `sync_settings.rb` | 157 | `SyncSettings` | **Medium→Hard** (↑) | How `source` is represented depends on the §3 outcome |
| `doctor.rb` | 158 | `Doctor` | **Easy** (↓) | Interop check removed; WSL2 detection simplifies |
| `initializer.rb` | 220 | `Initializer` | Medium | Templates become raw string literals (`"""`) |
| `build_context.rb` | 219 | `BuildContext` | **Medium** (↓) | No flock/UnixFileMode P/Invoke; instead always stages locally (§3.3) |
| `command_builder.rb` | 234 | `CommandBuilder` | Medium | Needs `Shellwords.split`; `volume_specs` depends on the §3 outcome |
| `config.rb` | 273 | `Config` | Medium | Drop or replace the `shadow_context` validation |
| `compose_file.rb` | 272 | `ComposeFile` | Medium | Same |
| `command_runner.rb` | 205 | `CommandRunner` | **Medium** (↓) | Three paths become two; openpty, raw mode, and SIGWINCH all disappear |
| `cli.rb` | 541 | `Wip.Cli` | Medium | Reworked onto System.CommandLine |
| — | — | `Platform/WslPath` | **Hard** (new) | §3. UNC ↔ Linux path translation |
| — | — | `Platform/Shellwords` | Medium (new) | POSIX shell-compatible splitting |

(↑↓ mark changes in difficulty relative to the earlier four-RID plan.)

---

## 8. Parity Safety Net (Golden Tests)

**Extract the fixtures before the Ruby implementation leaves this repository.** This is the safety net for the whole migration.

Put input/output pairs under `tests/golden/`:

```text
tests/golden/
  001-container-basic/
    wip.yml
    .env
    cases.json        # [{ "argv": ["up","-d"], "expect": ["wslc","run","--name","app", ...] }, ...]
  002-compose-native/
    wip.yml
    compose.yml
    cases.json
  003-sync-exec/
  ...
```

Cover the layers where input → output is a pure function:

- The argv arrays produced by `CommandBuilder` ← **most important. If these match, runtime behavior matches**
- `Config#to_h` normalization, `DotenvLoader`, `DockerIgnore#ignored?`, `Shellwords.split`, `ComposeFile#to_dependencies_hash`

**Since breaking changes are allowed, some fixtures will change on purpose** — specifically the path-related ones from §3 (`sync.source`, `volumes:`, `shadow_context`). So the role of these tests is not to enforce an exact match but to **catch unintended changes**:

- Path-related cases get their `expect` values **deliberately rewritten** to the new model, so the change shows up in review
- The other ~80% **should stay green untouched**

`CommandRunner`'s terminal behavior and `BuildContext`'s real file operations can't be captured this way; they're covered by the manual test matrix in Phase 4.

---

## 9. Distribution Pipeline

### 9.1 Overview

```text
git tag v2.0.0
      │
      ▼
┌─ release.yml (windows-latest) ───────────────┐
│  dotnet publish -r win-x64                   │
│  → wip.exe → ZIP → SHA256                    │
│  → actions/attest-build-provenance           │
└──────────────────┬───────────────────────────┘
                   ▼
        GitHub Release (publish the release-drafter draft)
                   │  on: release published
                   ▼
        winget.yml → opens a PR against microsoft/winget-pkgs
```

Native AOT cannot cross-compile across operating systems, so **a Windows runner is mandatory**. Being Windows-only, though, **the matrix is a single job** (two, if arm64 ships).

### 9.2 Changing the release trigger

Today the chain is: `Changelog` workflow succeeds → `workflow_run` → `gem-push`, which is hard to follow. **Simplify to tag-driven:**

- `git tag v2.0.0 && git push --tags` triggers `release.yml`
- The single source of version truth is `<Version>` in `Directory.Build.props`; CI verifies it against the tag and fails on mismatch
- Rework `bump-version.yml` to edit `Directory.Build.props` instead of `version.rb`
- Keep release-drafter: it maintains a draft on every merge, so what is unreleased stays
  visible, and the tag-driven workflow publishes that draft rather than writing its own notes
- Delete `gem-push.yml`

### 9.3 Artifact naming

```text
wip-2.0.0-win-x64.zip
SHA256SUMS
```

WinGet manifests embed the version in the URL, so **this naming convention must not change once set** (it's already shaped to accept arm64 later).

### 9.4 WinGet manifest

Use `InstallerType: zip` with `NestedInstallerType: portable` — the mechanism built for exactly this shape, a ZIP containing a single executable (manifest schema 1.6+).

```yaml
# Slidict.Wip.installer.yaml (skeleton)
PackageIdentifier: Slidict.Wip
PackageVersion: 2.0.0
Installers:
  # Kept on the installer entry rather than at the root: that is where the schema always
  # accepts them, and it is the shape adding an arm64 sibling needs anyway.
  - Architecture: x64
    InstallerType: zip
    NestedInstallerType: portable
    NestedInstallerFiles:
      - RelativeFilePath: wip.exe
        PortableCommandAlias: wip
    InstallerUrl: https://github.com/slidict/wip/releases/download/v2.0.0/wip-2.0.0-win-x64.zip
    InstallerSha256: <sha256>
ManifestType: installer
ManifestVersion: 1.6.0
```

Three files are required: version, installer, and locale manifests.

**Prerequisites:**

1. **Confirm the PackageIdentifier** — `Slidict.Wip` is assumed; the publisher segment has to correspond to a real publisher name
2. **A fork of microsoft/winget-pkgs** — the target for automated PRs
3. **A classic PAT with `public_repo` scope**, stored as a repository secret. `GITHUB_TOKEN` cannot open PRs against another repository
4. **An automation action** — `vedantmgoyal9/winget-releaser` (supports zip/portable) or calling `wingetcreate update` from CI

**Things to watch:**

- The first submission goes through human review on the winget-pkgs side and can take several days. **Leave slack in Phase 5**
- Validation needs a published (non-draft, non-prerelease) release, so the WinGet job runs `on: release: types: [published]`
- A portable package gets a winget-created shim on PATH. **From WSL bash, users still type `wip.exe`** (§3.5)

---

## 10. Phases

### Phase 0 — Spikes and pipeline validation

**Approach: before writing a single line of logic, (1) settle the path model and (2) get the distribution path working end to end.** Both come before implementation.

**🔴 Spike 1: path model (highest priority — other decisions depend on it)**

- [ ] Measure the working directory a Windows exe actually receives when launched from WSL2 bash (is it UNC, and in which form?)
- [ ] Pass (a) Linux paths, (b) Windows-local paths, and (c) UNC paths to `wslc.exe run -v` and **measure which are accepted**
- [ ] Check `wslc.exe build` behavior with a UNC cwd
- [ ] → Settle the approach via the §3.2 decision tree. **If (b), lock in the "projects live on the Windows filesystem" constraint and document it in the README and wiki**

**🔴 Spike 2: interactive terminal**

- [ ] Does `wslc exec -it` correctly detect a TTY in a Windows process launched from WSL2 bash?
- [ ] Do Ctrl-C and window resizing behave as expected?

**Spike 3: AOT feasibility**

- [ ] Stand up the solution skeleton on the .NET 10 SDK
- [ ] Confirm AOT publishing succeeds with YamlDotNet (representation model) and System.CommandLine present, with zero reflection warnings
- [ ] Publish a stub that only answers `wip version` and **measure size and startup time**
- [ ] Confirm "unknown command → `commands:` fallback" is expressible in System.CommandLine (§5, Decision A-2)

**Pipeline validation**

- [ ] Get build → ZIP → prerelease publish working in CI on a Windows runner
- [ ] Generate a WinGet manifest by hand and validate locally with `winget validate` / `winget install --manifest` (**do not open a PR**)

**Exit criteria: the stub wip.exe installs through a local WinGet manifest, and `wip.exe version` runs from WSL2 bash.**

### Phase 1 — Retire Ruby, keep the safety net

- [ ] Extract `tests/golden/` fixtures from the existing RSpec suite and **confirm Ruby passes them**
- [ ] Copy the Ruby implementation to its own repository (repoint the gemspec metadata URLs)
- [ ] Delete `lib/`, `exe/`, `spec/`, `Gemfile`, `Rakefile`, `*.gemspec`, and `.rubocop.yml` from this repository
- [ ] Delete `gem-push.yml`; replace `test.yml` with a dotnet equivalent

### Phase 2 — Pure logic layer

- [ ] `Shellwords`, `DotenvLoader`, `DockerIgnore`, `VariableInterpolation`, `ErrorInterpreter`
- [ ] `WslPath` (implementing the Spike 1 conclusion)
- [ ] `Config`, `ConfigLoader`, `SyncSettings`, `ComposeFile`, `ComposeBridge`
- [ ] `CommandBuilder`
- [ ] **Exit criteria: golden tests fully green, apart from the deliberate path-related diffs**

### Phase 3 — Execution/IO layer and CLI

- [ ] `WindowsEnvironment`, `CommandResolver`
- [ ] `CommandRunner` (capture path + stdio inheritance path)
- [ ] `BuildContext` (always staging locally), `StagingProgress`
- [ ] `Doctor`, `DebugReporter`, `ResourceMonitor`, `Initializer`
- [ ] Define every command in System.CommandLine (`version`, `init`, `doctor`, `config`, `build`, `up`, `sync`, `stop`, `down`, `exec`, `run`, `shell`, `logs`, `dispatch`)
- [ ] Global options (`--config`, `--env-file`, `--debug`, `--debug-log`)
- [ ] Unknown command → `commands:` fallback from `wip.yml`
- [ ] **Reconcile help output wording against the wiki**
- [ ] Exit code parity (`1`, `127`, `130`)
- [ ] Decide whether `install-wsl-shim` is worth shipping (§3.5)

### Phase 4 — Real-environment verification

Close out what golden tests can't reach. Run **from both PowerShell and WSL2 bash**:

| Scenario | PowerShell | WSL2 bash |
|---|---|---|
| `wip init` → `doctor` → `build` → `up -d` → `exec` | ☐ | ☐ |
| `wip shell` (interactive: Ctrl-C, Ctrl-D, resize) | ☐ | ☐ |
| `wip run rails console` (interactive TTY) | ☐ | ☐ |
| `mode: compose-native` with `up` / `logs` | ☐ | ☐ |
| `sync` / `sync --watch` | ☐ | ☐ |
| `up --watch` (restart polling) | ☐ | ☐ |
| **Project on the WSL filesystem (`~/proj`)** | ☐ | ☐ |
| **Project on the Windows filesystem (`C:\proj`)** | ☐ | ☐ |
| A large context with `.dockerignore` active (**measure UNC walk performance**) | ☐ | ☐ |
| `--debug` / `--debug-log` | ☐ | ☐ |

- [ ] Dogfood on our own projects for one to two weeks

### Phase 5 — Ship

- [ ] Release v2.0.0
- [ ] **First PR** to winget-pkgs (allow time for review)
- [ ] Rewrite the README and wiki
  - Installation switches to WinGet
  - **Document the path constraint per the §3 outcome**
  - Note the removal of `shadow_context`
  - How to invoke from WSL2 (`wip.exe` / alias / shim)
- [ ] Publish a final gem from the new Ruby repository with a deprecation notice

---

## 11. Risks and Open Questions

### Risks

| Risk | Impact | Mitigation |
|---|---|---|
| 🔴 **wslc rejects bind mounts of WSL-side paths** | **High** | Settle first in Phase 0 Spike 1. If (b), document "projects live on the Windows filesystem" as a constraint. **This outcome may require rethinking the purpose of `sync:` itself** |
| **Walking files over UNC is too slow for large trees** | Medium | Take attributes from enumeration (§3.3). Measure in Phase 4; if inadequate, consider pushing the walk itself over to wslc/wsl.exe |
| **An exe launched from WSL bash has no console fit for interaction** | Medium | Phase 0 Spike 2. Fall back to ConPTY (§4.2 option B) if needed |
| **Some library doesn't work under AOT** | Medium | Phase 0 Spike 3 exercises every dependency up front. Fallbacks exist: hand-rolled YAML parser, hand-rolled CLI parser |
| **winget-pkgs first review drags on** | Low | The release itself can ship regardless (the ZIP is on Releases). Announce WinGet once review clears |
| **Friction from having to type `wip.exe`** | Low | §3.5. Absorbed by a shim or a documented alias |
| **Losing the spec reference once Ruby leaves** | Medium | Extract the golden fixtures **first**, in Phase 1. Keep the new Ruby repository reachable |

### To confirm from primary sources before starting

1. **wslc.exe's path acceptance rules** — §3. Nothing in this repository answers it; it needs hands-on measurement
2. **.NET version** — net10.0 (LTS) is assumed; confirm support status at kickoff
3. **System.CommandLine's current version and real AOT status** — particularly whether the unknown-command fallback is expressible cleanly
4. **YamlDotNet's AOT track record** — zero reflection warnings via the representation model?
5. **Whether `InvariantGlobalization=true` is appropriate** — any impact on `wip.yml`, paths, or container output containing non-ASCII text
6. **The working directory form WSL2 gives a Windows process** — `\\wsl.localhost\` or `\\wsl$\`, and whether it varies by WSL version
7. **Telling WSL2 apart from WSL1** — a zero exit from `wsl.exe --status` proves WSL is installed, not that the default version is 2. `wip doctor` therefore reports "WSL2 is available" for a WSL1-only machine. Distinguishing them means parsing that command's output, which is localised and UTF-16 encoded, so it needs a real machine to get right
8. **Current WinGet manifest schema** — the present syntax for `NestedInstallerType: portable`
9. **PackageIdentifier `Slidict.Wip`** — does it satisfy the publisher-name requirements?

---

## 12. Summary

- **Going Windows-only cut the port by roughly 20–30%.** The openpty, flock, and UnixFileMode P/Invokes, `/proc` parsing, the `Gem.win_platform?` branches, and the Linux RIDs with their separate distribution path all disappear.
- **Accepting breaking changes and moving Ruby out avoids the largest liability: maintaining two implementations in parallel.** The one prerequisite is extracting the golden fixtures before it goes (Phase 1).
- **One genuinely hard problem remains: the path model (§3).** Moving execution to the Windows side changes the meaning of all three places that hand wslc a host absolute path — `sync.source`, `volumes:`, and the build context. The build context is solved by staging locally at all times, but **the two bind-mount sites can't be settled until wslc's real behavior is measured.**
- **How to proceed:** settle the path model and get the distribution path working in Phase 0; extract the golden fixtures before Ruby leaves. With those two done, the rest is a mechanical port.
