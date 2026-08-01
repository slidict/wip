# frozen_string_literal: true

require 'shellwords'

module Wip
  # Renders a command array for debug output, masking `-e KEY=value` env values
  # so secrets from wip.yml never reach logs.
  module CommandDisplay
    def self.for_debug(command)
      masked = command.each_cons(2).with_index.each_with_object(command.dup) do |((flag, pair), index), result|
        next unless flag == '-e'

        key, = pair.split('=', 2)
        result[index + 1] = "#{key}=***"
      end
      Shellwords.join(masked)
    end
  end
end
