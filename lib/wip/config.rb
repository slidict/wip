# frozen_string_literal: true

require 'pathname'

module Wip
  # Validated, defaulted access to a parsed wip.yml document.
  class Config
    # Applied to every dependencies: entry, primary container included — there
    # is no separate, differently-shaped bucket for "the one you exec into."
    DEPENDENCY_DEFAULTS = { 'workdir' => nil, 'interactive' => false, 'remove' => true,
                            'env' => {}, 'ports' => [], 'volumes' => [] }.freeze
    SECRET_PATTERN = /token|password|secret|credential|auth/i
    # Which orchestration path `up`/`down`/`sync`/etc. take. Explicit rather than
    # inferred from a `compose:` block's presence, so a config reader doesn't have
    # to know that rule to predict which mode wip runs in.
    # compose bridges to an external compose-for-wslc binary (ComposeBridge);
    # compose-native parses compose.yml itself and drives wslc directly (ComposeFile) —
    # a stopgap for as long as those external tools stay incomplete (missing `run`, etc.).
    MODES = %w[container compose compose-native].freeze
    attr_reader :path

    def initialize(raw, path = nil)
      @raw = stringify(raw)
      @path = path
      validate!
    end

    def wslc_command = @raw.dig('wslc', 'command') || 'auto'
    def commands = @raw['commands'] || {}
    # Raw dependencies: block as written in wip.yml. Under compose-native mode this stays
    # empty by construction (validate_compose! forbids combining the two) — #dependencies
    # below is what callers actually want, since it's synthesized from compose.yml there.
    def raw_dependencies = @raw['dependencies'] || {}
    def dependencies = compose_native? ? parsed_compose_file.to_dependencies_hash : raw_dependencies
    # Which dependencies: entry `up`/`down`/`exec`/`run`/`build`/`commands:` target
    # by default — the one container wip itself considers "the app." Everything
    # else in dependencies: is a sidecar wip only starts and stops. No default:
    # guessing a name here (the old default was "app") either matches by luck or
    # fails in a way that doesn't point at the real problem (a differently-named
    # entry), so a project with any dependencies: must say which one explicitly.
    # Under compose-native mode, container: is compose.service — compose.yml already
    # names the service, so there's no separate container: key to set in wip.yml.
    def container = compose_native? ? compose_service : presence(@raw['container'])
    # Under compose-native mode, wip creates its own project network (compose.project,
    # or the wip.yml directory's name) so services can reach each other by name — the
    # same guarantee real Compose's per-project network gives.
    def network = compose_native? ? (compose_project || default_compose_network) : @raw['network']
    def mode = @raw['mode'] || 'container'
    def compose = @raw['compose']
    def compose? = mode == 'compose'
    def compose_native? = mode == 'compose-native'
    def compose_mode? = compose? || compose_native?
    def compose_service = compose && compose['service']
    def compose_file = compose && compose['file']
    def compose_project = compose && compose['project']
    def compose_command = compose && compose['command']
    # name => {context:, dockerfile:, tag:} for compose-native services with a build:
    # instead of an image: — empty otherwise. Consumed by `wip up` to build each one
    # before starting it (see CommandBuilder#build).
    def compose_build_specs = compose_native? ? parsed_compose_file.build_specs : {}
    def sync? = !!sync

    def sync
      return @sync if defined?(@sync)

      @sync = @raw.key?('sync') ? build_sync : nil
    end

    # The dependencies: entry `container` points at, or nil if it isn't defined.
    def primary = dependency(container)

    def command(name)
      entry = commands[name.to_s]
      return unless entry

      (primary || {}).merge('type' => 'exec').merge(entry)
    end

    def dependency(name)
      entry = dependencies[name.to_s]
      return unless entry

      DEPENDENCY_DEFAULTS.merge(entry)
    end

    def to_h(redact: true)
      value = { 'version' => 1, 'wslc' => { 'command' => wslc_command }, 'mode' => mode, 'container' => container,
                'network' => network, 'dependencies' => dependencies, 'compose' => compose, 'sync' => sync&.to_h,
                'commands' => commands.transform_values do |entry|
                  (primary || {}).merge('type' => 'exec').merge(entry)
                end }
      redact ? redact_secrets(value) : value
    end

    private

    def validate!
      raise ConfigError, "Unsupported configuration version: #{@raw['version']}" unless (@raw['version'] || 1) == 1

      validate_mode!
      validate_commands!
      validate_dependencies!
      validate_compose!
      validate_sync!
    end

    def build_sync
      SyncSettings.new(@raw['sync'], base: path && File.dirname(path.to_s),
                                     workdir: primary && primary['workdir'], container: container,
                                     compose: compose?)
    end

    def validate_mode!
      raise ConfigError, "mode must be one of #{MODES.join(', ')}" unless MODES.include?(mode)
      raise ConfigError, "mode: #{mode} requires a compose: block" if compose_mode? && !@raw.key?('compose')
      return unless @raw.key?('compose') && !compose_mode?

      raise ConfigError, 'a compose: block requires mode: compose or compose-native'
    end

    # Forces SyncSettings to build (and so run its own validation, including
    # sync.mode vs. mode) at load time instead of on first access.
    def validate_sync!
      sync if @raw.key?('sync')
    end

    def validate_commands!
      raise ConfigError, 'commands must be a mapping' unless commands.is_a?(Hash)

      commands.each { |name, entry| validate_command!(name, entry) }
    end

    def validate_dependencies!
      raise ConfigError, 'dependencies must be a mapping' unless raw_dependencies.is_a?(Hash)
      # Under either compose mode, populated dependencies: is already an error on its
      # own (validate_compose!, below) — this only needs to fire for the mode: container
      # case, where dependencies: having entries but no container: is otherwise valid.
      if raw_dependencies.any? && !container && !compose_mode?
        raise ConfigError,
              'container: must be set when dependencies: has entries'
      end

      raw_dependencies.each { |name, entry| validate_dependency!(name, entry) }
    end

    def validate_compose!
      return unless @raw.key?('compose')
      raise ConfigError, 'compose must be a mapping' unless compose.is_a?(Hash)
      raise ConfigError, 'compose.service must not be empty' if compose_service.to_s.empty?
      raise ConfigError, 'compose is mutually exclusive with dependencies' if raw_dependencies.any?
      raise ConfigError, 'compose is mutually exclusive with network' if @raw['network']

      validate_compose_command!
    end

    # compose.command has opposite requiredness depending on which compose mode this
    # is: mode: compose has no default (every external compose-for-wslc implementation
    # must be named explicitly), while mode: compose-native drives wslc directly and so
    # has no external binary to name at all.
    def validate_compose_command!
      if compose_native?
        if compose_command
          raise ConfigError, 'compose.command is not used under mode: compose-native (wip drives wslc ' \
                             'directly — there’s no external compose binary to name)'
        end
      elsif compose_command.to_s.empty?
        raise ConfigError, 'compose.command must not be empty'
      end
    end

    def validate_command!(name, entry)
      raise ConfigError, "commands.#{name} must be a mapping" unless entry.is_a?(Hash)

      type = entry['type'] || (name == 'build' ? 'build' : 'exec')
      raise ConfigError, "Invalid command type for #{name}: #{type}" unless %w[exec run build].include?(type)

      entry['env']&.transform_values!(&:to_s)
    end

    def validate_dependency!(name, entry)
      raise ConfigError, "dependencies.#{name} must be a mapping" unless entry.is_a?(Hash)
      raise ConfigError, "dependencies.#{name} must set image" if entry['image'].to_s.empty?

      entry['env']&.transform_values!(&:to_s)
    end

    def stringify(object)
      case object
      when Hash then object.to_h { |key, value| [key.to_s, stringify(value)] }
      when Array then object.map { |value| stringify(value) }
      else object
      end
    end

    def redact_secrets(object)
      return object.map { |item| redact_secrets(item) } if object.is_a?(Array)
      return object unless object.is_a?(Hash)

      object.to_h { |key, value| [key, key.match?(SECRET_PATTERN) ? '[REDACTED]' : redact_secrets(value)] }
    end

    def presence(value) = value.to_s.empty? ? nil : value.to_s

    def parsed_compose_file
      @parsed_compose_file ||= ComposeFile.load(ComposeBridge.file_path(self))
    end

    def default_compose_network = path && Pathname(path).dirname.basename.to_s
  end
end
