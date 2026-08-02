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
      expect(parsed['defaults']).to include('container', 'image', 'workdir')
      expect(parsed).to have_key('sync')
    end
  end

  Wip::ComposeBridge::FILENAMES.each do |filename|
    it "detects #{filename} and writes a mode: compose starter" do
      Dir.mktmpdir do |dir|
        File.write(File.join(dir, filename), "services:\n  app:\n    image: example:dev\n")
        initializer = described_class.new(dir: dir)

        expect(initializer.compose?).to be(true)
        parsed = YAML.safe_load(initializer.call)
        expect(parsed).to include('version' => 1, 'mode' => 'compose')
        expect(parsed['compose']).to include('service', 'command')
      end
    end
  end
end
