# frozen_string_literal: true

require 'fileutils'
require 'pathname'
require 'tmpdir'

module Wip
  # Stages a build context into a scratch directory with anything matched by
  # .dockerignore left out, since wslc build (unlike `docker build`) sends the
  # context as-is instead of filtering it itself.
  class BuildContext
    def initialize(context, ignore: nil)
      @root = Pathname(context).expand_path
      @ignore = ignore || DockerIgnore.load(@root.join('.dockerignore'))
    end

    def stage
      return yield @root.to_s if @ignore.empty?

      Dir.mktmpdir('wip-build-context-') do |dir|
        copy_included_files(Pathname(dir))
        yield dir
      end
    end

    private

    def copy_included_files(destination)
      each_included_file do |relative_path|
        target = destination.join(relative_path)
        FileUtils.mkdir_p(target.dirname)
        # Keep links as links. Dereferencing a link here could copy arbitrary
        # host files outside the build context (for example, ~/.ssh/id_rsa)
        # into the staged directory and expose them to the image build.
        FileUtils.copy_entry(@root.join(relative_path), target, false, false, false)
      end
    end

    def each_included_file
      Dir.glob('**/*', File::FNM_DOTMATCH, base: @root.to_s).each do |entry|
        next if %w[. ..].include?(entry)

        path = @root.join(entry)
        next unless path.file? || path.symlink?
        next if @ignore.ignored?(entry)

        yield entry
      end
    end
  end
end
