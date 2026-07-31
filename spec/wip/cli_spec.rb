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

    expect(runner).to receive(:run).with(%w[wslc.exe exec -w /app app bin/rails c]).and_return(0)

    described_class.start(%w[rails c])
  end

  it 'falls back to creating the container when `up` finds none to start' do
    runner = instance_double(Wip::CommandRunner)
    allow(Wip::CommandRunner).to receive(:new).and_return(runner)
    allow(Wip::CommandResolver).to receive(:new).and_return(instance_double(Wip::CommandResolver,
                                                                            resolve: 'wslc.exe'))

    expect(runner).to receive(:run).with(%w[wslc.exe start app -a -i]).and_return(1)
    expect(runner).to receive(:run).with(%w[wslc.exe run --name app -w /app example:dev]).and_return(0)

    described_class.start(%w[up])
  end
end
