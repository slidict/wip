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
      report_hint(captured) unless status.success?
      exitstatus(status)
    rescue Errno::ENOENT => e
      @stderr.puts e.message
      127
    rescue Interrupt
      @stderr.puts "\nwip: interrupted"
      130
    end

    private

    # exitstatus is nil when the process was killed by a signal instead of
    # exiting normally; fall back to the conventional 128+signal shell code
    # so callers always get a comparable integer.
    def exitstatus(status)
      status.exitstatus || (128 + status.termsig)
    end

    def report_hint(captured)
      hint = @interpreter.interpret(captured.force_encoding(Encoding::UTF_8).scrub)
      @stderr.puts("\n#{hint}") if hint
    end

    # Piping stdin/stdout/stderr (as `run` does above) closes the child's
    # stdin immediately, which breaks anything that reads from the terminal
    # (a shell, `rails console`, ...). Inherit the real file descriptors
    # instead so the child gets a genuine TTY.
    def run_attached(command, env)
      pid = Process.spawn(env, *command)
      _, status = Process.wait2(pid)
      exitstatus(status)
    rescue Errno::ENOENT => e
      @stderr.puts e.message
      127
    end

    # An Interrupt during the main thread's `join` closes these pipes out from
    # under a still-reading pump thread; treat that as a normal stop rather
    # than an uncaught background-thread exception. Only the read from
    # `source` is rescued, so a genuine write failure on `destination` still
    # surfaces.
    def pump(source, destination, captured)
      Thread.new do
        loop do
          chunk = begin
            source.readpartial(4096)
          rescue IOError # includes EOFError, raised at end of stream
            break
          end
          destination.write(chunk)
          captured << chunk
        end
      end
    end
  end
end
