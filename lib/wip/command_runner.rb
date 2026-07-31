# frozen_string_literal: true
require "open3"
require "shellwords"

module Wip
  class CommandRunner
    def initialize(stdin: $stdin, stdout: $stdout, stderr: $stderr, interpreter: ErrorInterpreter.new)
      @stdin = stdin
      @stdout = stdout
      @stderr = stderr
      @interpreter = interpreter
    end

    def run(command, env: {})
      @stderr.puts "+ #{Shellwords.join(command)}" if ENV["WIP_DEBUG"]
      captured = +""
      status = nil
      Open3.popen3(env, *command) do |input, output, error, wait|
        input.close
        threads = [pump(output, @stdout, captured), pump(error, @stderr, captured)]
        threads.each(&:join)
        status = wait.value
      end
      hint = @interpreter.interpret(captured)
      @stderr.puts("\n#{hint}") if !status.success? && hint
      status.exitstatus
    rescue Errno::ENOENT => e
      @stderr.puts e.message
      127
    end

    private

    def pump(source, destination, captured)
      Thread.new do
        source.each(4096) do |chunk|
          destination.write(chunk)
          captured << chunk
        end
      end
    end
  end
end
