# frozen_string_literal: true

require 'spec_helper'
require 'tmpdir'

RSpec.describe Wip::Doctor do
  def results_for(yaml, compose_yaml: nil)
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, 'wip.yml'), yaml)
      File.write(File.join(dir, 'compose.yml'), compose_yaml) if compose_yaml
      described_class.new(loader: Wip::ConfigLoader.new(start_dir: dir)).call
    end
  end

  # Config validates the sync block while loading, so a bad one has to surface
  # as a failed check rather than an exception escaping `wip doctor`.
  it 'reports an invalid sync block as a failed check' do
    results = results_for(<<~YAML)
      version: 1
      container: app
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
      container: app
      dependencies:
        app:
          image: example:dev
      sync: {}
    YAML

    expect(results).to include(an_object_having_attributes(level: :ok,
                                                           message: a_string_matching(%r{volume app-src at /app})))
  end

  it 'warns when sync.image is set with sync.mode: exec' do
    results = results_for(<<~YAML)
      version: 1
      container: app
      dependencies:
        app:
          image: example:dev
      sync:
        mode: exec
        image: some/image:tag
    YAML

    expect(results).to include(an_object_having_attributes(
                                 level: :warn, message: a_string_matching(/pre-boot mirror/)
                               ))
  end

  it 'warns when sync.build is set with sync.mode: exec' do
    results = results_for(<<~YAML)
      version: 1
      container: app
      dependencies:
        app:
          image: example:dev
      sync:
        mode: exec
        build:
          dockerfile: |
            FROM alpine:latest
    YAML

    expect(results).to include(an_object_having_attributes(
                                 level: :warn, message: a_string_matching(/pre-boot mirror/)
                               ))
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

  it 'reports a missing container: as a failed check when dependencies: has entries' do
    results = results_for(<<~YAML)
      version: 1
      dependencies:
        app:
          image: example:dev
    YAML

    expect(results).to include(an_object_having_attributes(
                                 level: :fail, message: 'container: must be set when dependencies: has entries'
                               ))
  end

  it 'reports a compose.service that has no matching compose.yml service as a failed check' do
    results = results_for(<<~YAML, compose_yaml: "services:\n  app:\n    image: example:dev\n")
      version: 1
      mode: compose-native
      compose:
        service: web
    YAML

    expect(results).to include(an_object_having_attributes(
                                 level: :fail, message: "compose.service 'web' has no matching service in compose.yml"
                               ))
  end

  it 'reports the parsed compose file for a valid mode: compose-native config' do
    results = results_for(<<~YAML, compose_yaml: "services:\n  app:\n    image: example:dev\n")
      version: 1
      mode: compose-native
      compose:
        service: app
    YAML

    expect(results).to include(an_object_having_attributes(level: :ok, message: 'Loaded wip.yml'))
    expect(results).to include(an_object_having_attributes(level: :ok, message: 'Parsed compose file'))
  end

  it 'reports a missing compose file as a single failed check, without an unhandled exception' do
    results = results_for(<<~YAML)
      version: 1
      mode: compose-native
      compose:
        service: app
    YAML

    expect(results).to include(an_object_having_attributes(level: :fail,
                                                           message: a_string_matching(/no compose file found/)))
  end
end
