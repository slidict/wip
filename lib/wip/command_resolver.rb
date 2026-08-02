# frozen_string_literal: true

module Wip
  # Locates an executable on the current system, trying a list of candidates.
  class CommandResolver
    CANDIDATES = ['wslc.exe', 'wslc', '/mnt/c/Windows/System32/wslc.exe'].freeze
    DEFAULT_INSTALL_HINT = <<~HINT.chomp
      Install or update the WSL container tooling, then run:

        wip doctor
    HINT

    def initialize(path: ENV.fetch('PATH', ''), executable: nil, candidates: CANDIDATES, label: 'WSLC',
                   install_hint: DEFAULT_INSTALL_HINT)
      @path = path
      @executable = executable || method(:executable?)
      @candidates = candidates
      @label = label
      @install_hint = install_hint
    end

    def resolve(configured = 'auto')
      return configured if configured != 'auto' && @executable.call(configured)
      return raise_not_found([configured]) if configured != 'auto'

      @candidates.find { |candidate| @executable.call(candidate) } || raise_not_found(@candidates)
    end

    private

    def executable?(command)
      return File.executable?(command) if command.include?(File::SEPARATOR)

      @path.split(File::PATH_SEPARATOR).any? { |directory| File.executable?(File.join(directory, command)) }
    end

    def raise_not_found(attempted)
      raise CommandNotFoundError, <<~MESSAGE
        #{@label} was not found.

        Checked:
          #{attempted.join("\n  ")}

        #{@install_hint}
      MESSAGE
    end
  end
end
