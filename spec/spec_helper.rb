# frozen_string_literal: true
require "tmpdir"
require "wip"
RSpec.configure do |config|
  config.disable_monkey_patching!
  config.order = :random
end
