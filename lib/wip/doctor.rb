# frozen_string_literal: true

require 'open3'

module Wip
  # Runs environment diagnostics and reports pass/warn/fail results.
  class Doctor
    Result = Data.define(:level, :message)

    def initialize(loader:, resolver: CommandResolver.new, environment: Environment.new,
                   compose_resolver: CommandResolver.new(candidates: [], label: 'compose command',
                                                         install_hint: ComposeBridge::INSTALL_HINT))
      @loader = loader
      @resolver = resolver
      @environment = environment
      @compose_resolver = compose_resolver
    end

    def call
      results = []
      results << result(@environment.wsl2? ? :ok : :fail, *wsl2_messages)
      results << interop_result unless @environment.windows?
      results << Result.new(:ok, "Architecture: #{@environment.architecture}")
      config = load_config(results)
      check_wslc(config, results) if config
      check_compose(config, results) if config&.compose?
      results << result(command_available?('git') ? :ok : :warn, 'Git is available',
                        'Git is not available to the WSLC build environment')
      results
    end

    private

    def wsl2_messages
      return ['WSL2 is available', 'WSL2 is not available'] if @environment.windows?

      ['Running on WSL2', 'Not running on WSL2']
    end

    def interop_result
      result(@environment.windows_interop? ? :ok : :fail, 'Windows executable interoperability is enabled',
             'Windows executable interoperability is disabled')
    end

    def load_config(results)
      config = @loader.load
      invalid = config.compose? ? [] : %w[container image].select { |key| config.defaults[key].to_s.empty? }
      results << Result.new(invalid.empty? ? :ok : :fail,
                            invalid.empty? ? 'Loaded wip.yml' : "Empty defaults: #{invalid.join(', ')}")
      config
    rescue ConfigError => e
      results << Result.new(:fail, e.message)
      nil
    end

    def check_wslc(config, results)
      command = resolve(config, results)
      check_version(command, results) if command
    end

    def resolve(config, results)
      command = @resolver.resolve(config.wslc_command)
      results << Result.new(:ok, "Found #{command}")
      command
    rescue CommandNotFoundError => e
      results << Result.new(:fail, e.message)
      nil
    end

    def check_version(command, results, label: 'WSLC')
      _output, status = Open3.capture2e(command, 'version')
      results << Result.new(status.success? ? :ok : :fail,
                            status.success? ? "#{label} is available" : "#{label} version failed")
    rescue Errno::ENOENT
      results << Result.new(:fail, "#{label} version failed")
    end

    def check_compose(config, results)
      command = resolve_compose(config, results)
      check_version(command, results, label: 'compose command') if command
      check_compose_file(config, results)
    end

    def resolve_compose(config, results)
      command = @compose_resolver.resolve(config.compose_command)
      results << Result.new(:ok, "Found #{command}")
      command
    rescue CommandNotFoundError => e
      results << Result.new(:fail, e.message)
      nil
    end

    def check_compose_file(config, results)
      path = ComposeBridge.file_path(config)
      results << result(path.file? ? :ok : :fail, "Found compose file #{path}",
                        "Compose file not found: #{path}")
    rescue ConfigError => e
      results << Result.new(:fail, e.message)
    end

    def result(condition, ok_message, fail_message)
      Result.new(condition,
                 condition == :ok ? ok_message : fail_message)
    end

    def command_available?(name) = ENV.fetch('PATH', '').split(File::PATH_SEPARATOR).any? { |dir| File.executable?(File.join(dir, name)) }
  end
end
