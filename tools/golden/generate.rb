# frozen_string_literal: true

# Generates tests/golden/**/cases.json from the Ruby implementation.
#
# The point is that the expectations are *produced by the code being replaced*,
# not written by hand: whatever the Ruby build of wip does today becomes the
# contract the C# port has to meet. Run this while the Ruby implementation is
# still in the tree (see docs/csharp-migration-plan.md, Phase 1) — afterwards the
# JSON is the only surviving record.
#
#   ruby tools/golden/generate.rb
#
# Absolute paths are rewritten to <FIXTURE> so the output does not depend on
# where the repository is checked out. Entries whose value is a host path are
# the ones expected to change under the Windows-native execution model; they are
# listed in tests/golden/README.md.

$LOAD_PATH.unshift(File.expand_path('../../lib', __dir__))

require 'json'
require 'wip'

ROOT = File.expand_path('../..', __dir__)
CASES_DIR = File.join(ROOT, 'tests', 'golden', 'cases')
WSLC = 'wslc.exe'

# CommandBuilder asks the environment whether stdin/stdout are a terminal, which
# decides whether `-it` is appended. Pin both answers instead of inheriting the
# generator's own tty state, so the fixtures cover each branch deterministically.
FakeEnvironment = Struct.new(:interactive) do
  def interactive? = interactive
  def windows? = false
  def wsl2? = true
  def windows_interop? = true
  def architecture = 'linux/amd64'
end

def normalize(value, fixture_dir)
  case value
  when Hash then value.to_h { |k, v| [normalize(k, fixture_dir), normalize(v, fixture_dir)] }
  when Array then value.map { |v| normalize(v, fixture_dir) }
  when String then value.gsub(fixture_dir, '<FIXTURE>')
  else value
  end
end

def capture(fixture_dir)
  { 'ok' => normalize(yield, fixture_dir) }
rescue Wip::Error => e
  { 'error' => normalize(e.message, fixture_dir) }
end

def builders(config, dotenv)
  { tty: Wip::CommandBuilder.new(wslc: WSLC, config: config, dotenv: dotenv,
                                 environment: FakeEnvironment.new(true)),
    notty: Wip::CommandBuilder.new(wslc: WSLC, config: config, dotenv: dotenv,
                                   environment: FakeEnvironment.new(false)) }
end

def config_operations(config, dotenv)
  {
    'config.to_h' => -> { config.to_h },
    'config.to_h.unredacted' => -> { config.to_h(redact: false) },
    'config.mode' => -> { config.mode },
    'config.container' => -> { config.container },
    'config.network' => -> { config.network },
    'config.wslc_command' => -> { config.wslc_command },
    'config.dependencies.keys' => -> { config.dependencies.keys },
    'config.commands.keys' => -> { config.commands.keys },
    'config.compose_build_specs' => -> { config.compose_build_specs },
    'config.sync.to_h' => -> { config.sync&.to_h },
    'dotenv' => -> { dotenv }
  }
end

# Every builder entry point, under both tty answers. Anything that raises is
# recorded as an error string — the messages are part of the contract too.
def builder_operations(config, tty, notty)
  ops = {
    'builder.up.detached' => -> { tty.up(detach: true) },
    'builder.up.attached.tty' => -> { tty.up(detach: false) },
    'builder.up.attached.notty' => -> { notty.up(detach: false) },
    'builder.start.detached' => -> { tty.start(detach: true) },
    'builder.start.attached' => -> { tty.start(detach: false) },
    'builder.find' => -> { tty.find },
    'builder.stop' => -> { tty.stop },
    'builder.remove' => -> { tty.remove },
    'builder.network_create' => -> { tty.network_create },
    'builder.network_list' => -> { tty.network_list },
    'builder.exec.tty' => -> { tty.exec(%w[bash -lc ls], interactive: true) },
    'builder.exec.notty' => -> { notty.exec(%w[bash -lc ls], interactive: true) },
    'builder.exec.noninteractive' => -> { tty.exec(%w[bash -lc ls], interactive: false) },
    'builder.run.tty' => -> { tty.run(%w[ls -la], interactive: true) },
    'builder.run.notty' => -> { notty.run(%w[ls -la], interactive: true) },
    'builder.build' => -> { tty.build(settings: config.command('build') || {}) },
    'builder.build.extra' => -> { tty.build(settings: config.command('build') || {}, extra: ['--no-cache']) },
    'builder.sync_run' => -> { tty.sync_run },
    'builder.sync_exec' => -> { tty.sync_exec },
    'builder.sync_build' => -> { tty.sync_build('.') },
    'builder.logs.follow' => -> { tty.logs('web', follow: true) },
    'builder.logs.nofollow' => -> { tty.logs('web', follow: false) }
  }
  ops.merge(dependency_operations(config, tty)).merge(command_operations(config, tty))
end

def dependency_operations(config, builder)
  names = begin
    config.dependencies.keys
  rescue Wip::Error
    []
  end
  names.each_with_object({}) do |name, ops|
    ops["builder.dependency_up[#{name}]"] = -> { builder.dependency_up(name) }
    ops["builder.dependency_up.attached[#{name}]"] = -> { builder.dependency_up(name, detach: false) }
    ops["builder.dependency_start[#{name}]"] = -> { builder.dependency_start(name) }
    ops["builder.dependency_find[#{name}]"] = -> { builder.dependency_find(name) }
    ops["builder.dependency_stop[#{name}]"] = -> { builder.dependency_stop(name) }
    ops["builder.dependency_remove[#{name}]"] = -> { builder.dependency_remove(name) }
  end
end

def command_operations(config, builder)
  config.commands.keys.each_with_object({}) do |name, ops|
    ops["builder.custom[#{name}]"] = -> { builder.custom(name, []) }
    ops["builder.custom.args[#{name}]"] = -> { builder.custom(name, %w[--trace extra]) }
    ops["config.command[#{name}]"] = -> { config.command(name) }
  end
end

def generate(fixture_dir)
  path = File.join(fixture_dir, 'wip.yml')
  env_file = File.join(fixture_dir, '.env')
  dotenv = Wip::DotenvLoader.new(env_file).load
  config = Wip::ConfigLoader.new(path: path, env_file: (env_file if File.file?(env_file))).load
  built = builders(config, dotenv)

  operations = config_operations(config, dotenv)
             .merge(builder_operations(config, built[:tty], built[:notty]))
  operations.to_h { |name, op| [name, capture(fixture_dir) { op.call }] }
end

fixtures = Dir.children(CASES_DIR).sort.select { |name| File.directory?(File.join(CASES_DIR, name)) }
fixtures.each do |name|
  dir = File.join(CASES_DIR, name)
  cases = generate(dir)
  File.write(File.join(dir, 'cases.json'), "#{JSON.pretty_generate(cases)}\n")
  errors = cases.count { |_, result| result.key?('error') }
  puts "#{name}: #{cases.size} operations (#{errors} recorded as errors)"
end
