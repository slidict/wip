# frozen_string_literal: true

module Wip
  # Periodically prints host CPU/memory/disk-IO/process info while a debug step
  # is still running, so a hung or slow step is visible even before it produces
  # output of its own.
  class ResourceMonitor
    def initialize(interval: 5, out: $stderr)
      @interval = interval
      @out = out
      @last_disk = disk_sectors
      @last_sampled_at = monotonic
    end

    def start(label)
      @thread = Thread.new do
        loop do
          sleep @interval
          @out.puts "wip: [debug] still running (#{snapshot}): #{label}"
        end
      end
    end

    def stop
      @thread&.kill
      @thread&.join
    end

    private

    def monotonic = Process.clock_gettime(Process::CLOCK_MONOTONIC)

    def snapshot
      [load_average, memory, disk_io, top_processes].join(' | ')
    end

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

    # WSL2's bind-mounted (9p) volumes are often the real bottleneck behind a
    # slow `bundle`/`rails` boot, and that shows up here rather than in CPU.
    def disk_io
      now = monotonic
      elapsed = [now - @last_sampled_at, 0.001].max
      current = disk_sectors
      read_kbps = (current[:read] - @last_disk[:read]) * 0.5 / elapsed
      write_kbps = (current[:write] - @last_disk[:write]) * 0.5 / elapsed
      @last_disk = current
      @last_sampled_at = now
      format('io read %<read>.0fKB/s write %<write>.0fKB/s', read: read_kbps, write: write_kbps)
    rescue Errno::ENOENT, IOError
      'io n/a'
    end

    def disk_sectors
      totals = { read: 0, write: 0 }
      File.readlines('/proc/diskstats').each do |line|
        fields = line.split
        next if fields[2].to_s.match?(/\A(loop|ram)/)

        totals[:read] += fields[5].to_i
        totals[:write] += fields[9].to_i
      end
      totals
    rescue Errno::ENOENT, IOError
      { read: 0, write: 0 }
    end

    def top_processes
      lines = `ps -eo pid,pcpu,pmem,comm --sort=-pcpu 2>/dev/null`.lines.drop(1).first(3)
      entries = lines.filter_map do |line|
        pid, cpu, mem, comm = line.split(nil, 4)
        next unless comm

        "#{comm.strip}(#{pid}) cpu #{cpu}%/mem #{mem}%"
      end
      "top: #{entries.join(', ')}"
    rescue StandardError
      'top: n/a'
    end
  end
end
