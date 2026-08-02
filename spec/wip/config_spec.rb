# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::Config do
  it 'applies defaults and converts environment values to strings' do
    config = described_class.new('version' => 1,
                                 'commands' => { 'web' => { 'command' => 'server',
                                                            'env' => { 'PORT' => 3000 } } })
    expect(config.command('web')).to include('container' => 'app', 'workdir' => '/app', 'env' => { 'PORT' => '3000' })
  end

  it 'redacts secret-like settings' do
    config = described_class.new('commands' => { 'x' => { 'command' => 'x', 'env' => { 'API_TOKEN' => 'secret' } } })
    expect(config.to_h.dig('commands', 'x', 'env', 'API_TOKEN')).to eq('[REDACTED]')
  end

  it 'exposes the up command, defaulting to nil' do
    expect(described_class.new({}).up_command).to be_nil
    expect(described_class.new('up' => { 'command' => 'local' }).up_command).to eq('local')
  end

  it 'rejects a non-mapping up section' do
    expect { described_class.new('up' => 'local') }.to raise_error(Wip::ConfigError, /up must be a mapping/)
  end

  it 'exposes dependencies with defaulted settings' do
    config = described_class.new('dependencies' => { 'redis' => { 'image' => 'redis:latest' } })

    expect(config.dependency('redis')).to include('image' => 'redis:latest', 'env' => {}, 'ports' => [],
                                                  'volumes' => [])
    expect(config.dependency('unknown')).to be_nil
  end

  it 'requires dependencies to set an image' do
    expect do
      described_class.new('dependencies' => { 'redis' => { 'command' => 'redis-server' } })
    end.to raise_error(Wip::ConfigError, /dependencies\.redis must set image/)
  end

  it 'exposes defaults.network, defaulting to nil' do
    expect(described_class.new({}).network).to be_nil
    expect(described_class.new('defaults' => { 'network' => 'app-tier' }).network).to eq('app-tier')
  end

  it 'exposes compose settings, defaulting to disabled' do
    config = described_class.new({})
    expect(config.compose?).to be(false)
    expect(config.compose_service).to be_nil

    composed = described_class.new('compose' => { 'service' => 'app', 'file' => 'compose.yml',
                                                  'project' => 'myapp', 'command' => 'my-compose-tool' })
    expect(composed.compose?).to be(true)
    expect(composed.compose_service).to eq('app')
    expect(composed.compose_file).to eq('compose.yml')
    expect(composed.compose_project).to eq('myapp')
    expect(composed.compose_command).to eq('my-compose-tool')
  end

  it 'has no default compose.command: every implementation must be named explicitly' do
    expect(described_class.new({}).compose_command).to be_nil
  end

  it 'requires compose.service' do
    expect do
      described_class.new('compose' => { 'file' => 'compose.yml', 'command' => 'my-compose-tool' })
    end.to raise_error(Wip::ConfigError, /compose\.service must not be empty/)
  end

  it 'requires compose.command' do
    expect do
      described_class.new('compose' => { 'service' => 'app' })
    end.to raise_error(Wip::ConfigError, /compose\.command must not be empty/)
  end

  it 'rejects compose combined with dependencies' do
    expect do
      described_class.new('compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                          'dependencies' => { 'redis' => { 'image' => 'redis:latest' } })
    end.to raise_error(Wip::ConfigError, /compose is mutually exclusive with dependencies/)
  end

  it 'rejects compose combined with defaults.network' do
    expect do
      described_class.new('compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                          'defaults' => { 'network' => 'app-tier' })
    end.to raise_error(Wip::ConfigError, /compose is mutually exclusive with defaults\.network/)
  end
end
