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

  it 'defaults sync.exclude to .git/tmp/node_modules when no template is given' do
    Dir.mktmpdir do |dir|
      parsed = YAML.safe_load(described_class.new(dir: dir).call)
      expect(parsed['sync']['exclude']).to eq(['.git', 'tmp/', 'node_modules/'])
    end
  end

  {
    'rails' => %w[log/ tmp/ storage/],
    'node' => %w[node_modules/ dist/],
    'rust' => %w[target/],
    'csharp' => %w[bin/ obj/]
  }.each do |template, expected_patterns|
    it "picks the #{template} sync.exclude preset for --template #{template}" do
      Dir.mktmpdir do |dir|
        parsed = YAML.safe_load(described_class.new(dir: dir, template: template).call)
        expect(parsed['sync']['exclude']).to include(*expected_patterns)
      end
    end
  end

  it 'rejects an unknown --template' do
    expect { described_class.new(dir: Dir.pwd, template: 'cobol') }.to raise_error(Wip::Error, /cobol/)
  end
end
