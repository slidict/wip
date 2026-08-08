# frozen_string_literal: true

require 'pathname'

module Wip
  # Builds a starter wip.yml. Detects an existing compose file next to the
  # target (the same filenames ComposeBridge auto-detects) to decide between
  # mode: compose-native and mode: container, since that's the one decision
  # `wip init` can't leave to a placeholder.
  class Initializer
    # --template values wip init accepts, and the label used in the exclude: comment.
    TEMPLATE_LABELS = {
      'rails' => 'Rails',
      'node' => 'Node.js',
      'rust' => 'Rust',
      'csharp' => 'C#'
    }.freeze

    # sync.exclude patterns written live for each --template. They mirror that
    # stack's own github/gitignore template — directories that are either
    # regenerated inside the container or too large/irrelevant to mirror over rsync.
    TEMPLATE_EXCLUDES = {
      'rails' => %w[.git log/ tmp/ storage/ public/assets/ public/packs/ .bundle/ vendor/bundle/
                    coverage/ node_modules/],
      'node' => %w[.git node_modules/ dist/ build/ .next/ .cache/ coverage/],
      'rust' => %w[.git target/],
      'csharp' => %w[.git bin/ obj/ .vs/ packages/]
    }.freeze

    # Used when --template is omitted — the same starting point the README's own
    # example uses.
    FALLBACK_EXCLUDES = %w[.git tmp/ node_modules/].freeze

    # Appended to both templates after dependencies/compose. interaction: itself is left
    # commented since an empty interaction: {} is indistinguishable from omitting the key.
    COMMANDS_EXAMPLE = <<~YAML.chomp
      # optional; custom subcommands, e.g. `wip test` — see README
      # (interaction: is the primary key; commands: is accepted as an alias — the two are
      # mutually exclusive, so pick one)
      # interaction:
      #   test:
      #     optional; exec (default, runs in the running container) | run (fresh container) | build
      #     type: exec

      #     command: ls
    YAML

    def initialize(dir: Dir.pwd, template: nil)
      raise Error, "unknown --template #{template.inspect} (valid: #{TEMPLATE_LABELS.keys.join(', ')})" \
        if template && !TEMPLATE_LABELS.key?(template)

      @dir = dir
      @template = template
    end

    def compose? = !!compose_file

    def call
      compose? ? compose_template : container_template
    end

    private

    def compose_file
      @compose_file ||= ComposeBridge::FILENAMES.find { |name| File.file?(File.join(@dir, name)) }
    end

    # Directory name wip.yml would resolve as its default compose.project (and, from
    # that, its network name) if left unset — shown as a comment so it's discoverable
    # without duplicating it as a live value that would go stale if the directory moves.
    def compose_project_default = Pathname(@dir).basename.to_s

    def exclude_comment
      if @template
        "# optional; rsync --exclude patterns picked for --template #{@template} (#{TEMPLATE_LABELS[@template]})"
      else
        '# optional; rsync --exclude patterns, e.g. ["node_modules", "*.log"]'
      end
    end

    def exclude_list
      patterns = @template ? TEMPLATE_EXCLUDES.fetch(@template) : FALLBACK_EXCLUDES
      patterns.map { |pattern| "  - #{pattern}" }.join("\n")
    end

    # Appended to sync: in both templates. Every key here is either a plain constant
    # default (safe to state literally) or, where commented, a value SyncSettings
    # derives from another key (target from workdir, volume from the container/service
    # name) — those stay unset so they keep tracking that key instead of going stale.
    # A blank line separates every key from its neighbor; the gsub only indents
    # non-blank lines so those separators stay actual blank lines, not trailing
    # whitespace.
    def sync_extras
      <<~YAML.chomp.gsub(/^(?=.)/, '  ')
        # optional; host path to mirror (default: wip.yml directory)
        source: .

        # optional; in-container mount point for the mirror (default: the container's workdir, else /app)
        # target: /app

        # optional; read-only host mount inside the container
        mount: /host-src

        # optional; named volume holding the mirrored tree (default: "<container>-src")
        # volume: app-src

        # optional; rsync --delete so removals on the host are mirrored too
        delete: true

        #{exclude_comment}
        exclude:
        #{exclude_list}

        # optional; mirror binary to shell out to
        command: rsync

        # optional; extra flags appended to the mirror command
        options: []

        # optional; seconds between mirrors
        interval: 2

        # optional; exec (default, mirrors into the running container) | run (always a fresh container)
        mode: exec

        # sync.image/sync.build — required for mode: compose and used by sync.mode: run; see README "Source sync"
      YAML
    end

    def container_template
      <<~YAML
        version: 1

        # container | compose | compose-native (see README)
        mode: container

        # TODO: rename freely, as long as it matches a key under dependencies: below
        container: app

        wslc:
          # optional; wslc binary/path wip shells out to ("auto" resolves it for you)
          command: auto

        # optional; container network name (mode: container only)
        # network: my-network

        dependencies:
          # this is the container wip creates, execs into, and runs commands in
          app:
            # TODO: image to run
            image: your/image:tag

            # TODO: adjust to match your image, or delete this line
            workdir: /app

            # optional; keep stdin open / allocate a tty
            interactive: false

            # optional; remove the container after each run
            remove: true

            # optional; environment variables passed to the container, e.g. {FOO: bar}
            env: {}

            # optional; published ports, e.g. ["3000:3000"]
            ports: []

            # optional; extra -v specs beyond the sync mounts below
            volumes: []

        #{COMMANDS_EXAMPLE}

        # optional; mirrors the source into a named volume instead of bind-mounting it live
        sync:
        #{sync_extras}
      YAML
    end

    def compose_template
      <<~YAML
        version: 1

        # wip parses #{compose_file} itself and drives wslc directly — no external
        # compose-for-wslc binary needed. Prefer a real compose tool instead? Use
        # mode: compose (see README "Compose mode") and set compose.command.
        mode: compose-native

        wslc:
          # optional; wslc binary/path wip shells out to ("auto" resolves it for you)
          command: auto

        compose:
          # TODO: which service in #{compose_file} wip run/exec/NAME target
          service: app

          # optional; override which compose file wip parses (default: auto-detected)
          # file: #{compose_file}

          # optional; project/network name (default: this directory's name)
          # project: #{compose_project_default}

          # only used under mode: compose (external compose-for-wslc binary); unused here under compose-native
          # command:

        # network: is derived from compose.project above (or this directory's name) — setting it
        # directly conflicts with compose: (ConfigError), so it's intentionally left out here.

        #{COMMANDS_EXAMPLE}

        # optional; mirrors the source into a named volume instead of bind-mounting it live
        sync:
        #{sync_extras}
      YAML
    end
  end
end
