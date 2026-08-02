# frozen_string_literal: true

require 'thor'
require 'yaml'
require 'json'
require 'stringio'
require 'shellwords'

module Wip
  # Thor-based command-line interface for wip.
  class CLI < Thor
    class_option :config, type: :string, desc: 'Path to wip.yml'
    class_option :env_file, type: :string, desc: 'Path to a dotenv file (default: .env next to wip.yml)'
    class_option :debug, type: :boolean, default: false, desc: 'Print progress and timing for each step'
    class_option :debug_log, type: :string, desc: 'Where --debug snapshots go: a file path, or "-" for inline'
    default_task :dispatch

    def self.exit_on_failure? = true

    # Thor only falls back to the default task when no command name is given at
    # all; an unrecognized first argument (e.g. a custom wip.yml command name)
    # would otherwise raise "Could not find command". Route those to `dispatch`.
    def self.dispatch(meth, given_args, given_opts, config)
      name = given_args.first
      if meth.nil? && name && !name.to_s.start_with?('-') && find_command_possibilities(name).empty?
        return super('dispatch', given_args, given_opts, config)
      end

      super
    end

    desc 'version', 'Show wip and WSLC versions'
    def version
      puts "wip #{VERSION}"
      config = load_config
      command = resolver.resolve(config.wslc_command)
      system(command, 'version')
    rescue Error
      nil
    end

    desc 'doctor', 'Diagnose the development environment'
    def doctor
      results = Doctor.new(loader: loader).call
      results.each { |item| puts "[#{item.level.to_s.upcase}] #{item.message}" }
      exit(1) if results.any? { |item| item.level == :fail }
    end

    desc 'config', 'Print the effective configuration'
    def config
      puts YAML.dump(load_config.to_h)
    end

    desc 'build [OPTIONS]', 'Build the configured image'
    def build(*extra)
      extra.shift if extra.first == '--'
      settings = load_config.command('build') || {}
      context = settings['context'] || load_config.defaults['context'] || '.'
      BuildContext.new(context).stage do |staged_context|
        execute(builder.build(settings: settings.merge('context' => staged_context), extra: extra))
      end
    end

    desc 'up', 'Start the configured container and its dependencies, creating them if necessary'
    option :detach, type: :boolean, default: false, aliases: '-d'
    option :sync, type: :boolean, default: true, desc: 'Mirror the source into the sync volume first (--no-sync skips)'
    def up
      if load_config.compose?
        return execute(compose_bridge.up(detach: options[:detach]), interactive: tty?(!options[:detach]))
      end

      ensure_network
      load_config.dependencies.each_key { |name| ensure_dependency(name) }
      sync_before_boot if options[:sync]
      ensure_container
    end

    desc 'sync', 'Mirror the source tree into the sync volume'
    option :watch, type: :boolean, default: false, aliases: '-w', desc: 'Keep re-syncing until interrupted'
    option :interval, type: :numeric, desc: 'Seconds between syncs when watching (default: sync.interval)'
    def sync
      settings = sync_settings!
      warn_shadowed_command('sync')
      return run_sync unless options[:watch]

      interval = watch_interval(settings)
      warn "wip: syncing #{settings.source} -> #{settings.volume}:#{settings.target} " \
           "every #{interval}s (Ctrl-C to stop)"
      loop do
        run_sync(exit_on_failure: false)
        sleep interval
      end
    rescue Interrupt
      warn "\nwip: sync stopped"
    end

    desc 'down', 'Stop and remove the configured container and its dependencies'
    def down
      return execute(compose_bridge.down, exit_on_failure: false) if load_config.compose?

      execute(builder.down, exit_on_failure: false)
      execute(builder.remove, exit_on_failure: false)
      load_config.dependencies.each_key do |name|
        execute(builder.dependency_down(name), exit_on_failure: false)
        execute(builder.dependency_remove(name), exit_on_failure: false)
      end
    end

    desc 'exec COMMAND...', 'Execute a command in the running container'
    option :interactive, type: :boolean, default: true
    def exec(*command)
      execute(exec_target(command, interactive: options[:interactive]), interactive: tty?(options[:interactive]))
    end

    desc 'run COMMAND...', 'Run a command in a new container'
    option :interactive, type: :boolean, default: true
    def run_command(*command)
      if load_config.compose?
        warn "wip: compose mode has no ephemeral 'run'; executing in the running " \
             "'#{load_config.compose_service}' service instead"
        return execute(exec_target(command, interactive: options[:interactive]),
                       interactive: tty?(options[:interactive]))
      end

      execute(builder.run(command, interactive: options[:interactive]), interactive: tty?(options[:interactive]))
    end
    map 'run' => :run_command

    desc 'shell', 'Open a shell in the configured container'
    def shell_command
      configured = load_config.command('shell')
      return dispatch('shell') if configured

      code = execute(exec_target(['bash'], interactive: true), interactive: tty?(true), exit_on_failure: false)
      return if code.zero?

      execute(exec_target(['sh'], interactive: true), interactive: tty?(true))
    end
    map 'shell' => :shell_command

    desc 'logs [SERVICE...]', 'Follow logs from compose services (compose mode only)'
    option :follow, type: :boolean, default: true, aliases: '-f'
    def logs(*services)
      raise ConfigError, '`wip logs` is only available in compose mode' unless load_config.compose?

      execute(compose_bridge.logs(services: services, follow: options[:follow]), interactive: true)
    end

    desc 'dispatch COMMAND [ARGS...]', 'Run a command defined in wip.yml'
    def dispatch(name = nil, *arguments)
      raise ConfigError, 'A command is required' unless name

      values = load_config.command(name) || raise(ConfigError, "Unknown command: #{name}")
      return dispatch_compose(name, values, arguments) if load_config.compose?

      execute(builder.custom(name, arguments), interactive: tty?(values.fetch('interactive', false)))
    end

    private

    def loader = ConfigLoader.new(path: options[:config])
    def load_config = (@load_config ||= loader.load)
    def resolver = CommandResolver.new

    def dotenv_path
      options[:env_file] ? Pathname(options[:env_file]).expand_path : Pathname(load_config.path).dirname.join('.env')
    end

    def dotenv = @dotenv ||= DotenvLoader.new(dotenv_path).load

    def builder
      CommandBuilder.new(wslc: resolver.resolve(load_config.wslc_command), config: load_config, dotenv: dotenv)
    end

    def compose_bridge = @compose_bridge ||= ComposeBridge.for(load_config)

    def tty?(requested) = requested && Environment.new.interactive?

    def exec_target(arguments, interactive:)
      if load_config.compose?
        return compose_bridge.exec(load_config.compose_service, arguments,
                                   interactive: interactive)
      end

      builder.exec(arguments, interactive: interactive)
    end

    def dispatch_compose(name, values, arguments)
      type = values['type'] || 'exec'
      unless type == 'exec'
        raise ConfigError, "commands.#{name}: type '#{type}' is not supported in compose mode " \
                           '(use `wslc-compose build`/`up --build` directly)'
      end

      command = Shellwords.split(values['command'].to_s) + arguments
      interactive = values.fetch('interactive', false)
      execute(exec_target(command, interactive: interactive), interactive: tty?(interactive))
    end

    def execute(command, interactive: false, exit_on_failure: true)
      runner = CommandRunner.new(debug: debug?)
      code = reporter.step("running: #{CommandDisplay.for_debug(command)}", live: !interactive) do
        runner.run(command, interactive: interactive)
      end
      exit(code) if exit_on_failure && !code.zero?
      code
    end

    def resource_exists?(find_command)
      code, output = probe(find_command)
      code.zero? && !JSON.parse(output).empty?
    rescue JSON::ParserError
      false
    end

    def probe(command)
      out = StringIO.new
      runner = CommandRunner.new(stdout: out, stderr: StringIO.new, debug: debug?)
      code = reporter.step("checking: #{CommandDisplay.for_debug(command)}") { runner.run(command) }
      [code, out.string]
    end

    def debug? = options[:debug] || !ENV['WIP_DEBUG'].to_s.empty?
    def reporter = @reporter ||= DebugReporter.new(enabled: debug?, log: options[:debug_log])

    def ensure_network
      network = load_config.network
      return unless network
      return if network_exists?(network)

      warn "wip: creating network '#{network}'"
      execute(builder.network_create, exit_on_failure: false)
    end

    def network_exists?(network)
      code, output = probe(builder.network_list)
      code.zero? && JSON.parse(output).any? { |entry| entry['Name'] == network }
    rescue JSON::ParserError
      false
    end

    def ensure_dependency(name)
      if resource_exists?(builder.dependency_find(name))
        warn "wip: starting existing dependency '#{name}'"
        execute(builder.dependency_start(name))
      else
        warn "wip: dependency '#{name}' not found, creating it"
        execute(builder.dependency_up(name))
      end
    end

    def sync_settings!
      load_config.sync || raise(ConfigError, '`wip sync` needs a sync: block in wip.yml')
    end

    # `sync.interval` is validated when the config loads; --interval isn't, and
    # a negative one would only surface as an ArgumentError from `sleep`.
    def watch_interval(settings)
      interval = options[:interval] || settings.interval
      raise ConfigError, '--interval must be a positive number' unless interval.positive?

      interval
    end

    # A built-in command wins over a `commands:` entry of the same name, so
    # point at `wip dispatch` rather than letting the custom one vanish.
    def warn_shadowed_command(name)
      return unless load_config.commands.key?(name)

      warn "wip: commands.#{name} in wip.yml is shadowed by the built-in `wip #{name}`; " \
           "run it with `wip dispatch #{name}`"
    end

    # Inside the running container the mirror is a plain `exec`; otherwise it
    # takes a throwaway container that mounts the same source and volume.
    def run_sync(exit_on_failure: true)
      command = container_running? ? builder.sync_exec : builder.sync_run
      execute(command, exit_on_failure: exit_on_failure)
    end

    def container_running? = resource_exists?(builder.find_running)

    def sync_before_boot
      settings = load_config.sync
      return unless settings

      warn "wip: syncing #{settings.source} -> #{settings.volume}:#{settings.target}"
      execute(builder.sync_run)
      warn "wip: run `wip sync --watch` in another terminal to keep #{settings.target} up to date"
    end

    def ensure_container
      container = load_config.defaults['container']
      interactive = tty?(!options[:detach])
      if resource_exists?(builder.find)
        warn "wip: starting existing container '#{container}'"
        execute(builder.start(detach: options[:detach]), interactive: interactive)
      else
        warn "wip: container '#{container}' not found, creating it"
        execute(builder.up(detach: options[:detach]), interactive: interactive)
      end
    end
  end
end
