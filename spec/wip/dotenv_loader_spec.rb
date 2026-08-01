# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::DotenvLoader do
  it 'returns an empty hash when the file is missing' do
    Dir.mktmpdir do |dir|
      expect(described_class.new(File.join(dir, '.env')).load).to eq({})
    end
  end

  it 'parses key=value pairs, skipping blanks, comments, and export prefixes' do
    Dir.mktmpdir do |dir|
      path = File.join(dir, '.env')
      File.write(path, <<~ENV)
        # comment
        RAILS_ENV=development

        export PORT=3000
        QUOTED="hello world"
        SINGLE='it works'
        WITH_COMMENT=value # trailing comment
      ENV

      expect(described_class.new(path).load).to eq(
        'RAILS_ENV' => 'development',
        'PORT' => '3000',
        'QUOTED' => 'hello world',
        'SINGLE' => 'it works',
        'WITH_COMMENT' => 'value'
      )
    end
  end
end
