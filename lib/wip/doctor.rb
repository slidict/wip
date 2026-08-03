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
      check_config(load_config(results), results)
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
      results << Result.new(*container_result(config))
      config
    rescue ConfigError => e
      results << Result.new(:fail, e.message)
      nil
    end

    # dependencies.<container> having an empty image is already a load-time
    # ConfigError (validate_dependency!), so the only way to reach here with a
    # broken primary container is `container:` naming an entry that isn't defined
    # — or, under compose-native, compose.service naming a service compose.yml
    # doesn't actually define.
    def container_result(config)
      return [:ok, 'Loaded wip.yml'] if config.compose? || config.primary
      if config.compose_native?
        return [:fail,
                "compose.service '#{config.container}' has no matching service in compose.yml"]
      end

      [:fail, "No dependencies.#{config.container} entry"]
    end

    def check_config(config, results)
      return unless config

      check_wslc(config, results)
      check_compose(config, results) if config.compose?
      check_compose_native(config, results) if config.compose_native?
      check_sync(config, results) if config.sync?
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

    # Returns whether the file was found, so check_compose_native can skip
    # attempting to parse a file that's already been reported missing.
    def check_compose_file(config, results)
      path = ComposeBridge.file_path(config)
      found = path.file?
      results << result(found ? :ok : :fail, "Found compose file #{path}", "Compose file not found: #{path}")
      found
    rescue ConfigError => e
      results << Result.new(:fail, e.message)
      false
    end

    # mode: compose-native has no external binary to check (check_wslc, above, already
    # covers the one binary it drives) — just that compose.yml exists and parses.
    def check_compose_native(config, results)
      return unless check_compose_file(config, results)

      ComposeFile.load(ComposeBridge.file_path(config))
      results << Result.new(:ok, 'Parsed compose file')
    rescue ConfigError => e
      results << Result.new(:fail, e.message)
    end

    def check_sync(config, results)
      sync = config.sync
      results << result(File.directory?(sync.source) ? :ok : :fail,
                        "Sync source #{sync.source} mirrors into volume #{sync.volume} at #{sync.target}",
                        "Sync source not found: #{sync.source}")
      return unless sync.exec? && (sync.image || sync.build)

      results << Result.new(:warn, 'sync.image/sync.build only cover `wip up`’s one-time pre-boot mirror ' \
                                   '(the primary container isn’t running yet, so that step always uses a ' \
                                   'throwaway container) — sync.mode: exec’s `wip sync`/`wip sync --watch` run ' \
                                   'rsync inside the primary container instead, so its image needs rsync too')
    end

    def result(condition, ok_message, fail_message)
      Result.new(condition,
                 condition == :ok ? ok_message : fail_message)
    end

    def command_available?(name) = ENV.fetch('PATH', '').split(File::PATH_SEPARATOR).any? { |dir| File.executable?(File.join(dir, name)) }
  end
end
