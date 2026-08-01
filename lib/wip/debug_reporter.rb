# frozen_string_literal: true

require 'tmpdir'

module Wip
  # Prints each step wip takes and how long it took, when debug mode is enabled.
  class DebugReporter
    # `log:` overrides where resource snapshots go, regardless of `live`:
    #   nil  - automatic (see `step`'s `live:`)
    #   '-'  - always print inline, even for interactive steps
    #   path - always write to this file, even for non-interactive steps
    def initialize(enabled:, out: $stderr, log: nil)
      @enabled = enabled
      @out = out
      @log = log
    end

    # `live: false` is for steps that hand the real TTY to the child process
    # (e.g. `wslc exec -it`). The child owns raw-mode cursor control there, so
    # writing periodic snapshots straight into that same terminal races with
    # it and garbles the output; those snapshots go to a log file instead,
    # unless overridden by `log:` in the constructor.
    def step(label, live: true)
      return yield unless @enabled

      started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
      @out.puts "wip: [debug] #{label}"
      file = log_file(live)
      monitor = ResourceMonitor.new(out: file || @out)
      monitor.start(label)
      begin
        yield
      ensure
        monitor.stop
        file&.close
        elapsed = Process.clock_gettime(Process::CLOCK_MONOTONIC) - started
        @out.puts format('wip: [debug] done in %<elapsed>.2fs: %<label>s', elapsed: elapsed, label: label)
      end
    end

    private

    def log_file(live)
      return nil if @log == '-'
      return open_log(@log, "streaming resource snapshots to #{@log}") if @log
      return nil if live

      path = File.join(Dir.tmpdir, "wip-debug-#{Process.pid}-#{Time.now.to_i}.log")
      open_log(path, "command owns the terminal; streaming resource snapshots to #{path}")
    end

    def open_log(path, notice)
      @out.puts "wip: [debug] #{notice}"
      file = File.open(path, 'a') # rubocop:disable Style/FileOpen -- closed by `step`'s ensure block
      file.sync = true
      file
    end
  end
end
