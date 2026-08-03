# frozen_string_literal: true

module Wip
  # Interpolates ${VAR} references the way `docker compose` does when reading
  # compose.yml: ${VAR}, ${VAR:-default}/${VAR-default}, bare $VAR, and $$ as an
  # escaped literal dollar sign. ${VAR:?err}/${VAR:+alt} aren't recognized and
  # pass through untouched, same as an unset $VAR with no default.
  module VariableInterpolation
    PATTERN = /\$\$|\$\{([A-Za-z_][A-Za-z0-9_]*)((:-|-)([^}]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)/

    def self.call(text, env)
      text.gsub(PATTERN) do |matched|
        next '$' if matched == '$$'

        name, _, operator, default, bare_name = Regexp.last_match.captures
        resolve(env[name || bare_name], operator, default)
      end
    end

    def self.resolve(value, operator, default)
      case operator
      when ':-' then value.nil? || value.empty? ? default.to_s : value
      when '-' then value.nil? ? default.to_s : value
      else value.to_s
      end
    end
    private_class_method :resolve
  end
end
