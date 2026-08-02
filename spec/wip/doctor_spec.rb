# frozen_string_literal: true

require 'spec_helper'
require 'tmpdir'

RSpec.describe Wip::Doctor do
  def results_for(yaml)
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, 'wip.yml'), yaml)
      described_class.new(loader: Wip::ConfigLoader.new(start_dir: dir)).call
    end
  end

  # Config validates the sync block while loading, so a bad one has to surface
  # as a failed check rather than an exception escaping `wip doctor`.
  it 'reports an invalid sync block as a failed check' do
    results = results_for(<<~YAML)
      version: 1
      dependencies:
        app:
          image: example:dev
      sync:
        interval: 0
    YAML

    expect(results).to include(an_object_having_attributes(level: :fail,
                                                           message: 'sync.interval must be a positive number'))
  end

  it 'reports the resolved sync plan when the source exists' do
    results = results_for(<<~YAML)
      version: 1
      dependencies:
        app:
          image: example:dev
      sync: {}
    YAML

    expect(results).to include(an_object_having_attributes(level: :ok,
                                                           message: a_string_matching(%r{volume app-src at /app})))
  end

  it 'reports a missing primary container as a failed check' do
    results = results_for(<<~YAML)
      version: 1
      container: web
      dependencies:
        app:
          image: example:dev
    YAML

    expect(results).to include(an_object_having_attributes(level: :fail, message: 'No dependencies.web entry'))
  end
end
