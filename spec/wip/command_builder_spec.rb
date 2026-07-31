# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::CommandBuilder do
  let(:environment) { instance_double(Wip::Environment, interactive?: true) }
  let(:config) do
    Wip::Config.new('defaults' => { 'container' => 'app', 'image' => 'example:dev', 'workdir' => '/app' },
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

  it 'builds images with extra options before context' do
    expect(builder.build(settings: config.command('build'),
                         extra: ['--no-cache'])).to eq(%w[wslc.exe build -t example:dev
                                                          --no-cache .])
  end

  it 'appends custom command arguments' do
    expect(builder.custom('rails', ['console'])).to eq(%w[wslc.exe exec -it -w /app app bin/rails console])
  end

  it 'omits ports and volumes from exec commands since wslc exec does not accept them' do
    settings = { 'ports' => ['5000:3000'], 'volumes' => ['.:/app'], 'interactive' => true }
    expect(builder.exec(%w[bin/rails c], settings: settings))
      .to eq(%w[wslc.exe exec -it -w /app app bin/rails c])
  end
end
