# frozen_string_literal: true

require 'thor'
require 'yaml'

module Wip
  # Thor-based command-line interface for wip.
  class CLI < Thor
    class_option :config, type: :string, desc: 'Path to wip.yml'
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

    desc 'up', 'Start the configured container, creating it if necessary'
    option :detach, type: :boolean, default: false, aliases: '-d'
    def up
      code = execute(builder.start(detach: options[:detach]), exit_on_failure: false)
      execute(builder.up(detach: options[:detach])) if code != 0
    end

    desc 'down', 'Stop and remove the configured container'
    def down
      execute(builder.down, exit_on_failure: false)
      execute(builder.remove, exit_on_failure: false)
    end

    desc 'exec COMMAND...', 'Execute a command in the running container'
    option :interactive, type: :boolean, default: true
    def exec(*command)
      execute(builder.exec(command, interactive: options[:interactive]))
    end

    desc 'run COMMAND...', 'Run a command in a new container'
    option :interactive, type: :boolean, default: true
    def run_command(*command)
      execute(builder.run(command, interactive: options[:interactive]))
    end
    map 'run' => :run_command

    desc 'shell', 'Open a shell in the configured container'
    def shell_command
      configured = load_config.command('shell')
      return execute(builder.custom('shell', [])) if configured

      code = execute(builder.exec(['bash'], settings: { 'interactive' => true }, interactive: true),
                     exit_on_failure: false)
      execute(builder.exec(['sh'], settings: { 'interactive' => true }, interactive: true)) if code != 0
    end
    map 'shell' => :shell_command

    desc 'dispatch COMMAND [ARGS...]', 'Run a command defined in wip.yml'
    def dispatch(name = nil, *arguments)
      raise ConfigError, 'A command is required' unless name

      execute(builder.custom(name, arguments))
    end

    private

    def loader = ConfigLoader.new(path: options[:config])
    def load_config = (@load_config ||= loader.load)
    def resolver = CommandResolver.new
    def builder = CommandBuilder.new(wslc: resolver.resolve(load_config.wslc_command), config: load_config)

    def execute(command, exit_on_failure: true)
      code = CommandRunner.new.run(command)
      exit(code) if exit_on_failure && !code.zero?
      code
    end
  end
end
