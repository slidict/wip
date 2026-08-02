# frozen_string_literal: true

require 'spec_helper'
require 'fileutils'

RSpec.describe Wip::ComposeBridge do
  subject(:bridge) { described_class.new(compose_command: 'wslc-compose', file: 'compose.yml') }

  it 'builds a detached up command' do
    expect(bridge.up).to eq(%w[wslc-compose -f compose.yml up -d])
  end

  it 'builds an attached up command' do
    expect(bridge.up(detach: false)).to eq(%w[wslc-compose -f compose.yml up])
  end

  it 'builds a down command' do
    expect(bridge.down).to eq(%w[wslc-compose -f compose.yml down])
  end

  it 'builds an interactive exec command' do
    expect(bridge.exec('app', %w[bin/rails c])).to eq(%w[wslc-compose -f compose.yml exec app bin/rails c])
  end

  it 'builds a non-interactive exec command' do
    expect(bridge.exec('app', %w[bundle install], interactive: false))
      .to eq(%w[wslc-compose -f compose.yml exec -T app bundle install])
  end

  it 'builds a following logs command for all services by default' do
    expect(bridge.logs).to eq(%w[wslc-compose -f compose.yml logs -f])
  end

  it 'builds a non-following logs command scoped to services' do
    expect(bridge.logs(services: %w[app redis], follow: false))
      .to eq(%w[wslc-compose -f compose.yml logs app redis])
  end

  it 'includes the project name when configured' do
    scoped = described_class.new(compose_command: 'wslc-compose', file: 'compose.yml', project: 'myapp')
    expect(scoped.down).to eq(%w[wslc-compose -f compose.yml -p myapp down])
  end

  describe '.file_path' do
    around do |example|
      Dir.mktmpdir { |dir| Dir.chdir(dir) { example.run } }
    end

    it 'uses the explicit compose.file override when set' do
      config = Wip::Config.new({ 'mode' => 'compose', 'compose' => { 'service' => 'app', 'file' => 'custom.yml',
                                                                     'command' => 'my-compose-tool' } }, 'wip.yml')
      expect(described_class.file_path(config)).to eq(Pathname('custom.yml').expand_path)
    end

    it 'resolves a relative compose.file against wip.yml, not the current directory' do
      FileUtils.mkdir_p('project/config')
      config = Wip::Config.new({ 'mode' => 'compose', 'compose' => { 'service' => 'app', 'file' => 'config/custom.yml',
                                                                     'command' => 'my-compose-tool' } },
                               File.expand_path('project/wip.yml'))
      expected = Pathname(File.expand_path('project/config/custom.yml'))

      Dir.chdir('project/config') { expect(described_class.file_path(config)).to eq(expected) }
    end

    it 'keeps an absolute compose.file as-is' do
      absolute = File.expand_path('elsewhere/custom.yml')
      config = Wip::Config.new({ 'mode' => 'compose', 'compose' => { 'service' => 'app', 'file' => absolute,
                                                                     'command' => 'my-compose-tool' } },
                               File.expand_path('project/wip.yml'))
      expect(described_class.file_path(config)).to eq(Pathname(absolute))
    end

    it 'auto-detects a compose file next to wip.yml' do
      File.write('compose.yaml', "services:\n  app:\n    image: example:dev\n")
      config = Wip::Config.new({ 'mode' => 'compose',
                                 'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' } },
                               File.expand_path('wip.yml'))
      expect(described_class.file_path(config)).to eq(Pathname(File.expand_path('compose.yaml')))
    end

    it 'raises when no compose file can be found' do
      config = Wip::Config.new({ 'mode' => 'compose',
                                 'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' } },
                               File.expand_path('wip.yml'))
      expect { described_class.file_path(config) }.to raise_error(Wip::ConfigError, /no compose file found/)
    end
  end

  describe '.for' do
    it 'resolves the compose command and builds a bridge for the config' do
      config = Wip::Config.new({ 'mode' => 'compose',
                                 'compose' => { 'service' => 'app', 'file' => 'compose.yml', 'project' => 'myapp',
                                                'command' => 'wslc-compose' } }, 'wip.yml')
      resolver = instance_double(Wip::CommandResolver, resolve: 'wslc-compose')

      bridge = described_class.for(config, resolver: resolver)

      expect(bridge.down).to eq(['wslc-compose', '-f', Pathname('compose.yml').expand_path.to_s, '-p', 'myapp',
                                 'down'])
    end

    it 'resolves whatever compose.command names, not a hardcoded implementation' do
      config = Wip::Config.new({ 'mode' => 'compose', 'compose' => { 'service' => 'app', 'file' => 'compose.yml',
                                                                     'command' => 'my-compose-tool' } }, 'wip.yml')
      resolver = instance_double(Wip::CommandResolver)
      expect(resolver).to receive(:resolve).with('my-compose-tool').and_return('my-compose-tool')

      described_class.for(config, resolver: resolver)
    end

    it 'reports the configured command, not a default candidate list, when nothing is found' do
      config = Wip::Config.new({ 'mode' => 'compose', 'compose' => { 'service' => 'app', 'file' => 'compose.yml',
                                                                     'command' => 'my-compose-tool' } }, 'wip.yml')
      resolver = Wip::CommandResolver.new(executable: ->(_name) { false }, candidates: [],
                                          label: 'compose command')

      expect { described_class.for(config, resolver: resolver) }
        .to raise_error(Wip::CommandNotFoundError, /Checked:\s*my-compose-tool/)
    end
  end
end
