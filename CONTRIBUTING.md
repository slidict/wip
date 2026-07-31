# Contributing to wip

Thanks for taking the time to contribute! Bug reports, feature requests, and pull requests are
all welcome.

## Development setup

```bash
git clone https://github.com/slidict/wip.git
cd wip
bundle install
```

`wip` itself talks to `wslc.exe`/`wslc`, but the test suite doesn't require WSLC — the
resolution, build, and execution layers are all swappable, so you can develop and test on any
platform.

## Running checks locally

```bash
bundle exec rspec     # unit tests
bundle exec rubocop   # style/lint
bundle exec rake      # both
```

Both must pass before a PR is merged; CI runs the same checks on Ruby 3.2, 3.3, 3.4, and 4.0.

## Commit messages

This project uses [Conventional Commits](https://www.conventionalcommits.org/).

Format: `<type>[optional scope]: <description>`

Common types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `ci`.

Examples:

- `feat(cli): add doctor command`
- `fix(config): accept positional path argument`
- `ci: add push trigger for main`

PR labels drive the release notes (`feat` → Features, `fix` → Fixes, everything else →
Maintenance), so an accurate type matters even for small changes.

## Versioning

`wip` follows [Semantic Versioning](https://semver.org/):

- **patch** — bug fixes (`fix`)
- **minor** — new commands or features (`feat`)
- **major** — breaking changes

Maintainers bump the version (`lib/wip/version.rb`) via the "Bump Version" GitHub Actions
workflow; you don't need to bump it yourself in a PR.

## Pull requests

1. Fork and branch from `main`.
2. Make your change, with tests for new behavior.
3. Run `bundle exec rake` (RSpec + RuboCop) and make sure it's clean.
4. Open a PR with a summary of what changed and why, and how you verified it.

Small, focused PRs are easier to review than large ones — if a change grows a second unrelated
concern, consider splitting it.

## Reporting issues

When filing a bug, please include:

- Your `wip.yml` (with secrets redacted)
- The exact command you ran and its output (`WIP_DEBUG=1` output is especially helpful)
- Your `wip doctor` output
- OS/WSL/WSLC versions
