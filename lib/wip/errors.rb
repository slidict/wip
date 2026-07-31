# frozen_string_literal: true
module Wip
  class Error < StandardError; end
  class ConfigError < Error; end
  class CommandNotFoundError < Error; end
end
