# frozen_string_literal: true

require 'spec_helper'
require 'tmpdir'
require 'yaml'

RSpec.describe Wip::Initializer do
  it 'writes a mode: container starter when no compose file is present' do
    Dir.mktmpdir do |dir|
      initializer = described_class.new(dir: dir)

      expect(initializer.compose?).to be(false)
      parsed = YAML.safe_load(initializer.call)
      expect(parsed).to include('version' => 1, 'mode' => 'container')
      expect(parsed['dependencies']['app']).to include('image', 'workdir')
      expect(parsed).to have_key('sync')
    end
  end

  Wip::ComposeBridge::FILENAMES.each do |filename|
    it "detects #{filename} and writes a mode: compose-native starter" do
      Dir.mktmpdir do |dir|
        File.write(File.join(dir, filename), "services:\n  app:\n    image: example:dev\n")
        initializer = described_class.new(dir: dir)

        expect(initializer.compose?).to be(true)
        output = initializer.call
        parsed = YAML.safe_load(output)
        expect(parsed).to include('version' => 1, 'mode' => 'compose-native')
        expect(parsed['compose']).to include('service')
        expect(parsed['compose']).not_to have_key('command')
        expect(output).to include('mode: compose').and include(filename)
        expect(parsed).to have_key('sync')
      end
    end
  end
end
