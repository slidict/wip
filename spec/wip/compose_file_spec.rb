# frozen_string_literal: true

require 'spec_helper'
require 'fileutils'

RSpec.describe Wip::ComposeFile do
  around do |example|
    Dir.mktmpdir { |dir| Dir.chdir(dir) { example.run } }
  end

  def write_compose(yaml)
    File.write('compose.yml', yaml)
    'compose.yml'
  end

  it 'requires a services: mapping' do
    path = write_compose("version: '3'\n")
    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /services: must be a mapping/)
  end

  it 'parses a minimal image-based service' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          command: bin/rails s
          environment:
            RAILS_ENV: development
          ports:
            - "3000:3000"
          volumes:
            - app-src:/app
          working_dir: /app
    YAML

    deps = described_class.load(path).to_dependencies_hash
    expect(deps['app']).to eq('image' => 'example:dev', 'command' => 'bin/rails s',
                              'env' => { 'RAILS_ENV' => 'development' }, 'ports' => ['3000:3000'],
                              'volumes' => ['app-src:/app'], 'workdir' => '/app', 'user' => nil)
  end

  it 'normalizes environment given as a KEY=VALUE array' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          environment:
            - RAILS_ENV=development
            - PORT=3000
    YAML

    expect(described_class.load(path).to_dependencies_hash['app']['env'])
      .to eq('RAILS_ENV' => 'development', 'PORT' => '3000')
  end

  it 'rejects an environment array entry with no value' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          environment:
            - RAILS_ENV
    YAML

    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /environment entries must be KEY=VALUE/)
  end

  it 'rejects an environment mapping entry with a null value instead of silently blanking it' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          environment:
            RAILS_ENV:
    YAML

    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /environment\.RAILS_ENV must have a value/)
  end

  it 'joins exec-form command arrays back into a single shell-safe string' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          command: ["bin/rails", "s", "-p 3000"]
    YAML

    expect(described_class.load(path).to_dependencies_hash['app']['command']).to eq('bin/rails s -p\ 3000')
  end

  it 'raises a ConfigError, not a raw exception, when the compose file is missing' do
    expect { described_class.load('nope.yml') }.to raise_error(Wip::ConfigError, /Compose file not found/)
  end

  it 'raises a ConfigError, not a raw exception, on invalid YAML' do
    path = write_compose("services: [\n")
    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /Could not parse/)
  end

  it 'requires at least one of image or build' do
    neither = write_compose("services:\n  app:\n    command: x\n")
    expect { described_class.load(neither) }.to raise_error(Wip::ConfigError, /must set image or build/)
  end

  it 'builds and tags with image: when both image and build are set' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:tagged
          build:
            context: .
    YAML

    compose = described_class.load(path)
    expect(compose.build_specs['app']['tag']).to eq('example:tagged')
    expect(compose.to_dependencies_hash['app']['image']).to eq('example:tagged')
  end

  it 'resolves build.context relative to the compose file, not the current directory' do
    FileUtils.mkdir_p('sub')
    path = File.join('sub', 'compose.yml')
    File.write(path, <<~YAML)
      services:
        app:
          build: ./web
    YAML

    specs = described_class.load(path).build_specs
    expect(specs['app']['context']).to eq(File.expand_path('sub/web'))
    expect(specs['app']['tag']).to eq('wip-compose-app:latest')
  end

  it 'supports build as a mapping with context and dockerfile' do
    path = write_compose(<<~YAML)
      services:
        app:
          build:
            context: .
            dockerfile: Dockerfile.dev
    YAML

    spec = described_class.load(path).build_specs['app']
    expect(spec['dockerfile']).to eq(File.expand_path('Dockerfile.dev'))
    expect(described_class.load(path).to_dependencies_hash['app']['image']).to eq('wip-compose-app:latest')
  end

  it 'normalizes build.args given as a mapping or a KEY=VALUE array' do
    mapping = write_compose(<<~YAML)
      services:
        app:
          build:
            context: .
            args:
              FOO: bar
    YAML
    expect(described_class.load(mapping).build_specs['app']['args']).to eq('FOO' => 'bar')

    array = write_compose(<<~YAML)
      services:
        app:
          build:
            context: .
            args:
              - FOO=bar
    YAML
    expect(described_class.load(array).build_specs['app']['args']).to eq('FOO' => 'bar')
  end

  it 'rejects unsupported build keys' do
    path = write_compose(<<~YAML)
      services:
        app:
          build:
            context: .
            deploy: true
    YAML

    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /build has unsupported key\(s\): deploy/)
  end

  it 'reads user and ignores tty, stdin_open, networks' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          user: "1000:1000"
          tty: true
          stdin_open: true
          networks:
            - app-tier
    YAML

    deps = described_class.load(path).to_dependencies_hash
    expect(deps['app']['user']).to eq('1000:1000')
  end

  it 'excludes services with a profiles: entry from to_dependencies_hash, like an inactive `docker compose` profile' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
        production.build:
          image: example:dev
          profiles:
            - production.build
    YAML

    expect(described_class.load(path).to_dependencies_hash.keys).to eq(['app'])
  end

  it 'excludes services with a profiles: entry from build_specs' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
        production.build:
          build: .
          profiles:
            - production.build
    YAML

    expect(described_class.load(path).build_specs.keys).to eq([])
  end

  it 'still validates depends_on against a profile-gated service' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          depends_on:
            - missing.profile
        missing.profile:
          image: example:dev
          profiles:
            - only-with-profile
    YAML

    expect(described_class.load(path).to_dependencies_hash.keys).to eq(['app'])
  end

  it 'interpolates ${VAR} references from the given env, like `docker compose` does' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          user: ${USER_ID}:${GROUP_ID}
    YAML

    deps = described_class.load(path, env: { 'USER_ID' => '1000', 'GROUP_ID' => '1000' }).to_dependencies_hash
    expect(deps['app']['user']).to eq('1000:1000')
  end

  it 'falls back to a default for ${VAR:-default} and ${VAR-default} when unset' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          working_dir: ${MISSING:-/app}
          command: ${ALSO_MISSING-fallback}
    YAML

    deps = described_class.load(path).to_dependencies_hash
    expect(deps['app']['workdir']).to eq('/app')
    expect(deps['app']['command']).to eq('fallback')
  end

  it 'distinguishes ${VAR:-default} (falls back on empty) from ${VAR-default} (empty stays empty)' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          working_dir: ${EMPTY:-/app}
          command: "prefix-${EMPTY-fallback}-suffix"
    YAML

    deps = described_class.load(path, env: { 'EMPTY' => '' }).to_dependencies_hash
    expect(deps['app']['workdir']).to eq('/app')
    expect(deps['app']['command']).to eq('prefix--suffix')
  end

  it 'substitutes an unset ${VAR} with nothing, and $$ with a literal dollar sign' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          working_dir: /${MISSING}app
          command: echo $$HOME
    YAML

    deps = described_class.load(path).to_dependencies_hash
    expect(deps['app']['workdir']).to eq('/app')
    expect(deps['app']['command']).to eq('echo $HOME')
  end

  it 'interpolates values, not mapping keys, like real Compose' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          environment:
            $KEY: value
    YAML

    deps = described_class.load(path, env: { 'KEY' => 'RENAMED' }).to_dependencies_hash
    expect(deps['app']['env']).to eq('$KEY' => 'value')
  end

  it "substitutes a value without re-parsing it as YAML, so a literal '#' doesn't become a comment" do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          command: ${VALUE}
    YAML

    deps = described_class.load(path, env: { 'VALUE' => 'value # not a comment' }).to_dependencies_hash
    expect(deps['app']['command']).to eq('value # not a comment')
  end

  it 'rejects unsupported service keys' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          deploy:
            replicas: 2
    YAML

    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /unsupported key\(s\): deploy/)
  end

  it 'rejects long-syntax ports and volumes' do
    ports = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          ports:
            - target: 3000
              published: 3000
    YAML
    expect { described_class.load(ports) }.to raise_error(Wip::ConfigError, /ports only supports short syntax/)

    volumes = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          volumes:
            - type: volume
              source: app-src
              target: /app
    YAML
    expect { described_class.load(volumes) }.to raise_error(Wip::ConfigError, /volumes only supports short syntax/)
  end

  it 'orders services so depends_on targets start before their dependents' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          depends_on:
            - redis
            - db
        db:
          image: postgres:16
        redis:
          image: redis:latest
    YAML

    expect(described_class.load(path).service_names_in_dependency_order).to eq(%w[redis db app])
  end

  it 'accepts depends_on as a mapping with condition: service_started' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          depends_on:
            db:
              condition: service_started
        db:
          image: postgres:16
    YAML

    expect(described_class.load(path).service_names_in_dependency_order).to eq(%w[db app])
  end

  it 'rejects depends_on health-check conditions' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          depends_on:
            db:
              condition: service_healthy
        db:
          image: postgres:16
    YAML

    expect do
      described_class.load(path)
    end.to raise_error(Wip::ConfigError, /condition 'service_healthy' is not supported/)
  end

  it 'rejects depends_on naming an unknown service' do
    path = write_compose(<<~YAML)
      services:
        app:
          image: example:dev
          depends_on:
            - ghost
    YAML

    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /depends_on unknown service 'ghost'/)
  end

  it 'rejects a depends_on cycle' do
    path = write_compose(<<~YAML)
      services:
        a:
          image: example:dev
          depends_on: [b]
        b:
          image: example:dev
          depends_on: [a]
    YAML

    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /depends_on cycle/)
  end

  it 'has no build_specs for image-only services' do
    path = write_compose("services:\n  app:\n    image: example:dev\n")
    expect(described_class.load(path).build_specs).to eq({})
  end
end
