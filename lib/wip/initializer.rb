# frozen_string_literal: true

require 'pathname'

module Wip
  # Builds a starter wip.yml. Detects an existing compose file next to the
  # target (the same filenames ComposeBridge auto-detects) to decide between
  # mode: compose-native and mode: container, since that's the one decision
  # `wip init` can't leave to a placeholder.
  class Initializer
    # Appended to sync: in both templates. Every key here is either a plain constant
    # default (safe to state literally) or, where commented, a value SyncSettings
    # derives from another key (target from workdir, volume from the container/service
    # name) — those stay unset so they keep tracking that key instead of going stale.
    SYNC_EXTRAS = <<~YAML.chomp.gsub(/^/, '  ')
      source: . # optional; host path to mirror (default: wip.yml directory)
      # target: /app # optional; in-container mount point for the mirror (default: the container's workdir, else /app)
      mount: /host-src # optional; read-only host mount inside the container
      # volume: app-src # optional; named volume holding the mirrored tree (default: "<container>-src")
      delete: true # optional; rsync --delete so removals on the host are mirrored too
      exclude: [] # optional; rsync --exclude patterns, e.g. ["node_modules", "*.log"]
      command: rsync # optional; mirror binary to shell out to
      options: [] # optional; extra flags appended to the mirror command
      interval: 2 # optional; seconds between mirrors
      mode: exec # optional; exec (default, mirrors into the running container) | run (always a fresh container)
      # image: your/image:tag # only needed if sync.mode: run (or under mode: compose); image the mirror container runs
      # build: # alternative to image — let wip build a small dedicated mirror image itself
      #   dockerfile: |
      #     FROM alpine:latest
      #     RUN apk add --no-cache rsync
      #   tag: wip-sync-app:latest # optional (default: wip-sync-<container>:latest)
    YAML

    # Appended to both templates after dependencies/compose. commands: itself is left
    # commented since an empty commands: {} is indistinguishable from omitting the key.
    COMMANDS_EXAMPLE = <<~YAML.chomp
      # commands: # optional; custom subcommands, e.g. `wip test` — see README
      #   test:
      #     type: exec # optional; exec (default, runs in the running container) | run (fresh container) | build
      #     command: bundle exec rspec # TODO
    YAML

    CONTAINER_TEMPLATE = <<~YAML.freeze
      version: 1
      mode: container # container | compose | compose-native (see README)
      container: app # TODO: rename freely, as long as it matches a key under dependencies: below

      wslc:
        command: auto # optional; wslc binary/path wip shells out to ("auto" resolves it for you)

      # network: my-network # optional; container network name (mode: container only)

      dependencies:
        app: # this is the container wip creates, execs into, and runs commands in
          image: your/image:tag # TODO: image to run
          workdir: /app # TODO: adjust to match your image, or delete this line
          interactive: false # optional; keep stdin open / allocate a tty
          remove: true # optional; remove the container after each run
          env: {} # optional; environment variables passed to the container, e.g. {FOO: bar}
          ports: [] # optional; published ports, e.g. ["3000:3000"]
          volumes: [] # optional; extra -v specs beyond the sync mounts below

      #{COMMANDS_EXAMPLE}

      sync: # optional; mirrors the source into a named volume instead of bind-mounting it live
      #{SYNC_EXTRAS}
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

    # Directory name wip.yml would resolve as its default compose.project (and, from
    # that, its network name) if left unset — shown as a comment so it's discoverable
    # without duplicating it as a live value that would go stale if the directory moves.
    def compose_project_default = Pathname(@dir).basename.to_s

    def compose_template
      <<~YAML
        version: 1
        mode: compose-native # wip parses #{compose_file} itself and drives wslc directly — no external
                              # compose-for-wslc binary needed. Prefer a real compose tool instead? Use
                              # mode: compose (see README "Compose mode") and set compose.command.

        wslc:
          command: auto # optional; wslc binary/path wip shells out to ("auto" resolves it for you)

        compose:
          service: app # TODO: which service in #{compose_file} wip run/exec/NAME target
          # file: #{compose_file} # optional; override which compose file wip parses (default: auto-detected)
          # project: #{compose_project_default} # optional; project/network name (default: this directory's name)
          # command: # only used under mode: compose (external compose-for-wslc binary); unused here under compose-native

        # network: is derived from compose.project above (or this directory's name) — setting it
        # directly conflicts with compose: (ConfigError), so it's intentionally left out here.

        #{COMMANDS_EXAMPLE}

        sync: # optional; mirrors the source into a named volume instead of bind-mounting it live
        #{SYNC_EXTRAS}
      YAML
    end
  end
end
