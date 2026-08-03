# frozen_string_literal: true

module Wip
  # Interpolates ${VAR} references the way `docker compose` does when reading
  # compose.yml: ${VAR}, ${VAR:-default}/${VAR-default}, bare $VAR, and $$ as an
  # escaped literal dollar sign. ${VAR:?err}/${VAR:+alt} aren't recognized by
  # PATTERN at all, so — unlike an unset $VAR/${VAR}, which this resolves to an
  # empty string — they pass through completely unchanged, "${...}" and all.
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
    #
    # `seen` tracks Hash/Array objects on the current recursion path (by identity,
    # not #==) so a self-referential YAML alias — ComposeFile.load parses with
    # aliases: true — raises instead of recursing until SystemStackError. The same
    # object reached again from a *different* branch (an anchor reused, not a
    # cycle) is fine: it's removed from `seen` once its own subtree finishes.
    def self.tree(value, env, seen = {}.compare_by_identity)
      case value
      when String then call(value, env)
      when Hash, Array then walk_container(value, env, seen)
      else value
      end
    end

    def self.walk_container(value, env, seen)
      raise ConfigError, 'compose.yml contains a self-referential YAML alias' if seen.key?(value)

      seen[value] = true
      begin
        value.is_a?(Hash) ? value.transform_values { |v| tree(v, env, seen) } : value.map { |v| tree(v, env, seen) }
      ensure
        seen.delete(value)
      end
    end
    private_class_method :walk_container

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
