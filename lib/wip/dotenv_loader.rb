# frozen_string_literal: true

require 'pathname'

module Wip
  # Parses a .env file the way `docker compose` does, so values don't have to
  # be duplicated into wip.yml just to reach the container as -e flags.
  class DotenvLoader
    LINE_PATTERN = /\A(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=(.*)\z/

    def initialize(path)
      @path = Pathname(path)
    end

    def load
      return {} unless @path.file?

      @path.readlines.each_with_object({}) do |line, env|
        line = line.strip
        next if line.empty? || line.start_with?('#')

        match = LINE_PATTERN.match(line)
        next unless match

        env[match[1]] = unquote(match[2])
      end
    end

    private

    def unquote(value)
      value = value.strip
      return value[1..-2] if value.start_with?('"') && value.end_with?('"') && value.length >= 2
      return value[1..-2] if value.start_with?("'") && value.end_with?("'") && value.length >= 2

      value.split(/\s+#/, 2).first.to_s.strip
    end
  end
end
