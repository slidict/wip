# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::CommandRunner do
  it 'returns the external command exit status' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 23'])).to eq(23)
  end
end
