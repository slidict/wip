# frozen_string_literal: true

require 'shellwords'

module Wip
  # Builds the argument arrays for wslc build/exec/run/custom invocations.
  class CommandBuilder
    def initialize(wslc:, config:, environment: Environment.new)
      @wslc = wslc
      @config = config
      @environment = environment
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

    def down
      [@wslc, 'stop', required(@config.defaults, 'container')]
    end

    def remove
      [@wslc, 'remove', '-f', required(@config.defaults, 'container')]
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

    private

    def options(values, include_container: false, include_publish: true)
      result = []
      result.push('-w', values['workdir']) unless values['workdir'].to_s.empty?
      values.fetch('env', {}).each { |key, value| result.push('-e', "#{key}=#{value}") }
      if include_publish
        Array(values['ports']).each { |port| result.push('-p', port.to_s) }
        Array(values['volumes']).each { |volume| result.push('-v', volume.to_s) }
      end
      result << required(values, 'container') if include_container
      result
    end

    def required(values, key)
      value = values[key]
      raise ConfigError, "Configured #{key} must not be empty" if value.to_s.empty?

      value
    end

    def tty?(requested) = requested && @environment.interactive?
  end
end
