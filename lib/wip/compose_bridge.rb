# frozen_string_literal: true

module Wip
  # Builds argument arrays for wslc-compose invocations, delegating orchestration
  # to a real compose.yml instead of wip's own dependencies:/network handling.
  class ComposeBridge
    FILENAMES = %w[compose.yml compose.yaml docker-compose.yml docker-compose.yaml].freeze
    # No default candidates: wip doesn't favor any one compose-for-wslc implementation.
    # compose.command must name the one you've installed.
    INSTALL_HINT = <<~HINT.chomp
      wip doesn't bundle or pin a compose-for-wslc implementation — install one and set
      compose.command in wip.yml to its binary name or path, e.g.:

        https://github.com/bacarndiaye/wslc-compose
        https://github.com/inuyume/wslc-compose
    HINT

    def self.for(config, resolver: CommandResolver.new(candidates: [], label: 'compose command',
                                                       install_hint: INSTALL_HINT))
      new(compose_command: resolver.resolve(config.compose_command), file: file_path(config),
          project: config.compose_project)
    end

    def self.file_path(config)
      base = Pathname(config.path).dirname
      configured = config.compose_file
      # Relative compose.file is resolved against wip.yml, not the current directory,
      # so `wip` behaves the same from any subdirectory (matching auto-detection below).
      return base.join(configured).expand_path if configured

      FILENAMES.map { |name| base.join(name) }.find(&:file?) ||
        raise(ConfigError, "compose mode: no compose file found next to #{config.path} " \
                           "(looked for #{FILENAMES.join(', ')})")
    end

    def initialize(compose_command:, file:, project: nil)
      @compose_command = compose_command
      @file = file
      @project = project
    end

    def up(detach: true)
      command = base.push('up')
      command << '-d' if detach
      command
    end

    def stop = base.push('stop')

    def down = base.push('down')

    def exec(service, arguments, interactive: true)
      command = base.push('exec')
      command << '-T' unless interactive
      command.push(service.to_s).concat(arguments)
    end

    def logs(services: [], follow: true)
      command = base.push('logs')
      command << '-f' if follow
      command.concat(services.map(&:to_s))
    end

    private

    def base
      command = [@compose_command, '-f', @file.to_s]
      command.push('-p', @project) if @project
      command
    end
  end
end
