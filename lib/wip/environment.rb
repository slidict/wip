# frozen_string_literal: true

module Wip
  # Detects WSL2, Windows interop, and architecture facts about the host.
  class Environment
    def initialize(stdin: $stdin, stdout: $stdout)
      @stdin = stdin
      @stdout = stdout
    end

    def wsl2?
      File.read('/proc/version').match?(/microsoft.*WSL2/i)
    rescue Errno::ENOENT
      false
    end

    def windows_interop? = File.executable?('/proc/sys/fs/binfmt_misc/WSLInterop') || ENV.key?('WSL_INTEROP')
    def interactive? = @stdin.tty? && @stdout.tty?

    def architecture
      machine = `uname -m`.strip
      { 'x86_64' => 'linux/amd64', 'aarch64' => 'linux/arm64', 'arm64' => 'linux/arm64' }.fetch(machine,
                                                                                                "linux/#{machine}")
    end
  end
end
