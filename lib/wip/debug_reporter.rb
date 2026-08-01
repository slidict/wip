# frozen_string_literal: true

module Wip
  # Prints each step wip takes and how long it took, when debug mode is enabled.
  class DebugReporter
    def initialize(enabled:, out: $stderr)
      @enabled = enabled
      @out = out
    end

    def step(label)
      return yield unless @enabled

      started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
      @out.puts "wip: [debug] #{label}"
      monitor = ResourceMonitor.new(out: @out)
      monitor.start(label)
      begin
        yield
      ensure
        monitor.stop
        elapsed = Process.clock_gettime(Process::CLOCK_MONOTONIC) - started
        @out.puts format('wip: [debug] done in %<elapsed>.2fs: %<label>s', elapsed: elapsed, label: label)
      end
    end
  end
end
