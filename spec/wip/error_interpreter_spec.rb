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
  it('suggests freeing volumes and restarting a volume-limited WSLC session') {
    message = interpreter.interpret(
      'マウントされているボリュームが多すぎます (上限: 15)。エラー コード: 0x8007000e'
    )

    expect(message).to include(
      'wslc container list',
      'wslc container stop <container-name>',
      'wslc system session terminate'
    )
  }
  it('recognizes the mounted-volume limit without a localized error description') {
    expect(interpreter.interpret('Error code: 0X8007000E')).to include('mounted-volume limit')
    expect(interpreter.interpret('Too many mounted volumes (limit: 15)')).to include('mounted-volume limit')
  }
end
