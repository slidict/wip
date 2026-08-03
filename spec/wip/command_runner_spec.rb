# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::CommandRunner do
  it 'returns the external command exit status' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 23'])).to eq(23)
  end

  it 'inherits real stdio for interactive commands instead of piping (which would close stdin)' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 7'], interactive: true)).to eq(7)
  end

  it 'returns 130 and reports the interrupt instead of raising when Ctrl-C hits while waiting on pump threads' do
    raised = false
    allow_any_instance_of(Thread).to receive(:join).and_wrap_original do |original, *args|
      next original.call(*args) if raised

      raised = true
      raise Interrupt
    end

    stderr = StringIO.new
    runner = described_class.new(stderr: stderr)
    result = nil
    command = [RbConfig.ruby, '-e', 'puts "x" * 4096; sleep 0.1']

    expect { result = runner.run(command) }.not_to raise_error
    expect(result).to eq(130)
    expect(stderr.string).to include('wip: interrupted')
  end

  it 'interprets output containing invalid UTF-8 bytes instead of raising an encoding error' do
    stderr = StringIO.new
    runner = described_class.new(stderr: stderr)
    script = <<~'RUBY'
      STDOUT.binmode
      STDOUT.write("\xFF\xFE".b)
      STDOUT.write('too many mounted volumes')
      exit 1
    RUBY

    expect {
      runner.run([RbConfig.ruby, '-e', script])
    }.not_to raise_error
    expect(stderr.string).to include('mounted-volume limit')
  end

  it "doesn't let a pump thread's IOError escape when its stream is closed early" do
    source = Object.new
    def source.readpartial(_size)
      raise IOError, 'stream closed in another thread'
    end

    thread = described_class.new.send(:pump, source, StringIO.new, +'')

    expect { thread.join }.not_to raise_error
  end
end
