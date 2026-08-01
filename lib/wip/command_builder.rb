# frozen_string_literal: true

require 'shellwords'

module Wip
  # Builds the argument arrays for wslc build/exec/run/custom invocations.
  class CommandBuilder
    def initialize(wslc:, config:, environment: Environment.new, dotenv: {})
      @wslc = wslc
      @config = config
      @environment = environment
      @dotenv = dotenv
    end

    def exec(arguments, settings: {}, interactive: true)
      values = @config.defaults.merge(settings)
      command = [@wslc, 'exec']
      command << '-it' if tty?(interactive)
      command.concat(options(values, include_container: true, include_publish: false)).concat(arguments)
    end

    def run(arguments, settings: {}, interactive: true)
      values = @config.defaults.merge(settings)
      command = [@wslc, 'run']
      command << '--rm' if values['remove']
      command << '-it' if tty?(interactive)
      command.concat(options(values)).push(required(values, 'image')).concat(arguments)
    end

    def up(detach: false)
      values = @config.defaults
      command = [@wslc, 'run', '--name', required(values, 'container')]
      command.push('--network', @config.network) if @config.network
      command << '-d' if detach
      command << '-it' if !detach && tty?(true)
      command.concat(options(values)).push(required(values, 'image'))
      command.concat(Shellwords.split(@config.up_command.to_s)) if @config.up_command
      command
    end

    def start(detach: false)
      command = [@wslc, 'start', required(@config.defaults, 'container')]
      command.push('-a', '-i') unless detach
      command
    end

    def find
      container = required(@config.defaults, 'container')
      [@wslc, 'list', '--all', '--filter', "name=#{container}", '--format', 'json']
    end

    def down
      [@wslc, 'stop', required(@config.defaults, 'container')]
    end

    def remove
      [@wslc, 'remove', '-f', required(@config.defaults, 'container')]
    end

    def network_create
      [@wslc, 'network', 'create', required_network]
    end

    def network_list
      [@wslc, 'network', 'list', '--format', 'json']
    end

    def dependency_up(name, detach: true)
      values = dependency_values(name)
      command = [@wslc, 'run', '--name', name.to_s]
      command.push('--network', @config.network) if @config.network
      command << '-d' if detach
      command.concat(options(values)).push(required(values, 'image'))
      command.concat(Shellwords.split(values['command'].to_s)) unless values['command'].to_s.empty?
      command
    end

    def dependency_start(name)
      [@wslc, 'start', name.to_s]
    end

    def dependency_find(name)
      [@wslc, 'list', '--all', '--filter', "name=#{name}", '--format', 'json']
    end

    def dependency_down(name)
      [@wslc, 'stop', name.to_s]
    end

    def dependency_remove(name)
      [@wslc, 'remove', '-f', name.to_s]
    end

    def build(settings:, extra: [])
      values = @config.defaults.merge(settings)
      context = values['context'] || '.'
      tag = values['tag'] || values['image']
      raise ConfigError, 'Build image/tag must not be empty' if tag.to_s.empty?

      [@wslc, 'build', '-t', tag, *extra, context]
    end

    def custom(name, arguments)
      values = @config.command(name) || raise(ConfigError, "Unknown command: #{name}")
      type = values['type'] || (name.to_s == 'build' ? 'build' : 'exec')
      base = Shellwords.split(values['command'].to_s)
      return build(settings: values, extra: arguments) if type == 'build'

      public_send(type, base + arguments, settings: values, interactive: values.fetch('interactive', false))
    end

    def tty?(requested) = requested && @environment.interactive?

    private

    def options(values, include_container: false, include_publish: true)
      result = []
      result.push('-w', values['workdir']) unless values['workdir'].to_s.empty?
      merged_env(values).each { |key, value| result.push('-e', "#{key}=#{value}") }
      if include_publish
        Array(values['ports']).each { |port| result.push('-p', port.to_s) }
        Array(values['volumes']).each { |volume| result.push('-v', volume.to_s) }
      end
      result << required(values, 'container') if include_container
      result
    end

    # .env supplies defaults; env set in wip.yml (defaults or per-command) wins on conflict.
    def merged_env(values) = @dotenv.merge(values.fetch('env', {}))

    def required(values, key)
      value = values[key]
      raise ConfigError, "Configured #{key} must not be empty" if value.to_s.empty?

      value
    end

    def required_network
      network = @config.network
      raise ConfigError, 'Configured network must not be empty' if network.to_s.empty?

      network
    end

    def dependency_values(name)
      @config.dependency(name) || raise(ConfigError, "Unknown dependency: #{name}")
    end
  end
end
