# frozen_string_literal: true

require 'open3'
require 'pty'
require 'io/console'

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

    def run(command, env: {}, interactive: false, chdir: nil)
      @stderr.puts "+ #{CommandDisplay.for_debug(command)}" if @debug
      return run_attached(command, env, chdir) if interactive

      opts = chdir ? { chdir: chdir } : {}
      captured = +''
      status = nil
      Open3.popen3(env, *command, opts) do |input, output, error, wait|
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
    # (a shell, `rails console`, ...). Run it behind a pseudo-terminal instead
    # of inheriting wip's real fds directly: a pty still gives the child a
    # genuine controlling terminal (job control, Ctrl-C -> SIGINT, isatty-gated
    # rendering all work the same as direct inheritance), but routes output
    # through wip first, so report_hint can still see it — inherited fds go
    # straight to the terminal and wip never would. wip's own terminal is
    # switched to raw mode for the duration so only the pty's line discipline
    # echoes input; without that, every keystroke would echo twice.
    def run_attached(command, env, chdir)
      opts = chdir ? { chdir: chdir } : {}
      captured = +''
      status = nil
      PTY.spawn(env, *command, opts) do |output, input, pid|
        sync_winsize(output)
        with_raw_stdin { pump_attached(output, input, captured) }
        _, status = Process.wait2(pid)
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

    def pump_attached(output, input, captured)
      stdin_thread = forward_stdin(input)
      loop do
        chunk = output.readpartial(4096)
        @stdout.write(chunk)
        captured << chunk
      end
    rescue Errno::EIO, IOError
      # the pty's slave side closed when the child exited
    ensure
      stdin_thread.kill
    end

    # Keeps the child's pty sized to wip's real terminal so full-screen
    # programs (an editor, `less`, ...) render correctly. A non-tty @stdout
    # (piped output, tests) has no size to read, so the pty keeps its default.
    def sync_winsize(output)
      output.winsize = @stdout.winsize if @stdout.respond_to?(:winsize) && @stdout.tty?
    end

    # A non-tty @stdin (piped input, tests) has no raw mode to switch to, so
    # it's forwarded to the child as-is.
    def with_raw_stdin(&)
      return yield unless @stdin.respond_to?(:raw) && @stdin.tty?

      @stdin.raw(&)
    end

    def forward_stdin(input)
      Thread.new do
        loop do
          chunk = @stdin.readpartial(4096)
          input.write(chunk)
        end
      rescue IOError, Errno::EIO, Errno::EBADF
        nil
      end
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
