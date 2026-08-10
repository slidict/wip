# frozen_string_literal: true

require 'spec_helper'
begin
  require 'pty'
rescue LoadError
  # PTY is unavailable on native Windows; PTY-dependent examples are skipped below
end

RSpec.describe Wip::CommandRunner do
  it 'returns the external command exit status' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 23'])).to eq(23)
  end

  it 'inherits real stdio for interactive commands instead of piping (which would close stdin)' do
    expect(described_class.new.run([RbConfig.ruby, '-e', 'exit 7'], interactive: true)).to eq(7)
  end

  it 'runs the command inside chdir instead of the process working directory' do
    Dir.mktmpdir do |dir|
      stdout = StringIO.new
      runner = described_class.new(stdout: stdout)
      script = 'print Dir.pwd'

      runner.run([RbConfig.ruby, '-e', script], chdir: dir)

      expect(stdout.string).to eq(File.realpath(dir))
    end
  end

  it 'runs an interactive command inside chdir too' do
    Dir.mktmpdir do |dir|
      marker = File.join(dir, 'marker')
      script = "File.write('marker', Dir.pwd)"

      described_class.new.run([RbConfig.ruby, '-e', script], interactive: true, chdir: dir)

      expect(File.read(marker)).to eq(File.realpath(dir))
    end
  end

  it 'reports a hint for an interactive command too, unlike the plain fd-inheriting attach it replaced' do
    stdout = StringIO.new
    stderr = StringIO.new
    runner = described_class.new(stdout: stdout, stderr: stderr)
    script = <<~RUBY
      STDOUT.write('too many mounted volumes')
      exit 1
    RUBY

    status = runner.run([RbConfig.ruby, '-e', script], interactive: true)

    expect(status).to eq(1)
    expect(stderr.string).to include('mounted-volume limit')
  end

  it 'propagates a terminal resize (SIGWINCH) to the child pty while attached', skip: !defined?(PTY) do
    PTY.open do |master, slave|
      slave.winsize = [24, 80]
      runner = described_class.new(stdin: slave, stdout: slave, stderr: StringIO.new)
      script = 'sleep 0.4; require "io/console"; STDOUT.write($stdout.winsize.join(","))'

      thread = Thread.new { runner.run([RbConfig.ruby, '-e', script], interactive: true) }
      sleep 0.1
      slave.winsize = [50, 120] # simulates the terminal emulator resizing wip's own pty
      Process.kill('WINCH', Process.pid) # simulates the kernel notifying wip of that resize
      thread.join

      expect(master.read_nonblock(4096)).to include('50,120')
      master.close
    end
  end

  it 'puts a real controlling terminal in raw mode and syncs the pty size, without raising', skip: !defined?(PTY) do
    PTY.open do |master, slave|
      runner = described_class.new(stdin: slave, stdout: slave, stderr: StringIO.new)

      status = runner.run([RbConfig.ruby, '-e', 'exit 0'], interactive: true)

      expect(status).to eq(0)
      master.close
    end
  end

  it 'falls back to inheriting real stdio on native Windows, where the pty gem is unavailable' do
    allow(Gem).to receive(:win_platform?).and_return(true)
    runner = described_class.new
    expect(runner).to receive(:run_inherited).and_call_original
    expect(runner).not_to receive(:run_attached)

    expect(runner.run([RbConfig.ruby, '-e', 'exit 9'], interactive: true)).to eq(9)
  end

  it 'forwards real IO stdio (not just the process defaults) on the Windows fallback', skip: !defined?(PTY) do
    allow(Gem).to receive(:win_platform?).and_return(true)

    PTY.open do |master, slave|
      runner = described_class.new(stdin: slave, stdout: slave, stderr: slave)

      status = runner.run([RbConfig.ruby, '-e', "STDOUT.write('hello-from-child')"], interactive: true)

      expect(status).to eq(0)
      expect(master.read_nonblock(4096)).to include('hello-from-child')
      master.close
    end
  end

  it 'runs the Windows fallback inside chdir too' do
    allow(Gem).to receive(:win_platform?).and_return(true)

    Dir.mktmpdir do |dir|
      marker = File.join(dir, 'marker')
      script = "File.write('marker', Dir.pwd)"

      described_class.new.run([RbConfig.ruby, '-e', script], interactive: true, chdir: dir)

      expect(File.read(marker)).to eq(File.realpath(dir))
    end
  end

  it 'returns 130 and reports the interrupt instead of raising when Ctrl-C hits while waiting on pump threads' do
    raised = false
    allow_any_instance_of(Thread).to receive(:join).and_wrap_original do |original, *args|
      next original.call(*args) if raised

      raised = true
      raise Interrupt
    end

    stderr = StringIO.new
    runner = described_class.new(stderr: stderr)
    result = nil
    command = [RbConfig.ruby, '-e', 'puts "x" * 4096; sleep 0.1']

    expect { result = runner.run(command) }.not_to raise_error
    expect(result).to eq(130)
    expect(stderr.string).to include('wip: interrupted')
  end

  it 'interprets output containing invalid UTF-8 bytes instead of raising an encoding error' do
    stdout = StringIO.new(+''.b)
    stderr = StringIO.new
    runner = described_class.new(stdout: stdout, stderr: stderr)
    script = <<~'RUBY'
      STDOUT.binmode
      STDOUT.write("\xFF\xFE".b)
      STDOUT.write('too many mounted volumes')
      exit 1
    RUBY

    status = nil
    expect do
      status = runner.run([RbConfig.ruby, '-e', script])
    end.not_to raise_error
    expect(status).to eq(1)
    expect(stderr.string).to include('mounted-volume limit')
  end

  it "doesn't let a pump thread's IOError escape when its stream is closed early" do
    source = Object.new
    def source.readpartial(_size)
      raise IOError, 'stream closed in another thread'
    end

    thread = described_class.new.send(:pump, source, StringIO.new, +'')

    expect { thread.join }.not_to raise_error
  end
end
