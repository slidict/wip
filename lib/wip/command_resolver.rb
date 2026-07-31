# frozen_string_literal: true
module Wip
  class CommandResolver
    CANDIDATES = ["wslc.exe", "wslc", "/mnt/c/Windows/System32/wslc.exe"].freeze

    def initialize(path: ENV.fetch("PATH", ""), executable: nil)
      @path = path
      @executable = executable || method(:executable?)
    end

    def resolve(configured = "auto")
      return configured if configured != "auto" && @executable.call(configured)
      return raise_not_found if configured != "auto"

      CANDIDATES.find { |candidate| @executable.call(candidate) } || raise_not_found
    end

    private

    def executable?(command)
      return File.executable?(command) if command.include?(File::SEPARATOR)

      @path.split(File::PATH_SEPARATOR).any? { |directory| File.executable?(File.join(directory, command)) }
    end

    def raise_not_found
      raise CommandNotFoundError, <<~MESSAGE
        WSLC was not found.

        Checked:
          #{CANDIDATES.join("\n  ")}

        Install or update the WSL container tooling, then run:

          wip doctor
      MESSAGE
    end
  end
end
