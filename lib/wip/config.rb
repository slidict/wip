# frozen_string_literal: true

module Wip
  # Validated, defaulted access to a parsed wip.yml document.
  class Config
    DEFAULTS = { 'container' => 'app', 'workdir' => '/app', 'interactive' => false,
                 'remove' => true, 'env' => {}, 'ports' => [], 'volumes' => [] }.freeze
    SECRET_PATTERN = /token|password|secret|credential|auth/i
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

    def command(name)
      entry = commands[name.to_s]
      return unless entry

      defaults.merge('type' => 'exec').merge(entry)
    end

    def to_h(redact: true)
      value = { 'version' => 1, 'wslc' => { 'command' => wslc_command }, 'defaults' => defaults,
                'up' => { 'command' => up_command },
                'commands' => commands.transform_values { |entry| defaults.merge('type' => 'exec').merge(entry) } }
      redact ? redact_secrets(value) : value
    end

    private

    def validate!
      raise ConfigError, "Unsupported configuration version: #{@raw['version']}" unless (@raw['version'] || 1) == 1
      raise ConfigError, 'commands must be a mapping' unless commands.is_a?(Hash)
      raise ConfigError, 'up must be a mapping' if @raw.key?('up') && !@raw['up'].is_a?(Hash)

      commands.each { |name, entry| validate_command!(name, entry) }
    end

    def validate_command!(name, entry)
      raise ConfigError, "commands.#{name} must be a mapping" unless entry.is_a?(Hash)

      type = entry['type'] || (name == 'build' ? 'build' : 'exec')
      raise ConfigError, "Invalid command type for #{name}: #{type}" unless %w[exec run build].include?(type)

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
