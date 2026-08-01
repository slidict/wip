# frozen_string_literal: true

require 'spec_helper'
require 'tmpdir'

RSpec.describe Wip::CLI do
  around do |example|
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, 'wip.yml'), <<~YAML)
        version: 1
        defaults:
          container: app
          image: example:dev
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
      defaults:
        container: app
        image: example:dev
        network: app-tier
      dependencies:
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
end
