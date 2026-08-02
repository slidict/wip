# frozen_string_literal: true

require 'yaml'

module Wip
  # Builds a starter wip.yml. Detects an existing compose file next to the
  # target (the same filenames ComposeBridge auto-detects) to decide between
  # mode: compose and mode: container, since that's the one decision `wip
  # init` can't leave to a placeholder.
  class Initializer
    CONTAINER_TEMPLATE = <<~YAML
      version: 1
      mode: container

      dependencies:
        app: # TODO: this is the container wip creates, execs into, and runs commands in
          image: your/image:tag # TODO: image to run
          workdir: /app # TODO: adjust to match your image, or delete this line

      sync: {} # optional; mirrors the source into a named volume instead of bind-mounting it live
    YAML

    def initialize(dir: Dir.pwd)
      @dir = dir
    end

    def compose? = !!compose_file

    def call
      compose? ? compose_template : CONTAINER_TEMPLATE
    end

    private

    def compose_file
      @compose_file ||= ComposeBridge::FILENAMES.find { |name| File.file?(File.join(@dir, name)) }
    end

    def compose_template
      <<~YAML
        version: 1
        mode: compose

        compose:
          service: app # TODO: which service in #{compose_file} wip run/exec/NAME target
          command: wslc-compose # TODO: the compose-for-wslc binary/path you have installed

        #{sync_hint}
      YAML
    end

    # sync: needs sync.image (mode: compose has no dependencies: entry to borrow one from) and a
    # named volume the compose service mounts, matching sync.volume ("app-src" by default). Checked
    # against the detected compose file so the hint doesn't repeat what's already set up.
    def sync_hint
      return sync_hint_configured if compose_volume_mounted?

      sync_hint_todo
    end

    def sync_hint_configured
      <<~COMMENT.chomp
        # #{compose_file} already mounts a volume matching sync.volume's default (app-src).
        # sync: # add sync.image too (required under mode: compose) — see README
      COMMENT
    end

    def sync_hint_todo
      <<~COMMENT.chomp
        # sync: # optional; mirrors the source into a named volume instead of bind-mounting it live
        #   image: your/image:tag # required under mode: compose (no dependencies: entry to borrow one from)
        #   # the service above must also mount a volume named "app-src" (sync.volume's default) at the
        #   # path your app expects — add to #{compose_file}:
        #   #   volumes:
        #   #     - app-src:/app
        #   # and, alongside services:
        #   # volumes:
        #   #   app-src:
      COMMENT
    end

    def compose_volume_mounted?
      services = YAML.safe_load_file(File.join(@dir, compose_file), aliases: true)&.fetch('services', nil) || {}
      services.values.any? { |service| Array(service['volumes']).any? { |v| v.to_s.start_with?('app-src:') } }
    rescue StandardError
      false
    end
  end
end
