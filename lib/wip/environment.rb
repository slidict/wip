# frozen_string_literal: true

require 'open3'

module Wip
  # Detects WSL2, Windows interop, and architecture facts about the host.
  class Environment
    def initialize(stdin: $stdin, stdout: $stdout)
      @stdin = stdin
      @stdout = stdout
    end

    def windows? = Gem.win_platform?

    def wsl2?
      return wsl2_backend_available? if windows?

      File.read('/proc/version').match?(/microsoft.*WSL2/i)
    rescue Errno::ENOENT
      false
    end

    def windows_interop? = File.executable?('/proc/sys/fs/binfmt_misc/WSLInterop') || ENV.key?('WSL_INTEROP')
    def interactive? = @stdin.tty? && @stdout.tty?

    def architecture
      machine = RbConfig::CONFIG['host_cpu']
      { 'x86_64' => 'linux/amd64', 'x64' => 'linux/amd64', 'aarch64' => 'linux/arm64', 'arm64' => 'linux/arm64' }
        .fetch(machine, "linux/#{machine}")
    end

    private

    # On native Windows there is no /proc/version to read, so ask Windows
    # itself whether the WSL2 backend that WSLC depends on is installed.
    def wsl2_backend_available?
      _output, status = Open3.capture2e('wsl.exe', '--status')
      status.success?
    rescue Errno::ENOENT
      false
    end
  end
end
