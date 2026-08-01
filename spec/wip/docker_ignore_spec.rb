# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::DockerIgnore do
  subject(:ignore) do
    described_class.new(<<~IGNORE.lines)
      # comment
      node_modules
      tmp/
      *.log
      !important.log
    IGNORE
  end

  it 'ignores files matching a bare pattern at any depth' do
    expect(ignore.ignored?('node_modules/foo.js')).to be true
    expect(ignore.ignored?('vendor/node_modules/foo.js')).to be true
  end

  it 'ignores everything under a directory pattern' do
    expect(ignore.ignored?('tmp/cache/file')).to be true
  end

  it 'ignores files by extension glob' do
    expect(ignore.ignored?('debug.log')).to be true
  end

  it 'lets a later negation win' do
    expect(ignore.ignored?('important.log')).to be false
  end

  it 'does not ignore unrelated files' do
    expect(ignore.ignored?('app/main.rb')).to be false
  end

  it 'is empty for a blank file' do
    expect(described_class.new([]).empty?).to be true
  end
end
