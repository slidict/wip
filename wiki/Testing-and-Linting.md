# Testing and Linting

## Running them

```bash
bundle exec rspec     # unit tests
bundle exec rubocop   # style/lint
bundle exec rake      # both — the default task
```

`rake` with no arguments runs `spec` then `rubocop`. CI runs them as separate steps
(`rake spec`, `rake rubocop`) so a failure names which one.

Useful during development:

```bash
bundle exec rspec spec/wip/config_spec.rb
bundle exec rspec spec/wip/config_spec.rb:42
bundle exec rspec --only-failures
bundle exec rubocop -a          # safe autocorrect
bundle exec rubocop -A          # includes unsafe autocorrect — review the diff
```

## No WSLC required

The suite runs on any platform. Every layer that would touch the outside world is injectable:

| Class | Injection point |
|---|---|
| `CommandResolver` | `executable:` — a callable deciding whether a candidate exists |
| `CommandResolver` | `path:` — the `PATH` string to search |
| `CommandBuilder` | `wslc:`, `config:`, `environment:`, `dotenv:` — and it only *builds* arrays |
| `CommandRunner` | `stdin:` / `stdout:` / `stderr:` — `StringIO` in tests |
| `CommandRunner` | `interpreter:` — a stub `ErrorInterpreter` |
| `Doctor` | `loader:`, `resolver:`, `environment:`, `compose_resolver:` |
| `Environment` | `stdin:` / `stdout:` for TTY detection |
| `DebugReporter` / `ResourceMonitor` / `StagingProgress` | `out:` |

Keep it that way. A change that hardcodes a real filesystem path, a real `PATH` lookup, or a real
spawn makes that code untestable on CI.

## Where a test belongs

One spec file per class, mirroring `lib/`:

```
lib/wip/config.rb          →  spec/wip/config_spec.rb
lib/wip/compose_file.rb    →  spec/wip/compose_file_spec.rb
lib/wip/command_builder.rb →  spec/wip/command_builder_spec.rb
```

Test at the layer that owns the behavior:

| Behavior | Spec |
|---|---|
| A validation rule / a `ConfigError` | `config_spec.rb`, `sync_settings_spec.rb`, `compose_file_spec.rb` |
| The exact `wslc` arguments produced | `command_builder_spec.rb`, `compose_bridge_spec.rb` |
| A diagnostic's level and message | `doctor_spec.rb` |
| Flag parsing, command wiring, orchestration order | `cli_spec.rb` |
| `.dockerignore` matching, staging, shadow sync | `docker_ignore_spec.rb`, `build_context_spec.rb` |

Prefer the narrowest layer. A new `wip.yml` rule is a `config_spec.rb` test, not an end-to-end CLI
test that happens to exercise it.

## What a good test asserts

**For command construction — the exact array:**

```ruby
expect(builder.exec(['bin/rails', 'c'], interactive: true)).to eq(
  ['wslc.exe', 'exec', '-it', '-w', '/app', '-e', 'RAILS_ENV=development', 'app', 'bin/rails', 'c']
)
```

Arrays, not joined strings — the array *is* the injection-safety guarantee.

**For validation — the message, not just the class:**

```ruby
expect { described_class.new(raw) }
  .to raise_error(Wip::ConfigError, /container: must be set when dependencies: has entries/)
```

Every `ConfigError` in this codebase names the offending key. Asserting on the message keeps that
property from eroding.

**For defaults — assert the default explicitly**, so a change to it is a visible test failure.

## RuboCop configuration

`.rubocop.yml`:

| Setting | Value |
|---|---|
| `TargetRubyVersion` | 3.2 |
| `NewCops` | enabled |
| `Layout/LineLength` | 120 |
| `Metrics/MethodLength` | 20 |
| `Metrics/AbcSize` | 25 |
| `Metrics/ClassLength` | 160, waived for `cli.rb`, `compose_file.rb`, `config.rb` |
| `Metrics/BlockLength` | waived for `spec/**/*` |

Every file starts with `# frozen_string_literal: true`.

The three class-length exemptions are acknowledged debt, not permission to keep growing those
files. Prefer extracting a collaborator over widening the waiver.

### Inline disables

Occasionally necessary — with a reason:

```ruby
file = File.open(path, 'a') # rubocop:disable Style/FileOpen -- closed by `step`'s ensure block
```

A bare `rubocop:disable` with no explanation will get a review comment.

## The CI matrix

`.github/workflows/test.yml`, on every PR and every push to `main`:

- Ruby **3.2, 3.3, 3.4, 4.0** (`fail-fast: false`)
- `bundle exec rake spec`
- `bundle exec rake rubocop`
- CLI smoke test: `bundle exec exe/wip help | grep -q '^Commands:'`

The smoke test is a cheap guard against a change that breaks the executable while every unit test
still passes.

## Before opening a PR

```bash
bundle exec rake
```

Clean output, on the oldest supported Ruby if you can — 3.2 is the target version, and syntax newer
than that will pass locally on 3.4 and fail in CI.

## Related

- [Development](Development)
- [Architecture](Architecture)
