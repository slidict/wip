# frozen_string_literal: true

module Wip
  # Builds a starter wip.yml. Detects an existing compose file next to the
  # target (the same filenames ComposeBridge auto-detects) to decide between
  # mode: compose-native and mode: container, since that's the one decision
  # `wip init` can't leave to a placeholder.
  class Initializer
    CONTAINER_TEMPLATE = <<~YAML
      version: 1
      mode: container
      container: app # TODO: rename freely, as long as it matches a key under dependencies: below

      dependencies:
        app: # this is the container wip creates, execs into, and runs commands in
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
        mode: compose-native # wip parses #{compose_file} itself and drives wslc directly — no external
                              # compose-for-wslc binary needed. Prefer a real compose tool instead? Use
                              # mode: compose (see README "Compose mode") and set compose.command.

        compose:
          service: app # TODO: which service in #{compose_file} wip run/exec/NAME target

        sync: {} # optional; mirrors the source into a named volume instead of bind-mounting it live
      YAML
    end
  end
end
