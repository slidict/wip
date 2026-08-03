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

    # Walks an already-parsed YAML structure and interpolates string values only —
    # real Compose interpolates YAML values, never mapping keys (its docs call this
    # out explicitly), and doing this after parsing means a substituted value can't
    # introduce YAML syntax (e.g. a literal "#" turning into a comment marker).
    def self.tree(value, env)
      case value
      when String then call(value, env)
      when Hash then value.transform_values { |v| tree(v, env) }
      when Array then value.map { |v| tree(v, env) }
      else value
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
