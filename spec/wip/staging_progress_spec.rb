# frozen_string_literal: true

require 'spec_helper'
require 'stringio'

RSpec.describe Wip::StagingProgress do
  it 'prints a count/total line, throttled to at most one per interval' do
    out = StringIO.new
    clock = [0.0]
    allow(Process).to(receive(:clock_gettime).and_wrap_original { clock.first })
    progress = described_class.new(out: out, interval: 1)

    progress.tick(1, 10)
    expect(out.string).to eq("\rwip: copying build context files: 1/10")

    clock[0] = 0.5
    progress.tick(2, 10)
    expect(out.string).to eq("\rwip: copying build context files: 1/10")

    clock[0] = 1.5
    progress.tick(3, 10)
    expect(out.string).to eq("\rwip: copying build context files: 1/10\rwip: copying build context files: 3/10")
  end

  it 'always prints the final count/total, even mid-interval' do
    out = StringIO.new
    progress = described_class.new(out: out, interval: 100)

    progress.tick(1, 2)
    progress.tick(2, 2)

    expect(out.string).to include('2/2')
  end

  it 'prints a trailing newline on finish only if it ever ticked, and finish is idempotent' do
    out = StringIO.new
    progress = described_class.new(out: out)

    progress.finish
    expect(out.string).to eq('')

    progress.tick(1, 1)
    progress.finish
    expect(out.string).to end_with("\n")

    before = out.string
    progress.finish
    expect(out.string).to eq(before)
  end
end
