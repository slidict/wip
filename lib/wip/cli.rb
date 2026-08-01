# frozen_string_literal: true

require 'thor'
require 'yaml'
require 'json'
require 'stringio'

module Wip
  # Thor-based command-line interface for wip.
  class CLI < Thor
    class_option :config, type: :string, desc: 'Path to wip.yml'
    class_option :debug, type: :boolean, default: false, desc: 'Print progress and timing for each step'
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
      execute(builder.build(settings: load_config.command('build') || {}, extra: extra))
    end

    desc 'up', 'Start the configured container and its dependencies, creating them if necessary'
    option :detach, type: :boolean, default: false, aliases: '-d'
    def up
      ensure_network
      load_config.dependencies.each_key { |name| ensure_dependency(name) }
      ensure_container
    end

    desc 'down', 'Stop and remove the configured container and its dependencies'
    def down
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
      tty = builder.tty?(options[:interactive])
      execute(builder.exec(command, interactive: options[:interactive]), interactive: tty)
    end

    desc 'run COMMAND...', 'Run a command in a new container'
    option :interactive, type: :boolean, default: true
    def run_command(*command)
      tty = builder.tty?(options[:interactive])
      execute(builder.run(command, interactive: options[:interactive]), interactive: tty)
    end
    map 'run' => :run_command

    desc 'shell', 'Open a shell in the configured container'
    def shell_command
      configured = load_config.command('shell')
      if configured
        tty = builder.tty?(configured.fetch('interactive', false))
        return execute(builder.custom('shell', []), interactive: tty)
      end

      tty = builder.tty?(true)
      code = execute(builder.exec(['bash'], settings: { 'interactive' => true }, interactive: true),
                     interactive: tty, exit_on_failure: false)
      return if code.zero?

      execute(builder.exec(['sh'], settings: { 'interactive' => true }, interactive: true), interactive: tty)
    end
    map 'shell' => :shell_command

    desc 'dispatch COMMAND [ARGS...]', 'Run a command defined in wip.yml'
    def dispatch(name = nil, *arguments)
      raise ConfigError, 'A command is required' unless name

      values = load_config.command(name) || raise(ConfigError, "Unknown command: #{name}")
      execute(builder.custom(name, arguments), interactive: builder.tty?(values.fetch('interactive', false)))
    end

    private

    def loader = ConfigLoader.new(path: options[:config])
    def load_config = (@load_config ||= loader.load)
    def resolver = CommandResolver.new
    def builder = CommandBuilder.new(wslc: resolver.resolve(load_config.wslc_command), config: load_config)

    def execute(command, interactive: false, exit_on_failure: true)
      runner = CommandRunner.new(debug: debug?)
      code = reporter.step("running: #{CommandDisplay.for_debug(command)}") do
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
    def reporter = @reporter ||= DebugReporter.new(enabled: debug?)

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

    def ensure_container
      container = load_config.defaults['container']
      interactive = builder.tty?(!options[:detach])
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
