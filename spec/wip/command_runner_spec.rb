# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::CommandRunner do
  it 'returns the external command exit status' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 23'])).to eq(23)
  end

  it 'inherits real stdio for interactive commands instead of piping (which would close stdin)' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 7'], interactive: true)).to eq(7)
  end
end
