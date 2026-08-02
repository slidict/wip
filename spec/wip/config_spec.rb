# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::Config do
  it 'merges custom commands onto the primary container and converts environment values to strings' do
    config = described_class.new('version' => 1, 'container' => 'app',
                                 'dependencies' => { 'app' => { 'image' => 'example:dev', 'workdir' => '/app' } },
                                 'commands' => { 'web' => { 'command' => 'server',
                                                            'env' => { 'PORT' => 3000 } } })
    expect(config.command('web')).to include('image' => 'example:dev', 'workdir' => '/app',
                                             'env' => { 'PORT' => '3000' })
  end

  it 'redacts secret-like settings' do
    config = described_class.new('commands' => { 'x' => { 'command' => 'x', 'env' => { 'API_TOKEN' => 'secret' } } })
    expect(config.to_h.dig('commands', 'x', 'env', 'API_TOKEN')).to eq('[REDACTED]')
  end

  it 'exposes container and the primary dependency it points at, with no implicit default' do
    config = described_class.new('container' => 'app',
                                 'dependencies' => { 'app' => { 'image' => 'example:dev', 'command' => 'local' } })
    expect(config.container).to eq('app')
    expect(config.primary).to include('image' => 'example:dev', 'command' => 'local')
    expect(described_class.new({}).container).to be_nil
  end

  it 'requires container: once dependencies: has entries, rather than guessing a name' do
    expect { described_class.new('dependencies' => { 'app' => { 'image' => 'example:dev' } }) }
      .to raise_error(Wip::ConfigError, /container: must be set when dependencies: has entries/)
  end

  it 'lets container point at a differently-named dependency' do
    config = described_class.new('container' => 'web', 'dependencies' => { 'web' => { 'image' => 'example:dev' } })
    expect(config.primary).to include('image' => 'example:dev')
  end

  it 'has no primary entry when the pointed-at dependency is missing' do
    expect(described_class.new({}).primary).to be_nil
    missing = described_class.new('container' => 'app',
                                  'dependencies' => { 'redis' => { 'image' => 'redis:latest' } })
    expect(missing.primary).to be_nil
  end

  it 'exposes dependencies with defaulted settings, uniformly for the primary and sidecars' do
    config = described_class.new('container' => 'redis',
                                 'dependencies' => { 'redis' => { 'image' => 'redis:latest' } })

    expect(config.dependency('redis')).to include('image' => 'redis:latest', 'workdir' => nil,
                                                  'interactive' => false, 'remove' => true,
                                                  'env' => {}, 'ports' => [], 'volumes' => [])
    expect(config.dependency('unknown')).to be_nil
  end

  it 'requires dependencies to set an image' do
    expect do
      described_class.new('container' => 'redis', 'dependencies' => { 'redis' => { 'command' => 'redis-server' } })
    end.to raise_error(Wip::ConfigError, /dependencies\.redis must set image/)
  end

  it 'exposes network, defaulting to nil' do
    expect(described_class.new({}).network).to be_nil
    expect(described_class.new('network' => 'app-tier').network).to eq('app-tier')
  end

  it 'exposes mode, defaulting to container' do
    expect(described_class.new({}).mode).to eq('container')
    expect(described_class.new('mode' => 'compose',
                               'compose' => { 'service' => 'app', 'command' => 'c' }).mode).to eq('compose')
  end

  it 'rejects an unknown mode' do
    expect { described_class.new('mode' => 'nope') }.to raise_error(Wip::ConfigError, /mode must be one of/)
  end

  it 'requires a compose: block when mode: compose is set' do
    expect { described_class.new('mode' => 'compose') }
      .to raise_error(Wip::ConfigError, /mode: compose requires a compose: block/)
  end

  it 'requires mode: compose when a compose: block is set' do
    expect { described_class.new('compose' => { 'service' => 'app', 'command' => 'c' }) }
      .to raise_error(Wip::ConfigError, /a compose: block requires mode: compose/)
  end

  it 'exposes compose settings, defaulting to disabled' do
    config = described_class.new({})
    expect(config.compose?).to be(false)
    expect(config.compose_service).to be_nil

    composed = described_class.new('mode' => 'compose',
                                   'compose' => { 'service' => 'app', 'file' => 'compose.yml',
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
      described_class.new('mode' => 'compose',
                          'compose' => { 'file' => 'compose.yml', 'command' => 'my-compose-tool' })
    end.to raise_error(Wip::ConfigError, /compose\.service must not be empty/)
  end

  it 'requires compose.command' do
    expect do
      described_class.new('mode' => 'compose', 'compose' => { 'service' => 'app' })
    end.to raise_error(Wip::ConfigError, /compose\.command must not be empty/)
  end

  it 'rejects compose combined with dependencies' do
    expect do
      described_class.new('mode' => 'compose',
                          'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                          'dependencies' => { 'redis' => { 'image' => 'redis:latest' } })
    end.to raise_error(Wip::ConfigError, /compose is mutually exclusive with dependencies/)
  end

  it 'rejects compose combined with network' do
    expect do
      described_class.new('mode' => 'compose',
                          'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                          'network' => 'app-tier')
    end.to raise_error(Wip::ConfigError, /compose is mutually exclusive with network/)
  end

  it 'has no sync settings unless a sync block is present' do
    config = described_class.new({})
    expect(config.sync?).to be(false)
    expect(config.sync).to be_nil
  end

  it 'derives sync settings from the primary container and the config location' do
    config = described_class.new({ 'container' => 'web',
                                   'dependencies' => { 'web' => { 'image' => 'example:dev',
                                                                  'workdir' => '/srv/app' } },
                                   'sync' => {} },
                                 '/home/me/project/wip.yml')

    expect(config.sync?).to be(true)
    expect(config.sync.source).to eq('/home/me/project')
    expect(config.sync.target).to eq('/srv/app')
    expect(config.sync.volume).to eq('web-src')
  end

  it 'allows sync alongside compose, defaulting sync.mode to run' do
    config = described_class.new('mode' => 'compose',
                                 'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                                 'sync' => { 'image' => 'example:dev' })
    expect(config.sync.mode).to eq('run')
  end

  it 'requires sync.image or sync.build alongside compose' do
    expect do
      described_class.new('mode' => 'compose',
                          'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                          'sync' => {})
    end.to raise_error(Wip::ConfigError, /sync\.image or sync\.build is required under mode: compose/)
  end

  it 'allows sync.build alongside compose in place of sync.image' do
    config = described_class.new('mode' => 'compose', 'container' => 'app',
                                 'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                                 'sync' => { 'build' => { 'dockerfile' => 'FROM alpine' } })
    expect(config.sync.build['tag']).to eq('wip-sync-app:latest')
  end

  it 'rejects sync.mode: exec alongside compose' do
    expect do
      described_class.new('mode' => 'compose',
                          'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                          'sync' => { 'mode' => 'exec', 'image' => 'example:dev' })
    end.to raise_error(Wip::ConfigError, /sync\.mode: exec needs mode: container/)
  end

  it 'includes the resolved sync settings in the effective configuration' do
    config = described_class.new('container' => 'app',
                                 'dependencies' => { 'app' => { 'image' => 'example:dev' } },
                                 'sync' => { 'exclude' => ['.git'] })

    expect(config.to_h['sync']).to include('volume' => 'app-src', 'target' => '/app', 'exclude' => ['.git'])
  end

  it 'includes container and network in the effective configuration, with no defaults or up block' do
    config = described_class.new('container' => 'app', 'network' => 'app-tier',
                                 'dependencies' => { 'app' => { 'image' => 'example:dev' } })

    expect(config.to_h).to include('container' => 'app', 'network' => 'app-tier')
    expect(config.to_h).not_to have_key('defaults')
    expect(config.to_h).not_to have_key('up')
  end
end
