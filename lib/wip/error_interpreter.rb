# frozen_string_literal: true

module Wip
  # Translates raw WSLC error output into friendlier hints.
  class ErrorInterpreter
    # Shells report a missing rsync as "rsync: not found", while the container
    # runtime names the executable either before or after its own phrasing.
    RSYNC_MISSING = Regexp.union(
      /rsync: (?:command )?not found/i,
      /rsync[^\n]*executable file not found/i,
      /executable file not found[^\n]*rsync/i
    )

    def initialize(architecture: Environment.new.architecture)
      @architecture = architecture
    end

    def interpret(output)
      case output
      when /pull access denied|insufficient_scope|authorization failed/i then registry_message
      when %r{no matching manifest for linux/(?:amd64|arm64)}i then architecture_message
      when RSYNC_MISSING then rsync_message
      end
    end

    private

    def rsync_message
      <<~TEXT
        `wip sync` needs rsync inside the image.

        Install it in your Dockerfile:

          RUN apt-get update && apt-get install -y rsync

        Or point sync.command at a tool the image already has.
      TEXT
    end

    def registry_message
      <<~TEXT
        The container registry rejected the request.

        Try logging in with:

          wslc registry login -u <username> docker.io
      TEXT
    end

    def architecture_message
      <<~TEXT
        The image does not contain a manifest for the current CPU architecture.

        Current architecture:
          #{@architecture}

        Inspect the image with:

          docker buildx imagetools inspect <image>

        Rebuild and push a multi-platform image with:

          docker buildx build \\
            --platform linux/amd64,linux/arm64 \\
            -t <image> \\
            --push .
      TEXT
    end
  end
end
