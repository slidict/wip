# frozen_string_literal: true
module Wip
  class ErrorInterpreter
    def initialize(architecture: Environment.new.architecture)
      @architecture = architecture
    end

    def interpret(output)
      case output
      when /pull access denied|insufficient_scope|authorization failed/i then registry_message
      when /no matching manifest for linux\/(?:amd64|arm64)/i then architecture_message
      end
    end

    private

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
