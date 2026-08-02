# frozen_string_literal: true

require 'spec_helper'
require 'tmpdir'
require 'fileutils'

RSpec.describe Wip::CLI do
  around do |example|
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, 'wip.yml'), <<~YAML)
        version: 1
        container: app
        dependencies:
          app:
            image: example:dev
            workdir: /app
        commands:
          rails:
            command: bin/rails
            interactive: true
      YAML
      Dir.chdir(dir) { example.run }
    end
  end

  it 'routes an unrecognized top-level command to the custom wip.yml dispatcher' do
    runner = instance_double(Wip::CommandRunner)
    allow(Wip::CommandRunner).to receive(:new).and_return(runner)
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))

    expect(runner).to receive(:run).with(%w[wslc.exe exec -w /app app bin/rails c], interactive: false).and_return(0)

    described_class.start(%w[rails c])
  end

  it 'prints step-by-step progress and timing when --debug is passed' do
    runner = instance_double(Wip::CommandRunner, run: 0)
    allow(Wip::CommandRunner).to receive(:new).and_return(runner)
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))

    expect { described_class.start(%w[rails c --debug]) }.to output(
      a_string_matching(%r{\[debug\] running: wslc\.exe exec.*bin/rails c})
        .and(a_string_matching(/\[debug\] done in \d+\.\d{2}s/))
    ).to_stderr
  end

  it 'forwards --debug-log to the DebugReporter' do
    runner = instance_double(Wip::CommandRunner, run: 0)
    allow(Wip::CommandRunner).to receive(:new).and_return(runner)
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))
    log_path = File.join(Dir.mktmpdir, 'custom.log')
    expect(Wip::DebugReporter).to receive(:new).with(enabled: true, log: log_path).and_call_original

    described_class.start(['rails', 'c', '--debug', "--debug-log=#{log_path}"])
  ensure
    FileUtils.rm_rf(File.dirname(log_path)) if log_path
  end

  it 'creates the container when `up` finds none existing via a quiet `wslc list` probe' do
    runner = instance_double(Wip::CommandRunner)
    allow(Wip::CommandRunner).to receive(:new) do |**kwargs|
      kwargs[:stdout]&.write('[]')
      runner
    end
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))
    allow(runner).to receive(:run).with(%w[wslc.exe list --all --filter name=app --format json]).and_return(0)
    expect(runner).to receive(:run).with(%w[wslc.exe run --name app -w /app example:dev],
                                         interactive: false).and_return(0)

    described_class.start(%w[up])
  end

  it 'starts an existing container instead of recreating it' do
    runner = instance_double(Wip::CommandRunner)
    allow(Wip::CommandRunner).to receive(:new) do |**kwargs|
      kwargs[:stdout]&.write('[{"Name":"app"}]')
      runner
    end
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))
    allow(runner).to receive(:run).with(%w[wslc.exe list --all --filter name=app --format json]).and_return(0)
    expect(runner).to receive(:run).with(%w[wslc.exe start app -a -i], interactive: false).and_return(0)

    described_class.start(%w[up])
  end

  it 'creates the network and dependencies before bringing up the main container' do
    File.write('wip.yml', <<~YAML)
      version: 1
      container: app
      network: app-tier
      dependencies:
        app:
          image: example:dev
          workdir: /app
        redis:
          image: redis:latest
    YAML

    fake_runner = Class.new do
      class << self
        attr_accessor :calls, :responses
      end
      self.calls = []
      self.responses = {
        %w[wslc.exe network list --format json] => '[]',
        %w[wslc.exe list --all --filter name=redis --format json] => '[]',
        %w[wslc.exe list --all --filter name=app --format json] => '[]'
      }

      def initialize(stdout: nil, **_kwargs)
        @stdout = stdout
      end

      def run(command, interactive: false, **_kwargs)
        self.class.calls << [command, interactive]
        @stdout&.write(self.class.responses.fetch(command, ''))
        0
      end
    end
    stub_const('Wip::CommandRunner', fake_runner)
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))

    described_class.start(%w[up -d])

    expected_commands = [
      %w[wslc.exe network list --format json],
      %w[wslc.exe network create app-tier],
      %w[wslc.exe list --all --filter name=redis --format json],
      %w[wslc.exe run --name redis --network app-tier -d redis:latest],
      %w[wslc.exe list --all --filter name=app --format json],
      %w[wslc.exe run --name app --network app-tier -d -w /app example:dev]
    ]
    expect(fake_runner.calls.map(&:first)).to eq(expected_commands)
  end

  it 'auto-loads .env next to wip.yml and injects it as container env, without overriding wip.yml env' do
    File.write('wip.yml', <<~YAML)
      version: 1
      container: app
      dependencies:
        app:
          image: example:dev
          workdir: /app
          env:
            PORT: "3000"
    YAML
    File.write('.env', "PORT=9999\nRAILS_ENV=development\n")

    runner = instance_double(Wip::CommandRunner)
    allow(Wip::CommandRunner).to receive(:new) do |**kwargs|
      kwargs[:stdout]&.write('[]')
      runner
    end
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))
    allow(runner).to receive(:run).with(%w[wslc.exe list --all --filter name=app --format json]).and_return(0)
    expect(runner).to receive(:run).with(
      %w[wslc.exe run --name app -w /app -e PORT=3000 -e RAILS_ENV=development example:dev], interactive: false
    ).and_return(0)

    described_class.start(%w[up])
  end

  describe 'sync mode' do
    let(:source) { File.expand_path('.') }

    before do
      File.write('wip.yml', <<~YAML)
        version: 1
        container: app
        dependencies:
          app:
            image: example:dev
            workdir: /app
            volumes:
              - ".:/app"
        sync:
          exclude:
            - .git
      YAML
      allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                              resolve: 'wslc.exe'))
    end

    def stub_runner(probe_output)
      runner = instance_double(Wip::CommandRunner)
      allow(Wip::CommandRunner).to receive(:new) do |**kwargs|
        kwargs[:stdout]&.write(probe_output)
        runner
      end
      runner
    end

    it 'mirrors into the volume before booting, and boots off the volume instead of the bind mount' do
      runner = stub_runner('[]')
      allow(runner).to receive(:run).with(%w[wslc.exe list --all --filter name=app --format json]).and_return(0)
      expect(runner).to receive(:run).with(
        ['wslc.exe', 'run', '--rm', '-v', "#{source}:/host-src:ro", '-v', 'app-src:/app', 'example:dev',
         'rsync', '-r', '-l', '-t', '--whole-file', '--delete', '--exclude=.git', '/host-src/', '/app/'],
        interactive: false
      ).and_return(0).ordered
      expect(runner).to receive(:run).with(
        ['wslc.exe', 'run', '--name', 'app', '-d', '-w', '/app', '-v', "#{source}:/host-src:ro", '-v',
         'app-src:/app', 'example:dev'], interactive: false
      ).and_return(0).ordered

      described_class.start(%w[up -d])
    end

    it 'skips the pre-boot mirror when --no-sync is passed' do
      runner = stub_runner('[]')
      allow(runner).to receive(:run).with(%w[wslc.exe list --all --filter name=app --format json]).and_return(0)
      expect(runner).not_to receive(:run).with(a_collection_including('rsync'), any_args)
      expect(runner).to receive(:run).with(a_collection_including('--name', 'app'), interactive: false).and_return(0)

      described_class.start(%w[up -d --no-sync])
    end

    it 'runs a one-shot mirror inside the running container (sync.mode: exec, the default)' do
      runner = stub_runner('')
      expect(runner).to receive(:run).with(
        %w[wslc.exe exec app rsync -r -l -t --whole-file --delete --exclude=.git /host-src/ /app/],
        interactive: false
      ).and_return(0)

      described_class.start(%w[sync])
    end

    it 'uses a throwaway container when sync.mode: run is configured' do
      File.write('wip.yml', <<~YAML)
        version: 1
        container: app
        dependencies:
          app:
            image: example:dev
        sync:
          exclude:
            - .git
          mode: run
      YAML
      runner = stub_runner('')
      expect(runner).to receive(:run).with(a_collection_including('--rm', 'rsync'), interactive: false).and_return(0)

      described_class.start(%w[sync])
    end

    it 'builds the sync.build image once before mirroring, using it over the primary dependency' do
      File.write('wip.yml', <<~YAML)
        version: 1
        container: app
        dependencies:
          app:
            image: example:dev
        sync:
          mode: run
          build:
            dockerfile: |
              FROM alpine:latest
              RUN apk add --no-cache rsync
      YAML
      runner = stub_runner('')
      expect(runner).to receive(:run).with(a_collection_including('build', '-t', 'wip-sync-app:latest'),
                                           interactive: false).and_return(0).ordered
      expect(runner).to receive(:run).with(a_collection_including('--rm', 'wip-sync-app:latest', 'rsync'),
                                           interactive: false).and_return(0).ordered

      described_class.start(%w[sync])
    end

    it 'builds sync.build once per --watch run, not on every tick' do
      File.write('wip.yml', <<~YAML)
        version: 1
        container: app
        dependencies:
          app:
            image: example:dev
        sync:
          mode: run
          build:
            dockerfile: FROM alpine:latest
      YAML
      runner = stub_runner('')
      builds = 0
      allow(runner).to receive(:run).with(a_collection_including('build'), interactive: false) do
        builds += 1
        0
      end
      syncs = 0
      allow(runner).to receive(:run).with(a_collection_including('--rm', 'rsync'), interactive: false) do
        syncs += 1
        raise Interrupt if syncs == 2

        0
      end

      described_class.start(%w[sync --watch --interval 0.01])

      expect(builds).to eq(1)
      expect(syncs).to eq(2)
    end

    it 'keeps mirroring on an interval with --watch until interrupted' do
      runner = stub_runner('')
      syncs = 0
      allow(runner).to receive(:run).with(a_collection_including('rsync'), interactive: false) do
        syncs += 1
        raise Interrupt if syncs == 2

        0
      end

      expect { described_class.start(%w[sync --watch --interval 0.01]) }
        .to output(/syncing .* every 0\.01s/).to_stderr
      expect(syncs).to eq(2)
    end

    it 'requires a sync block' do
      File.write('wip.yml', "version: 1\ncontainer: app\ndependencies:\n  app:\n    image: example:dev\n")

      expect { described_class.start(%w[sync]) }.to raise_error(Wip::ConfigError, /needs a sync: block/)
    end

    it 'rejects a non-positive --interval instead of letting sleep raise' do
      stub_runner('[]')

      expect { described_class.start(%w[sync --watch --interval -1]) }
        .to raise_error(Wip::ConfigError, /--interval must be a positive number/)
    end

    it 'points at `wip dispatch` when wip.yml defines a command the built-in shadows' do
      File.write('wip.yml', <<~YAML)
        version: 1
        container: app
        dependencies:
          app:
            image: example:dev
        sync: {}
        commands:
          sync:
            command: bin/custom-sync
      YAML
      runner = stub_runner('[]')
      allow(runner).to receive(:run).and_return(0)

      expect { described_class.start(%w[sync]) }
        .to output(/commands\.sync .* is shadowed by the built-in `wip sync`.*wip dispatch sync/m).to_stderr
    end
  end

  it 'excludes files matched by .dockerignore from the build context' do
    FileUtils.mkdir_p('node_modules')
    File.write(File.join('node_modules', 'pkg.js'), '')
    File.write('.dockerignore', "node_modules\n")
    File.write('Dockerfile', "FROM scratch\n")
    File.write('wip.yml', <<~YAML)
      version: 1
      container: app
      dependencies:
        app:
          image: example:dev
      commands:
        build:
          type: build
          tag: example:dev
          context: .
    YAML

    runner = instance_double(Wip::CommandRunner, run: 0)
    allow(Wip::CommandRunner).to receive(:new).and_return(runner)
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))
    staged_context = nil
    dockerfile_present = node_modules_present = nil
    expect(runner).to receive(:run) do |command, **_kwargs|
      staged_context = command.last
      dockerfile_present = File.exist?(File.join(staged_context, 'Dockerfile'))
      node_modules_present = File.exist?(File.join(staged_context, 'node_modules'))
      0
    end

    described_class.start(%w[build])

    expect(staged_context).not_to eq(Dir.pwd)
    expect(dockerfile_present).to be true
    expect(node_modules_present).to be false
  end

  context 'in compose mode' do
    around do |example|
      Dir.mktmpdir do |dir|
        File.write(File.join(dir, 'wip.yml'), <<~YAML)
          version: 1
          mode: compose
          compose:
            service: app
            command: wslc-compose
        YAML
        File.write(File.join(dir, 'compose.yml'), "services:\n  app:\n    image: example:dev\n")
        Dir.chdir(dir) { example.run }
      end
    end

    let(:compose_file) { File.expand_path('compose.yml') }

    before do
      allow(Wip::CommandResolver).to receive(:new) do |**kwargs|
        command = kwargs[:label] == 'compose command' ? 'wslc-compose' : 'wslc.exe'
        instance_double(Wip::CommandResolver, resolve: command)
      end
    end

    it 'delegates up to wslc-compose' do
      runner = instance_double(Wip::CommandRunner, run: 0)
      allow(Wip::CommandRunner).to receive(:new).and_return(runner)
      expect(runner).to receive(:run).with(['wslc-compose', '-f', compose_file, 'up', '-d'],
                                           interactive: false).and_return(0)

      described_class.start(%w[up -d])
    end

    it 'mirrors into the volume before compose starts the container when sync is enabled' do
      File.write('wip.yml', <<~YAML)
        version: 1
        mode: compose
        container: app
        compose:
          service: app
          command: wslc-compose
        sync:
          image: example:dev
      YAML
      runner = instance_double(Wip::CommandRunner, run: 0)
      allow(Wip::CommandRunner).to receive(:new).and_return(runner)
      source = File.expand_path('.')
      expect(runner).to receive(:run).with(
        ['wslc.exe', 'run', '--rm', '-v', "#{source}:/host-src:ro", '-v', 'app-src:/app', 'example:dev',
         'rsync', '-r', '-l', '-t', '--whole-file', '--delete', '/host-src/', '/app/'],
        interactive: false
      ).and_return(0).ordered
      expect(runner).to receive(:run).with(['wslc-compose', '-f', compose_file, 'up', '-d'],
                                           interactive: false).and_return(0).ordered

      described_class.start(%w[up -d])
    end

    it 'skips the pre-boot mirror when --no-sync is passed, even with sync configured' do
      File.write('wip.yml', <<~YAML)
        version: 1
        mode: compose
        compose:
          service: app
          command: wslc-compose
        sync:
          image: example:dev
      YAML
      runner = instance_double(Wip::CommandRunner, run: 0)
      allow(Wip::CommandRunner).to receive(:new).and_return(runner)
      expect(runner).not_to receive(:run).with(a_collection_including('rsync'), any_args)
      expect(runner).to receive(:run).with(['wslc-compose', '-f', compose_file, 'up', '-d'],
                                           interactive: false).and_return(0)

      described_class.start(%w[up -d --no-sync])
    end

    it 'attaches to an un-detached compose up when the terminal is interactive' do
      allow(Wip::Environment).to receive(:new).and_return(instance_double(Wip::Environment, interactive?: true))
      runner = instance_double(Wip::CommandRunner, run: 0)
      allow(Wip::CommandRunner).to receive(:new).and_return(runner)
      expect(runner).to receive(:run).with(['wslc-compose', '-f', compose_file, 'up'],
                                           interactive: true).and_return(0)

      described_class.start(%w[up])
    end

    it 'delegates down to wslc-compose' do
      runner = instance_double(Wip::CommandRunner, run: 0)
      allow(Wip::CommandRunner).to receive(:new).and_return(runner)
      expect(runner).to receive(:run).with(['wslc-compose', '-f', compose_file, 'down'],
                                           interactive: false).and_return(0)

      described_class.start(%w[down])
    end

    it 'delegates exec to the configured compose service' do
      runner = instance_double(Wip::CommandRunner, run: 0)
      allow(Wip::CommandRunner).to receive(:new).and_return(runner)
      expect(runner).to receive(:run).with(['wslc-compose', '-f', compose_file, 'exec', 'app', 'bin/rails', 'c'],
                                           interactive: false).and_return(0)

      described_class.start(%w[exec bin/rails c])
    end

    it 'delegates logs to wslc-compose' do
      runner = instance_double(Wip::CommandRunner, run: 0)
      allow(Wip::CommandRunner).to receive(:new).and_return(runner)
      expect(runner).to receive(:run).with(['wslc-compose', '-f', compose_file, 'logs', '-f'],
                                           interactive: true).and_return(0)

      described_class.start(%w[logs])
    end

    it 'rejects `wip logs` outside compose mode' do
      File.write('wip.yml', <<~YAML)
        version: 1
        container: app
        dependencies:
          app:
            image: example:dev
      YAML

      expect { described_class.start(%w[logs]) }.to raise_error(Wip::ConfigError, /compose mode/)
    end
  end

  describe 'init' do
    around do |example|
      Dir.mktmpdir { |dir| Dir.chdir(dir) { example.run } }
    end

    it 'writes a mode: container wip.yml when no compose file is present' do
      described_class.start(%w[init])

      expect(YAML.safe_load_file('wip.yml')).to include('mode' => 'container')
    end

    it 'writes a mode: compose wip.yml when a compose file is present' do
      File.write('compose.yml', "services:\n  app:\n    image: example:dev\n")

      described_class.start(%w[init])

      expect(YAML.safe_load_file('wip.yml')).to include('mode' => 'compose')
    end

    it 'refuses to overwrite an existing wip.yml without --force' do
      File.write('wip.yml', "version: 1\n")

      expect { described_class.start(%w[init]) }.to raise_error(Wip::Error, /already exists/)
    end

    it 'overwrites an existing wip.yml with --force' do
      File.write('wip.yml', "version: 1\n")

      described_class.start(%w[init --force])

      expect(YAML.safe_load_file('wip.yml')).to include('mode' => 'container')
    end
  end
end
