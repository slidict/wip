# frozen_string_literal: true
require "pathname"
require "yaml"

module Wip
  class ConfigLoader
    FILENAME = "wip.yml"

    def initialize(start_dir: Dir.pwd, path: nil)
      @start_dir = Pathname(start_dir).expand_path
      @path = path
    end

    def find
      return Pathname(@path).expand_path if @path

      @start_dir.ascend do |directory|
        candidate = directory.join(FILENAME)
        return candidate if candidate.file?
      end
      nil
    end

    def load
      path = find
      raise ConfigError, "wip.yml was not found (searched from #{@start_dir} to the filesystem root)" unless path&.file?

      data = YAML.safe_load_file(path, permitted_classes: [], aliases: false) || {}
      Config.new(data, path: path)
    rescue Psych::Exception => e
      raise ConfigError, "Could not parse #{path}: #{e.message}"
    end
  end
end
