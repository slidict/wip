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
      # dependencies: entries don't carry their own name (it's the hash key), so the
      # exec target defaults to @config.container; a commands: entry can still
      # redirect it by setting its own `container:`.
      values = primary_values.merge('container' => required_container).merge(settings)
      command = [@wslc, 'exec']
      command << '-it' if tty?(interactive)
      command.concat(options(values, include_container: true, include_publish: false)).concat(arguments)
    end

    def run(arguments, settings: {}, interactive: true)
      values = primary_values.merge(settings)
      command = [@wslc, 'run']
      command << '--rm' if values['remove']
      command << '-it' if tty?(interactive)
      command.concat(options(values)).push(required(values, 'image')).concat(arguments)
    end

    def up(detach: false)
      values = primary_values
      command = [@wslc, 'run', '--name', required_container]
      command.push('--network', @config.network) if @config.network
      command << '-d' if detach
      command << '-it' if !detach && tty?(true)
      command.concat(options(values)).push(required(values, 'image'))
      command.concat(Shellwords.split(values['command'].to_s)) unless values['command'].to_s.empty?
      command
    end

    def start(detach: false)
      command = [@wslc, 'start', required_container]
      command.push('-a', '-i') unless detach
      command
    end

    def find
      [@wslc, 'list', '--all', '--filter', "name=#{required_container}", '--format', 'json']
    end

    # Mirrors into the volume from a throwaway container. Used for sync.mode:
    # run (compose's default, and mode: container's fallback for when the app
    # container isn't running yet — e.g. just before `up` boots it). The image
    # comes from sync.build's tag (once built via sync_build), else sync.image,
    # else the primary dependencies: entry; compose mode requires one of the
    # first two, since it has no dependencies: entry to fall back to.
    def sync_run
      sync = required_sync
      command = [@wslc, 'run', '--rm']
      sync.volume_specs.each { |spec| command.push('-v', spec) }
      image = sync.build&.fetch('tag') || sync.image || required(primary_values, 'image')
      command.push(image).concat(sync.mirror_command)
    end

    # Builds sync.build's image from a Dockerfile staged in `context` (a caller-
    # managed directory, since the build only reads it once `wslc build` runs).
    # Doesn't touch dependencies: at all, so it works the same under compose
    # mode as it does under container mode.
    def sync_build(context)
      sync = required_sync
      raise ConfigError, 'No sync.build configured in wip.yml' unless sync.build

      [@wslc, 'build', '-t', sync.build['tag'], context]
    end

    # Mirrors from inside the already-running container. Only valid for
    # sync.mode: exec (mode: container's default), since only a container wip
    # itself booted is guaranteed to have both the read-only source mount and
    # the volume attached.
    def sync_exec
      [@wslc, 'exec', required_container, *required_sync.mirror_command]
    end

    def down
      [@wslc, 'stop', required_container]
    end

    # Only reachable under mode: compose-native — mode: compose delegates `wip logs`
    # to the external compose command's own `logs` (ComposeBridge#logs) instead.
    # Single container only, mirroring `wslc`/docker's own `logs`: there's no
    # multi-service log aggregation the way a real compose tool provides.
    def logs(name, follow: true)
      command = [@wslc, 'logs']
      command << '-f' if follow
      command.push(name.to_s)
    end

    def remove
      [@wslc, 'remove', '-f', required_container]
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
      command.concat(options(values, sync: false)).push(required(values, 'image'))
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
      values = primary_values.merge(settings)
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

    def options(values, include_container: false, include_publish: true, sync: true)
      result = scalar_options(values)
      merged_env(values).each { |key, value| result.push('-e', "#{key}=#{value}") }
      result.concat(publish_options(values, sync: sync)) if include_publish
      result << required(values, 'container') if include_container
      result
    end

    def scalar_options(values)
      result = []
      result.push('-w', values['workdir']) unless values['workdir'].to_s.empty?
      result.push('-u', values['user']) unless values['user'].to_s.empty?
      result
    end

    def publish_options(values, sync:)
      result = Array(values['ports']).flat_map { |port| ['-p', port.to_s] }
      volume_specs(values, sync: sync).each { |volume| result.push('-v', volume) }
      result
    end

    # With sync configured, a live bind mount of the target (`.:/app`) is
    # swapped for the read-only source mount plus the named volume, so the
    # running app only ever touches the volume.
    def volume_specs(values, sync: true)
      specs = Array(values['volumes']).map(&:to_s)
      settings = sync ? @config.sync : nil
      return specs unless settings

      specs.reject { |spec| settings.replaces?(spec) } + settings.volume_specs
    end

    # .env supplies defaults; env set in wip.yml (primary or per-command) wins on conflict.
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

    def required_sync
      @config.sync || raise(ConfigError, 'No sync: block configured in wip.yml')
    end

    def dependency_values(name)
      @config.dependency(name) || raise(ConfigError, "Unknown dependency: #{name}")
    end

    def required_container
      container = @config.container
      raise ConfigError, 'container: must be set in wip.yml' if container.to_s.empty?

      container
    end

    def primary_values
      container = required_container
      @config.dependency(container) || raise(ConfigError, "No dependencies.#{container} entry " \
                                                          '(check container: in wip.yml)')
    end
  end
end
