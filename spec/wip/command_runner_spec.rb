# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::CommandRunner do
  it 'returns the external command exit status' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 23'])).to eq(23)
  end

  it 'inherits real stdio for interactive commands instead of piping (which would close stdin)' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 7'], interactive: true)).to eq(7)
  end

  it 'returns 130 instead of raising when interrupted mid-command' do
    allow(Open3).to receive(:popen3).and_raise(Interrupt)

    result = nil
    expect { result = described_class.new.run([RbConfig.ruby, '-e', 'exit 0']) }.not_to raise_error
    expect(result).to eq(130)
  end

  it "doesn't let a pump thread's IOError escape when its stream is closed early" do
    source = Object.new
    def source.each(_size)
      raise IOError, 'stream closed in another thread'
    end

    thread = described_class.new.send(:pump, source, StringIO.new, +'')

    expect { thread.join }.not_to raise_error
  end
end
