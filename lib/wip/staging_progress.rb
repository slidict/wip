# frozen_string_literal: true

module Wip
  # Prints a self-overwriting "copying build context files: N/total" line, at
  # most every half second, while a slow, silent step (staging a large build
  # context file by file can take tens of seconds) is still moving — so it
  # doesn't read as hung.
  class StagingProgress
    def initialize(out: $stderr, interval: 0.5)
      @out = out
      @interval = interval
      # Backdated so the very first tick always prints immediately, rather
      # than waiting a full interval before the first sign of life.
      @last = Process.clock_gettime(Process::CLOCK_MONOTONIC) - interval
      @printed = false
    end

    def tick(count, total)
      now = Process.clock_gettime(Process::CLOCK_MONOTONIC)
      return if now - @last < @interval && count != total

      @last = now
      @printed = true
      @out.print "\rwip: copying build context files: #{count}/#{total}"
    end

    # Idempotent: a caller can't know in advance whether tick ever fired.
    def finish
      return unless @printed

      @printed = false
      @out.puts
    end
  end
end
