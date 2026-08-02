# frozen_string_literal: true

module Wip
  # Validated, defaulted access to a parsed wip.yml document.
  class Config
    DEFAULTS = { 'container' => 'app', 'workdir' => '/app', 'interactive' => false,
                 'remove' => true, 'env' => {}, 'ports' => [], 'volumes' => [] }.freeze
    SECRET_PATTERN = /token|password|secret|credential|auth/i
    # Which orchestration path `up`/`down`/`sync`/etc. take. Explicit rather than
    # inferred from a `compose:` block's presence, so a config reader doesn't have
    # to know that rule to predict which mode wip runs in.
    MODES = %w[container compose].freeze
    attr_reader :path

    def initialize(raw, path = nil)
      @raw = stringify(raw)
      @path = path
      validate!
    end

    def wslc_command = @raw.dig('wslc', 'command') || 'auto'
    def commands = @raw['commands'] || {}
    def defaults = DEFAULTS.merge(@raw['defaults'] || {})
    def up_command = @raw.dig('up', 'command')
    def dependencies = @raw['dependencies'] || {}
    def network = defaults['network']
    def mode = @raw['mode'] || 'container'
    def compose = @raw['compose']
    def compose? = mode == 'compose'
    def compose_service = compose && compose['service']
    def compose_file = compose && compose['file']
    def compose_project = compose && compose['project']
    def compose_command = compose && compose['command']
    def sync? = !!sync

    def sync
      return @sync if defined?(@sync)

      @sync = @raw.key?('sync') ? build_sync : nil
    end

    def command(name)
      entry = commands[name.to_s]
      return unless entry

      defaults.merge('type' => 'exec').merge(entry)
    end

    def dependency(name)
      entry = dependencies[name.to_s]
      return unless entry

      { 'workdir' => nil, 'env' => {}, 'ports' => [], 'volumes' => [] }.merge(entry)
    end

    def to_h(redact: true)
      value = { 'version' => 1, 'wslc' => { 'command' => wslc_command }, 'defaults' => defaults,
                'up' => { 'command' => up_command }, 'mode' => mode, 'dependencies' => dependencies,
                'compose' => compose, 'sync' => sync&.to_h,
                'commands' => commands.transform_values { |entry| defaults.merge('type' => 'exec').merge(entry) } }
      redact ? redact_secrets(value) : value
    end

    private

    def validate!
      raise ConfigError, "Unsupported configuration version: #{@raw['version']}" unless (@raw['version'] || 1) == 1
      raise ConfigError, 'up must be a mapping' if @raw.key?('up') && !@raw['up'].is_a?(Hash)

      validate_mode!
      validate_commands!
      validate_dependencies!
      validate_compose!
      validate_sync!
    end

    def build_sync
      SyncSettings.new(@raw['sync'], base: path && File.dirname(path.to_s),
                                     workdir: defaults['workdir'], container: defaults['container'],
                                     compose: compose?)
    end

    def validate_mode!
      raise ConfigError, "mode must be one of #{MODES.join(', ')}" unless MODES.include?(mode)
      raise ConfigError, 'mode: compose requires a compose: block' if compose? && !@raw.key?('compose')
      raise ConfigError, 'a compose: block requires mode: compose' if @raw.key?('compose') && !compose?
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
      raise ConfigError, 'dependencies must be a mapping' unless dependencies.is_a?(Hash)

      dependencies.each { |name, entry| validate_dependency!(name, entry) }
    end

    def validate_compose!
      return unless @raw.key?('compose')
      raise ConfigError, 'compose must be a mapping' unless compose.is_a?(Hash)
      raise ConfigError, 'compose.service must not be empty' if compose_service.to_s.empty?
      raise ConfigError, 'compose.command must not be empty' if compose_command.to_s.empty?
      raise ConfigError, 'compose is mutually exclusive with dependencies' if dependencies.any?
      raise ConfigError, 'compose is mutually exclusive with defaults.network' if network
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
  end
end
