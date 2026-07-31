# frozen_string_literal: true
require "thor"
require "yaml"

module Wip
  class CLI < Thor
    class_option :config, type: :string, desc: "Path to wip.yml"
    default_task :dispatch

    desc "version", "Show wip and WSLC versions"
    def version
      puts "wip #{VERSION}"
      config = load_config
      command = resolver.resolve(config.wslc_command)
      system(command, "version")
    rescue Error
      nil
    end

    desc "doctor", "Diagnose the development environment"
    def doctor
      results = Doctor.new(loader: loader).call
      results.each { |item| puts "[#{item.level.to_s.upcase}] #{item.message}" }
      exit(1) if results.any? { |item| item.level == :fail }
    end

    desc "config", "Print the effective configuration"
    def config
      puts YAML.dump(load_config.to_h)
    end

    desc "build [OPTIONS]", "Build the configured image"
    def build(*extra)
      extra.shift if extra.first == "--"
      execute(builder.build(settings: load_config.command("build") || {}, extra: extra))
    end

    desc "exec COMMAND...", "Execute a command in the running container"
    option :interactive, type: :boolean, default: true
    def exec(*command)
      execute(builder.exec(command, interactive: options[:interactive]))
    end

    desc "run COMMAND...", "Run a command in a new container"
    option :interactive, type: :boolean, default: true
    def run_command(*command)
      execute(builder.run(command, interactive: options[:interactive]))
    end
    map "run" => :run_command

    desc "shell", "Open a shell in the configured container"
    def shell_command
      configured = load_config.command("shell")
      return execute(builder.custom("shell", [])) if configured

      code = execute(builder.exec(["bash"], settings: { "interactive" => true }, interactive: true))
      execute(builder.exec(["sh"], settings: { "interactive" => true }, interactive: true)) if code != 0
    end
    map "shell" => :shell_command

    desc "dispatch COMMAND [ARGS...]", "Run a command defined in wip.yml"
    def dispatch(name = nil, *arguments)
      raise ConfigError, "A command is required" unless name

      execute(builder.custom(name, arguments))
    end

    private

    def loader = ConfigLoader.new(path: options[:config])
    def load_config = (@loaded_config ||= loader.load)
    def resolver = CommandResolver.new
    def builder = CommandBuilder.new(wslc: resolver.resolve(load_config.wslc_command), config: load_config)

    def execute(command)
      code = CommandRunner.new.run(command)
      exit(code) unless code.zero?
      code
    end
  end
end
