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
                              'volumes' => ['app-src:/app'], 'workdir' => '/app')
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

  it 'requires exactly one of image or build' do
    neither = write_compose("services:\n  app:\n    command: x\n")
    expect { described_class.load(neither) }.to raise_error(Wip::ConfigError, /must set image or build/)

    both = write_compose("services:\n  app:\n    image: example:dev\n    build: .\n")
    expect { described_class.load(both) }.to raise_error(Wip::ConfigError, /must not set both image and build/)
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
    expect(spec['dockerfile']).to eq('Dockerfile.dev')
    expect(described_class.load(path).to_dependencies_hash['app']['image']).to eq('wip-compose-app:latest')
  end

  it 'rejects unsupported build keys' do
    path = write_compose(<<~YAML)
      services:
        app:
          build:
            context: .
            args:
              FOO: bar
    YAML

    expect { described_class.load(path) }.to raise_error(Wip::ConfigError, /build has unsupported key\(s\): args/)
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
