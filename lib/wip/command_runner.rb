# frozen_string_literal: true

require 'open3'

module Wip
  # Executes a built command, pumping its I/O and returning the exit status.
  class CommandRunner
    def initialize(stdin: $stdin, stdout: $stdout, stderr: $stderr, interpreter: ErrorInterpreter.new,
                   debug: !ENV['WIP_DEBUG'].to_s.empty?)
      @stdin = stdin
      @stdout = stdout
      @stderr = stderr
      @interpreter = interpreter
      @debug = debug
    end

    def run(command, env: {}, interactive: false)
      @stderr.puts "+ #{CommandDisplay.for_debug(command)}" if @debug
      return run_attached(command, env) if interactive

      captured = +''
      status = nil
      Open3.popen3(env, *command) do |input, output, error, wait|
        input.close
        threads = [pump(output, @stdout, captured), pump(error, @stderr, captured)]
        threads.each(&:join)
        status = wait.value
      end
      hint = @interpreter.interpret(captured)
      @stderr.puts("\n#{hint}") if !status.success? && hint
      status.exitstatus
    rescue Errno::ENOENT => e
      @stderr.puts e.message
      127
    end

    private

    # Piping stdin/stdout/stderr (as `run` does above) closes the child's
    # stdin immediately, which breaks anything that reads from the terminal
    # (a shell, `rails console`, ...). Inherit the real file descriptors
    # instead so the child gets a genuine TTY.
    def run_attached(command, env)
      pid = Process.spawn(env, *command)
      _, status = Process.wait2(pid)
      status.exitstatus
    rescue Errno::ENOENT => e
      @stderr.puts e.message
      127
    end

    def pump(source, destination, captured)
      Thread.new do
        source.each(4096) do |chunk|
          destination.write(chunk)
          captured << chunk
        end
      end
    end
  end
end
