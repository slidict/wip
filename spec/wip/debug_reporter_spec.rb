# frozen_string_literal: true

require 'spec_helper'
require 'tmpdir'
require 'stringio'

RSpec.describe Wip::DebugReporter do
  it 'runs the block and returns its value when disabled, printing nothing' do
    reporter = described_class.new(enabled: false)
    expect(reporter.step('label') { 42 }).to eq(42)
  end

  it 'prints start/done lines directly to `out` for a live step' do
    out = StringIO.new
    reporter = described_class.new(enabled: true, out: out)
    monitor = instance_double(Wip::ResourceMonitor, start: nil, stop: nil)
    allow(Wip::ResourceMonitor).to receive(:new).with(out: out).and_return(monitor)

    reporter.step('doing work', live: true) { :done }

    expect(out.string).to include('[debug] doing work').and include('[debug] done in')
  end

  it 'redirects a non-live step to a log file by default and reports its path' do
    Dir.mktmpdir do |dir|
      allow(Dir).to receive(:tmpdir).and_return(dir)
      out = StringIO.new
      reporter = described_class.new(enabled: true, out: out)

      reporter.step('interactive work', live: false) { :done }

      expect(out.string).to match(%r{streaming resource snapshots to #{Regexp.escape(dir)}/wip-debug-.*\.log})
      expect(Dir.glob(File.join(dir, 'wip-debug-*.log'))).not_to be_empty
    end
  end

  it '--debug-log=- forces snapshots inline even for a non-live step' do
    out = StringIO.new
    reporter = described_class.new(enabled: true, out: out, log: '-')

    reporter.step('interactive work', live: false) { :done }

    expect(out.string).not_to include('streaming resource snapshots')
  end

  it '--debug-log=PATH forces snapshots to that file even for a live step' do
    Dir.mktmpdir do |dir|
      path = File.join(dir, 'custom.log')
      out = StringIO.new
      reporter = described_class.new(enabled: true, out: out, log: path)

      reporter.step('non-interactive work', live: true) { :done }

      expect(out.string).to include("streaming resource snapshots to #{path}")
      expect(File).to exist(path)
    end
  end
end
