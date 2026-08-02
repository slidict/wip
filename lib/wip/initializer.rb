# frozen_string_literal: true

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
      YAML
    end
  end
end
