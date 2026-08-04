# frozen_string_literal: true

require 'pathname'

module Wip
  # Parses a .dockerignore file and decides whether a build-context-relative
  # path should be excluded, following the same pattern rules as the Docker
  # CLI (gitignore-like globs, later rules override earlier ones, `!` negates).
  class DockerIgnore
    Rule = Struct.new(:pattern, :negate)

    FNMATCH_FLAGS = File::FNM_PATHNAME | File::FNM_DOTMATCH | File::FNM_EXTGLOB

    def self.load(path)
      path = Pathname(path)
      return new([]) unless path.file?

      new(path.readlines)
    end

    def initialize(lines)
      @rules = lines.filter_map { |line| parse(line) }
    end

    def empty? = @rules.empty?

    def ignored?(relative_path)
      @rules.reduce(false) do |ignored, rule|
        matches?(rule.pattern, relative_path) ? !rule.negate : ignored
      end
    end

    # Whether a walker can stop descending into an ignored directory without
    # risking a miss: false if some later negated rule (e.g. `!node_modules/pkg`
    # after `node_modules`) could still match something beneath it.
    def prunable?(relative_path)
      dir_components = relative_path.split('/')
      @rules.none? { |rule| rule.negate && targets_descendant?(rule.pattern, dir_components) }
    end

    private

    def targets_descendant?(pattern, dir_components)
      pattern_components = pattern.split('/')
      return true if pattern_components.first == '**'
      return false if pattern_components.size <= dir_components.size

      pattern_components.first(dir_components.size).each_with_index.all? do |component, i|
        File.fnmatch(component, dir_components[i], FNMATCH_FLAGS)
      end
    end

    def parse(line)
      line = line.strip
      return nil if line.empty? || line.start_with?('#')

      negate = line.start_with?('!')
      pattern = negate ? line[1..] : line
      pattern = pattern.delete_suffix('/')
      anchored = pattern.start_with?('/')
      pattern = pattern.delete_prefix('/')
      pattern = "**/#{pattern}" if !anchored && !pattern.include?('/')
      Rule.new(pattern, negate)
    end

    # A match on a directory component also excludes everything under it,
    # so every prefix of the path (not just the full path) is tested.
    def matches?(pattern, relative_path)
      components = relative_path.split('/')
      (1..components.size).any? do |i|
        File.fnmatch(pattern, components[0...i].join('/'), FNMATCH_FLAGS)
      end
    end
  end
end
