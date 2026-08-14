# frozen_string_literal: true

# Generates the table-driven half of the golden corpus: the pure functions whose
# behaviour is easiest to get subtly wrong when reimplemented, and where a
# hand-written expectation would just encode the porter's assumptions instead of
# the Ruby build's actual output. See tools/golden/generate.rb for the rest.
#
#   ruby tools/golden/generate_units.rb

$LOAD_PATH.unshift(File.expand_path('../../lib', __dir__))

require 'json'
require 'shellwords'
require 'tmpdir'
require 'wip'

ROOT = File.expand_path('../..', __dir__)
UNITS_DIR = File.join(ROOT, 'tests', 'golden', 'units')

# Shellwords.split is what turns a `command:` string into argv. wip has no
# equivalent in the BCL, so the C# port hand-writes one; these pin its edges.
SHELLWORDS_INPUTS = [
  '',
  '   ',
  'bin/rails',
  'bin/rails server -p 3000',
  "echo 'single quoted'",
  'echo "double quoted"',
  'echo "with  inner   spaces"',
  'echo hello\\ world',
  'a"b"c',
  "a'b'c",
  'sh -c "echo nested \'quotes\'"',
  'sh -c \'echo "double inside single"\'',
  "tab\tseparated words",
  "newline\nseparated",
  'trailing   ',
  '   leading',
  'back\\\\slash',
  'empty "" args',
  "empty '' args",
  '$VAR stays literal',
  'a\\"b',
  'unmatched "quote',
  "unmatched 'quote",
  'trailing backslash \\'
].freeze

VARIABLE_INTERPOLATION_ENV = { 'SET' => 'value', 'EMPTY' => '', 'NUM' => '42' }.freeze

VARIABLE_INTERPOLATION_INPUTS = [
  'plain text',
  '${SET}',
  '$SET',
  '${UNSET}',
  '$UNSET',
  '${EMPTY}',
  '${SET:-fallback}',
  '${EMPTY:-fallback}',
  '${UNSET:-fallback}',
  '${SET-fallback}',
  '${EMPTY-fallback}',
  '${UNSET-fallback}',
  '${UNSET:-}',
  '$$',
  '$$SET',
  '$${SET}',
  'prefix ${SET} suffix',
  '${SET}${NUM}',
  '${UNSET:?required}',
  '${SET:+alternate}',
  'user: ${NUM}:${NUM}',
  '$1 positional',
  '${lowercase}',
  'a$-b'
].freeze

DOCKERIGNORE_PATTERNS = <<~IGNORE
  # comment
  node_modules
  *.log
  !important.log
  /root-only.txt
  tmp/
  **/generated
  docs/*.md
  !docs/README.md
  build/output
IGNORE

DOCKERIGNORE_PATHS = %w[
  app.rb
  node_modules
  node_modules/pkg/index.js
  debug.log
  important.log
  nested/debug.log
  nested/important.log
  root-only.txt
  nested/root-only.txt
  tmp
  tmp/cache/file
  generated
  a/b/generated
  a/b/generated/file.txt
  docs/guide.md
  docs/README.md
  docs/nested/guide.md
  build
  build/output
  build/output/bin
  build/other
].freeze

ERROR_INTERPRETER_INPUTS = [
  '',
  'some unrelated failure',
  'error 0x8007000E while attaching',
  'too many mounted volumes',
  'マウントされているボリュームが多すぎます',
  'Error: pull access denied for private/image',
  'insufficient_scope: authorization failed',
  'no matching manifest for linux/amd64 in the manifest list entries',
  'no matching manifest for linux/arm64',
  'rsync: not found',
  'rsync: command not found',
  'exec: "rsync": executable file not found in $PATH',
  'executable file not found: rsync'
].freeze

def capture
  { 'ok' => yield }
rescue Wip::Error, ArgumentError => e
  { 'error' => e.message }
end

def shellwords_cases
  SHELLWORDS_INPUTS.map { |input| { 'input' => input, 'result' => capture { Shellwords.split(input) } } }
end

def variable_interpolation_cases
  VARIABLE_INTERPOLATION_INPUTS.map do |input|
    { 'input' => input,
      'result' => capture { Wip::VariableInterpolation.call(input, VARIABLE_INTERPOLATION_ENV) } }
  end
end

def dockerignore_cases
  ignore = Wip::DockerIgnore.new(DOCKERIGNORE_PATTERNS.lines)
  DOCKERIGNORE_PATHS.map do |path|
    { 'path' => path,
      'ignored' => ignore.ignored?(path),
      'prunable' => ignore.prunable?(path) }
  end
end

def error_interpreter_cases
  interpreter = Wip::ErrorInterpreter.new(architecture: 'linux/amd64')
  ERROR_INTERPRETER_INPUTS.map { |input| { 'input' => input, 'hint' => interpreter.interpret(input) } }
end

def command_display_cases
  [
    %w[wslc.exe run --rm alpine sh],
    ['wslc.exe', 'run', '-e', 'TOKEN=super-secret', 'alpine'],
    ['wslc.exe', 'exec', '-e', 'A=1', '-e', 'B=2', 'app', 'sh', '-c', 'echo hi there'],
    ['wslc.exe', 'run', '-e', 'NOVALUE', 'alpine'],
    ['wslc.exe', 'run', '-e', 'EMPTY=', 'alpine']
  ].map { |command| { 'command' => command, 'display' => Wip::CommandDisplay.for_debug(command) } }
end

Dir.mkdir(UNITS_DIR) unless Dir.exist?(UNITS_DIR)

{
  'shellwords' => shellwords_cases,
  'variable_interpolation' => { 'env' => VARIABLE_INTERPOLATION_ENV, 'cases' => variable_interpolation_cases },
  'dockerignore' => { 'patterns' => DOCKERIGNORE_PATTERNS, 'cases' => dockerignore_cases },
  'error_interpreter' => { 'architecture' => 'linux/amd64', 'cases' => error_interpreter_cases },
  'command_display' => command_display_cases
}.each do |name, payload|
  File.write(File.join(UNITS_DIR, "#{name}.json"), "#{JSON.pretty_generate(payload)}\n")
  count = payload.is_a?(Hash) ? payload['cases'].size : payload.size
  puts "#{name}: #{count} cases"
end
