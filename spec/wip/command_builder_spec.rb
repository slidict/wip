# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::CommandBuilder do
  let(:environment) { instance_double(Wip::Environment, interactive?: true) }
  let(:config) do
    Wip::Config.new('container' => 'app',
                    'dependencies' => { 'app' => { 'image' => 'example:dev', 'workdir' => '/app' } },
                    'commands' => { 'rails' => { 'command' => 'bin/rails', 'interactive' => true },
                                    'build' => { 'type' => 'build', 'tag' => 'example:dev', 'context' => '.' } })
  end
  subject(:builder) { described_class.new(wslc: 'wslc.exe', config: config, environment: environment) }

  it 'builds exec commands' do
    expect(builder.exec(%w[bundle install], interactive: false)).to eq(%w[wslc.exe exec -w /app app bundle install])
  end

  it 'builds interactive run commands with environment, ports, and volumes' do
    settings = { 'interactive' => true, 'env' => { 'PORT' => '3000' }, 'ports' => ['3000:3000'],
                 'volumes' => ['.:/app'] }
    expected = ['wslc.exe', 'run', '--rm', '-it', '-w', '/app', '-e', 'PORT=3000', '-p', '3000:3000', '-v',
                '.:/app', 'example:dev', 'server']
    expect(builder.run(['server'], settings: settings)).to eq(expected)
  end

  it 'passes user through as -u' do
    settings = { 'user' => '1000:1000' }
    expect(builder.exec(%w[whoami], settings: settings,
                                    interactive: false)).to eq(%w[wslc.exe exec -w /app -u 1000:1000 app whoami])
  end

  it 'builds images with extra options before context, without an unsupported --cache-from' do
    expect(builder.build(settings: config.command('build'),
                         extra: ['--build-arg', 'RAILS_ENV=development']))
      .to eq(%w[wslc.exe build -t example:dev --build-arg RAILS_ENV=development .])
  end

  it 'passes --no-cache through when building with --no-cache' do
    expect(builder.build(settings: config.command('build'),
                         extra: ['--no-cache'])).to eq(%w[wslc.exe build -t example:dev --no-cache .])
  end

  it 'appends custom command arguments' do
    expect(builder.custom('rails', ['console'])).to eq(%w[wslc.exe exec -it -w /app app bin/rails console])
  end

  it 'omits ports and volumes from exec commands since wslc exec does not accept them' do
    settings = { 'ports' => ['5000:3000'], 'volumes' => ['.:/app'], 'interactive' => true }
    expect(builder.exec(%w[bin/rails c], settings: settings))
      .to eq(%w[wslc.exe exec -it -w /app app bin/rails c])
  end

  it 'lets a command redirect exec to a different container name' do
    settings = { 'container' => 'other' }
    expect(builder.exec(%w[bin/rails c], settings: settings, interactive: false))
      .to eq(%w[wslc.exe exec -w /app other bin/rails c])
  end

  it 'builds a detached up command that names the persistent container' do
    expect(builder.up(detach: true)).to eq(%w[wslc.exe run --name app -d -w /app example:dev])
  end

  it 'builds a foreground up command with -it when the terminal is interactive' do
    expect(builder.up).to eq(%w[wslc.exe run --name app -it -w /app example:dev])
  end

  it 'builds a start command that attaches by default' do
    expect(builder.start).to eq(%w[wslc.exe start app -a -i])
  end

  it 'builds a detached start command without attaching' do
    expect(builder.start(detach: true)).to eq(%w[wslc.exe start app])
  end

  it 'builds stop and remove commands for the configured container' do
    expect(builder.stop).to eq(%w[wslc.exe stop app])
    expect(builder.remove).to eq(%w[wslc.exe remove -f app])
  end

  it 'builds a quiet find query for the configured container' do
    expect(builder.find).to eq(%w[wslc.exe list --all --filter name=app --format json])
  end

  it 'appends the primary dependency command so the container stays running' do
    with_command = Wip::Config.new('container' => 'app',
                                   'dependencies' => { 'app' => { 'image' => 'example:dev', 'workdir' => '/app',
                                                                  'command' => 'local' } })
    builder = described_class.new(wslc: 'wslc.exe', config: with_command, environment: environment)

    expect(builder.up(detach: true)).to eq(%w[wslc.exe run --name app -d -w /app example:dev local])
  end

  it 'raises a clear error when container points at an undefined dependency' do
    pointing_elsewhere = Wip::Config.new('container' => 'web',
                                         'dependencies' => { 'app' => { 'image' => 'example:dev' } })
    builder = described_class.new(wslc: 'wslc.exe', config: pointing_elsewhere, environment: environment)

    expect { builder.up }.to raise_error(Wip::ConfigError, /No dependencies\.web entry/)
  end

  describe 'network and dependency support' do
    let(:networked_config) do
      Wip::Config.new('container' => 'app', 'network' => 'app-tier',
                      'dependencies' => { 'app' => { 'image' => 'example:dev', 'workdir' => '/app' },
                                          'redis' => { 'image' => 'redis:latest' },
                                          'development.mysql' => {
                                            'image' => 'mysql:8.0',
                                            'command' => '--default-authentication-plugin=mysql_native_password',
                                            'env' => { 'MYSQL_ROOT_PASSWORD' => 'password' },
                                            'ports' => ['3306:3306']
                                          } })
    end
    subject(:builder) { described_class.new(wslc: 'wslc.exe', config: networked_config, environment: environment) }

    it 'attaches the main container to the configured network on creation' do
      expect(builder.up(detach: true)).to eq(%w[wslc.exe run --name app --network app-tier -d -w /app example:dev])
    end

    it 'builds network create and list commands' do
      expect(builder.network_create).to eq(%w[wslc.exe network create app-tier])
      expect(builder.network_list).to eq(%w[wslc.exe network list --format json])
    end

    it 'builds a dependency container on the shared network with its own env/ports/command' do
      expected = ['wslc.exe', 'run', '--name', 'development.mysql', '--network', 'app-tier', '-d', '-e',
                  'MYSQL_ROOT_PASSWORD=password', '-p', '3306:3306', 'mysql:8.0',
                  '--default-authentication-plugin=mysql_native_password']
      expect(builder.dependency_up('development.mysql')).to eq(expected)
    end

    it 'builds a simple dependency container without a custom command' do
      expect(builder.dependency_up('redis')).to eq(%w[wslc.exe run --name redis --network app-tier -d redis:latest])
    end

    it 'builds start/find/stop/remove commands for a dependency' do
      expect(builder.dependency_start('redis')).to eq(%w[wslc.exe start redis])
      expect(builder.dependency_find('redis')).to eq(%w[wslc.exe list --all --filter name=redis --format json])
      expect(builder.dependency_stop('redis')).to eq(%w[wslc.exe stop redis])
      expect(builder.dependency_remove('redis')).to eq(%w[wslc.exe remove -f redis])
    end

    it 'raises for an undefined dependency' do
      expect { builder.dependency_up('unknown') }.to raise_error(Wip::ConfigError, /Unknown dependency: unknown/)
    end
  end

  describe 'sync support' do
    let(:synced_config) do
      Wip::Config.new('container' => 'app',
                      'dependencies' => { 'app' => { 'image' => 'example:dev', 'workdir' => '/app',
                                                     'volumes' => ['.:/app', 'bundle:/usr/local/bundle'] },
                                          'redis' => { 'image' => 'redis:latest', 'volumes' => ['.:/app'] } },
                      'sync' => { 'exclude' => ['.git'] })
    end
    subject(:builder) { described_class.new(wslc: 'wslc.exe', config: synced_config, environment: environment) }

    it 'replaces the live bind mount with the read-only source and the named volume' do
      expect(builder.up(detach: true)).to eq(['wslc.exe', 'run', '--name', 'app', '-d', '-w', '/app', '-v',
                                              'bundle:/usr/local/bundle', '-v', '.:/host-src:ro', '-v',
                                              'app-src:/app', 'example:dev'])
    end

    it 'leaves dependency containers mounting the host directly' do
      expect(builder.dependency_up('redis'))
        .to eq(%w[wslc.exe run --name redis -d -v .:/app redis:latest])
    end

    it 'mirrors from a throwaway container when the app container is not running' do
      expect(builder.sync_run).to eq(['wslc.exe', 'run', '--rm', '-v', '.:/host-src:ro', '-v', 'app-src:/app',
                                      'example:dev', 'rsync', '-r', '-l', '-t', '--whole-file', '--delete',
                                      '--exclude=.git', '/host-src/', '/app/'])
    end

    it 'mirrors inside the running container' do
      expect(builder.sync_exec).to eq(%w[wslc.exe exec app rsync -r -l -t --whole-file --delete --exclude=.git
                                         /host-src/ /app/])
    end

    it 'raises when sync commands are built without a sync block' do
      expect { described_class.new(wslc: 'wslc.exe', config: config, environment: environment).sync_run }
        .to raise_error(Wip::ConfigError, /No sync: block/)
    end

    it 'uses the sync.build tag over sync.image and the primary dependency, without needing one to exist' do
      built_config = Wip::Config.new('mode' => 'compose', 'container' => 'app',
                                     'compose' => { 'service' => 'app', 'command' => 'my-compose-tool' },
                                     'sync' => { 'build' => { 'dockerfile' => 'FROM alpine' } })
      builder = described_class.new(wslc: 'wslc.exe', config: built_config, environment: environment)

      expect(builder.sync_run).to eq(['wslc.exe', 'run', '--rm', '-v', '.:/host-src:ro', '-v', 'app-src:/app',
                                      'wip-sync-app:latest', 'rsync', '-r', '-l', '-t', '--whole-file', '--delete',
                                      '/host-src/', '/app/'])
    end

    it 'builds the sync.build Dockerfile from a caller-supplied context' do
      built_config = Wip::Config.new('sync' => { 'build' => { 'dockerfile' => 'FROM alpine', 'tag' => 'x:1' } })
      builder = described_class.new(wslc: 'wslc.exe', config: built_config, environment: environment)

      expect(builder.sync_build('/tmp/staged')).to eq(%w[wslc.exe build -t x:1 /tmp/staged])
    end

    it 'raises when building without a sync.build block' do
      expect { builder.sync_build('/tmp/staged') }.to raise_error(Wip::ConfigError, /No sync\.build configured/)
    end
  end

  describe 'dotenv support' do
    subject(:builder) do
      described_class.new(wslc: 'wslc.exe', config: config, environment: environment,
                          dotenv: { 'RAILS_ENV' => 'development', 'PORT' => '4000' })
    end

    it 'injects dotenv values that are not already set' do
      expect(builder.exec(%w[bin/rails c], settings: { 'env' => { 'PORT' => '3000' } }, interactive: false))
        .to eq(%w[wslc.exe exec -w /app -e RAILS_ENV=development -e PORT=3000 app bin/rails c])
    end
  end
end
