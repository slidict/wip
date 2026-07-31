# frozen_string_literal: true

require 'open3'

module Wip
  # Runs environment diagnostics and reports pass/warn/fail results.
  class Doctor
    Result = Data.define(:level, :message)

    def initialize(loader:, resolver: CommandResolver.new, environment: Environment.new)
      @loader = loader
      @resolver = resolver
      @environment = environment
    end

    def call
      results = []
      results << result(@environment.wsl2? ? :ok : :fail, 'Running on WSL2', 'Not running on WSL2')
      results << result(@environment.windows_interop? ? :ok : :fail, 'Windows executable interoperability is enabled',
                        'Windows executable interoperability is disabled')
      results << Result.new(:ok, "Architecture: #{@environment.architecture}")
      config = load_config(results)
      command = resolve(config, results) if config
      check_version(command, results) if command
      results << result(command_available?('git') ? :ok : :warn, 'Git is available',
                        'Git is not available to the WSLC build environment')
      results
    end

    private

    def load_config(results)
      config = @loader.load
      invalid = %w[container image].select { |key| config.defaults[key].to_s.empty? }
      results << Result.new(invalid.empty? ? :ok : :fail,
                            invalid.empty? ? 'Loaded wip.yml' : "Empty defaults: #{invalid.join(', ')}")
      config
    rescue ConfigError => e
      results << Result.new(:fail, e.message)
      nil
    end

    def resolve(config, results)
      command = @resolver.resolve(config.wslc_command)
      results << Result.new(:ok, "Found #{command}")
      command
    rescue CommandNotFoundError => e
      results << Result.new(:fail, e.message)
      nil
    end

    def check_version(command, results)
      _output, status = Open3.capture2e(command, 'version')
      results << Result.new(status.success? ? :ok : :fail,
                            status.success? ? 'WSLC is available' : 'WSLC version failed')
    rescue Errno::ENOENT
      results << Result.new(:fail, 'WSLC version failed')
    end

    def result(condition, ok_message, fail_message)
      Result.new(condition,
                 condition == :ok ? ok_message : fail_message)
    end

    def command_available?(name) = ENV.fetch('PATH', '').split(File::PATH_SEPARATOR).any? { |dir| File.executable?(File.join(dir, name)) }
  end
end
