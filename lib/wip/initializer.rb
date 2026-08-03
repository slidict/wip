# frozen_string_literal: true

module Wip
  # Builds a starter wip.yml. Detects an existing compose file next to the
  # target (the same filenames ComposeBridge auto-detects) to decide between
  # mode: compose-native and mode: container, since that's the one decision
  # `wip init` can't leave to a placeholder.
  class Initializer
    # Shared across both templates: every sync.* key SyncSettings understands,
    # commented out with its default/behavior so nothing has to be looked up
    # in the README to customize the mirror.
    SYNC_TEMPLATE = <<~YAML.chomp
      sync: {} # optional; mirrors the source into a named volume instead of bind-mounting it live
        # source: . # optional; host path to mirror (default: wip.yml directory)
        # target: /app # optional; in-container path the mirror volume mounts at (default: workdir, else /app)
        # mount: /host-src # optional; read-only host mount inside the container (default: /host-src)
        # volume: app-src # optional; named volume that holds the mirrored tree (default: "<container>-src")
        # delete: true # optional; rsync --delete so removals on the host are mirrored too (default: true)
        # exclude: [] # optional; rsync --exclude patterns, e.g. ["node_modules", "*.log"]
        # command: rsync # optional; mirror binary to shell out to (default: rsync)
        # options: [] # optional; extra flags appended to the mirror command
        # interval: 2 # optional; seconds between mirrors (default: 2)
        # mode: exec # optional; exec (default, mirrors into the running container) | run (fresh container each mirror)
        # image: # optional; image the mirror container runs (required under mode: compose unless build: is set)
        # build: # optional; let wip build a small dedicated mirror image instead of requiring one to exist
        #   dockerfile: # TODO required if build: is set
        #   tag: # optional (default: wip-sync-<container>:latest)
    YAML

    CONTAINER_TEMPLATE = <<~YAML.freeze
      version: 1
      mode: container # container | compose | compose-native (see README)
      container: app # TODO: rename freely, as long as it matches a key under dependencies: below

      # wslc: {} # optional; override which wslc binary/path wip shells out to (default: auto)
      #   command: auto

      # network: my-network # optional; container network name (mode: container only)

      dependencies:
        app: # this is the container wip creates, execs into, and runs commands in
          image: your/image:tag # TODO: image to run
          workdir: /app # TODO: adjust to match your image, or delete this line
          # interactive: false # optional; keep stdin open / allocate a tty (default: false)
          # remove: true # optional; remove the container after each run (default: true)
          # env: {} # optional; environment variables passed to the container, e.g. {FOO: bar}
          # ports: [] # optional; published ports, e.g. ["3000:3000"]
          # volumes: [] # optional; extra -v specs beyond the source/sync mounts

      # commands:
      #   test: # example custom command, invoked as `wip test`
      #     type: exec # optional; exec (default, runs in the running container) | run (fresh container) | build
      #     command: bundle exec rspec # TODO

      #{SYNC_TEMPLATE}
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

        # wslc: {} # optional; override which wslc binary/path wip shells out to (default: auto)
        #   command: auto

        compose:
          service: app # TODO: which service in #{compose_file} wip run/exec/NAME target
          # file: #{compose_file} # optional; override which compose file to parse (default: auto-detected)
          # project: # optional; project/network name (default: wip.yml directory name)
          # command: # required under mode: compose (external compose-for-wslc binary); unused under compose-native

        # commands:
        #   test: # example custom command, invoked as `wip test`
        #     type: exec # optional; exec (default, runs in the running container) | run (fresh container) | build
        #     command: bundle exec rspec # TODO

        #{SYNC_TEMPLATE}
      YAML
    end
  end
end
