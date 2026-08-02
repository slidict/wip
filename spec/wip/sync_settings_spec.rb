# frozen_string_literal: true

require 'spec_helper'

RSpec.describe Wip::SyncSettings do
  subject(:sync) { described_class.new({}, container: 'app') }

  it 'defaults the mount, volume, and mirror command' do
    expect(sync.source).to eq('.')
    expect(sync.target).to eq('/app')
    expect(sync.mount).to eq('/host-src')
    expect(sync.volume).to eq('app-src')
    expect(sync.interval).to eq(2)
    expect(sync.mirror_command).to eq(%w[rsync -r -l -t --whole-file --delete /host-src/ /app/])
  end

  it 'falls back to the configured workdir for the target' do
    expect(described_class.new({}, workdir: '/srv/app').target).to eq('/srv/app')
  end

  it 'builds the read-only source mount and the named volume' do
    expect(sync.volume_specs).to eq(['.:/host-src:ro', 'app-src:/app'])
  end

  it 'expands the source against the wip.yml directory' do
    settings = described_class.new({ 'source' => 'src' }, base: '/home/me/project')

    expect(settings.source).to eq('/home/me/project/src')
    expect(settings.volume_specs.first).to eq('/home/me/project/src:/host-src:ro')
  end

  it 'passes excludes, extra options, and a custom binary through to the mirror command' do
    settings = described_class.new({ 'command' => 'rsync-3', 'exclude' => ['.git', 'tmp/'],
                                     'options' => ['--info=stats0'], 'delete' => false })

    expect(settings.mirror_command)
      .to eq(%w[rsync-3 -r -l -t --whole-file --exclude=.git --exclude=tmp/ --info=stats0 /host-src/ /app/])
  end

  it 'recognizes the volumes it replaces, including mode suffixes and Windows-style hosts' do
    expect(sync).to be_replaces('.:/app')
    expect(sync).to be_replaces('.:/app/')
    expect(sync).to be_replaces('/host/src:/app:ro')
    expect(sync).to be_replaces('C:\\src:/host-src:ro')
    expect(sync).not_to be_replaces('bundle:/usr/local/bundle')
  end

  it 'rejects a relative target, a colliding mount, and a non-positive interval' do
    expect { described_class.new({ 'target' => 'app' }) }.to raise_error(Wip::ConfigError, /absolute path/)
    expect { described_class.new({ 'mount' => '/app/' }) }.to raise_error(Wip::ConfigError, /differ from sync.target/)
    expect { described_class.new({ 'interval' => 0 }) }.to raise_error(Wip::ConfigError, /positive number/)
    expect { described_class.new('nope') }.to raise_error(Wip::ConfigError, /must be a mapping/)
  end

  it 'defaults mode to exec outside compose, and to run under compose' do
    expect(described_class.new({}).mode).to eq('exec')
    expect(described_class.new({}, compose: true).mode).to eq('run')
  end

  it 'lets sync.mode override the default explicitly' do
    expect(described_class.new({ 'mode' => 'run' }).mode).to eq('run')
  end

  it 'rejects an unknown sync.mode and exec under compose' do
    expect { described_class.new({ 'mode' => 'nope' }) }.to raise_error(Wip::ConfigError, /sync.mode must be one of/)
    expect { described_class.new({ 'mode' => 'exec' }, compose: true) }
      .to raise_error(Wip::ConfigError, /sync.mode: exec needs mode: container/)
  end
end
