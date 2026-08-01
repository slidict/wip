# frozen_string_literal: true

module Wip
  # Periodically prints host CPU/memory/process info while a debug step is still
  # running, so a hung or slow step is visible even before it produces output.
  class ResourceMonitor
    def initialize(interval: 5, out: $stderr)
      @interval = interval
      @out = out
    end

    def start(label)
      @thread = Thread.new do
        loop do
          sleep @interval
          @out.puts "wip: [debug] still running (#{load_average} | #{memory} | top: #{top_processes}): #{label}"
        end
      end
    end

    def stop
      @thread&.kill
      @thread&.join
    end

    private

    def load_average
      "load #{File.read('/proc/loadavg').split[0..2].join(' ')}"
    rescue Errno::ENOENT, IOError
      'load n/a'
    end

    def memory
      fields = File.read('/proc/meminfo').lines.to_h { |line| line.split(':', 2).then { |k, v| [k, v.to_i] } }
      total_gb = fields.fetch('MemTotal', 0) / 1_048_576.0
      used_gb = total_gb - (fields.fetch('MemAvailable', 0) / 1_048_576.0)
      format('mem %<used>.1fG/%<total>.1fG', used: used_gb, total: total_gb)
    rescue Errno::ENOENT, IOError
      'mem n/a'
    end

    def top_processes
      `ps -eo pid,pcpu,pmem,comm --sort=-pcpu 2>/dev/null`.lines.drop(1).first(3).filter_map do |line|
        pid, cpu, mem, comm = line.split(nil, 4)
        next unless comm

        "#{comm.strip}(#{pid}) cpu #{cpu}%/mem #{mem}%"
      end.join(', ')
    rescue StandardError
      'n/a'
    end
  end
end
