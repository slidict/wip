# frozen_string_literal: true

require 'spec_helper'
RSpec.describe Wip::ErrorInterpreter do
  subject(:interpreter) { described_class.new(architecture: 'linux/amd64') }
  it('classifies registry errors') {
    expect(interpreter.interpret('pull access denied')).to include('registry rejected')
  }
  it('classifies architecture errors') {
    expect(interpreter.interpret('no matching manifest for linux/amd64')).to include('linux/amd64', 'multi-platform')
  }
  it('classifies a missing rsync in the image') {
    expect(interpreter.interpret('sh: 1: rsync: not found')).to include('wip sync', 'apt-get install -y rsync')
  }
  it('classifies a missing rsync however the runtime words it') {
    expect(interpreter.interpret('exec: "rsync": executable file not found in $PATH')).to include('wip sync')
    expect(interpreter.interpret('executable file not found in $PATH: rsync')).to include('wip sync')
  }
end
