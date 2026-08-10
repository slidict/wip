# Development

Contributor-facing detail.
[CONTRIBUTING.md](https://github.com/slidict/wip/blob/main/CONTRIBUTING.md) is the summary; this is
where the depth lives.

## Setting up

```bash
git clone https://github.com/slidict/wip.git
cd wip
bundle install
bundle exec exe/wip version
```

Requires Ruby 3.2+. Runtime dependencies are just `thor`; development adds `rake`, `rspec`, and
`rubocop`.

### You don't need WSLC to develop wip

The test suite doesn't require it. The resolution, build, and execution layers are all swappable —
`CommandResolver` takes an injectable executable check, `CommandBuilder` produces argument arrays
without running them, and `CommandRunner` takes injectable IO. So specs assert on the *arrays wip
would run*, not on real containers, and you can develop on any platform.

That design constraint is worth preserving: a change that makes a layer un-injectable makes it
untestable. See [Architecture](Architecture).

## Running the checks

```bash
bundle exec rspec     # unit tests
bundle exec rubocop   # style/lint
bundle exec rake      # both (the default task)
```

Both must pass before a PR merges. Details, including how to write specs that match the existing
style: [Testing and Linting](Testing-and-Linting).

## CI

`.github/workflows/test.yml` runs on every pull request and every push to `main`:

- **Ruby matrix:** 3.2, 3.3, 3.4, 4.0 (`fail-fast: false`, so you see every failing version)
- `bundle exec rake spec`
- `bundle exec rake rubocop`
- a CLI smoke test: `bundle exec exe/wip help | grep -q '^Commands:'`

## Commit conventions

[Conventional Commits](https://www.conventionalcommits.org/):

```
<type>[optional scope]: <description>
```

Types in use: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `ci`, `build`, `perf`.

```
feat(cli): add doctor command
fix(config): accept positional path argument
ci: add push trigger for main
```

The type matters beyond tidiness: **PR labels drive the generated release notes** —
`feat` → 🚀 Features, `fix` → 🐛 Fixes, and `chore`/`ci`/`docs`/`build`/`perf`/`test` →
🧰 Maintenance. See [Release Process](Release-Process).

## Versioning

[SemVer](https://semver.org/):

| Bump | For |
|---|---|
| patch | bug fixes (`fix`) |
| minor | new commands or features (`feat`) |
| major | breaking changes |

**Maintainers bump the version**, via the "Bump Version" GitHub Actions workflow — you don't touch
`lib/wip/version.rb` in a PR.

## Making a pull request

1. Fork and branch from `main`.
2. Make the change, with tests for new behavior.
3. `bundle exec rake` — clean.
4. Open a PR describing what changed, why, and how you verified it.

Small, focused PRs are much easier to review. If a change grows a second unrelated concern, split
it.

### What reviewers look for

- **Tests for new behavior**, at the layer that owns it — a config rule belongs in
  `spec/wip/config_spec.rb`, not an end-to-end CLI test.
- **Errors that name the offending key.** Every `ConfigError` in this codebase says which key is
  wrong; keep that up. See [Configuration Errors](Configuration-Errors).
- **Load-time validation** over runtime surprises — fail before creating containers.
- **Comments that explain *why*.** This codebase leans on them heavily for non-obvious constraints
  (why an absolute build context crashes `wslc`, why `restart: no` needs normalizing, why the
  shadow root can't live inside the context). Preserve that reasoning when you touch the code.
- **Documentation.** A user-visible change means updating the README *and* the relevant wiki page.

## Where things live

```
exe/wip              # the executable
lib/wip.rb           # requires everything
lib/wip/             # one class per concern
spec/wip/            # one spec file per class
docs/                # README assets (logo, demo gif/tape)
```

Per-module detail: [Architecture](Architecture).

## Related

- [Architecture](Architecture)
- [Testing and Linting](Testing-and-Linting)
- [Release Process](Release-Process)
- [Reporting Issues](Reporting-Issues)
