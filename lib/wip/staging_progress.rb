# frozen_string_literal: true

module Wip
  # Prints a self-overwriting "copying build context files: N/total" line
  # every half second while a slow, silent step is running. Reporting runs on
  # its own thread so a single large file does not make progress appear hung.
  class StagingProgress
    def initialize(out: $stderr, interval: 0.5)
      @out = out
      @interval = interval
      @mutex = Mutex.new
      @condition = ConditionVariable.new
      @worker = nil
      @stopped = false
      @printed = false
    end

    def tick(count, total)
      @mutex.synchronize do
        @count = count
        @total = total
        print_progress if @worker.nil? || count == total
        @worker ||= Thread.new { report_on_interval }
      end
    end

    # Idempotent: a caller can't know in advance whether tick ever fired.
    def finish
      worker = @mutex.synchronize do
        return unless @worker

        @stopped = true
        @condition.broadcast
        @worker
      end
      worker.join

      @mutex.synchronize do
        @worker = nil
        @printed = false
        @out.puts
      end
    end

    private

    def report_on_interval
      @mutex.synchronize do
        until @stopped
          @condition.wait(@mutex, @interval)
          print_progress unless @stopped
        end
      end
    end

    def print_progress
      @printed = true
      @out.print "\rwip: copying build context files: #{@count}/#{@total}"
    end
  end
end
