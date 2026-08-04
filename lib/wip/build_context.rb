# frozen_string_literal: true

require 'find'
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

    # on_progress fires before copying and after each file, as `|count, total|`,
    # so a caller can report elapsed progress even while copying one large file.
    def stage(on_progress: nil)
      return yield @root.to_s if @ignore.empty?

      Dir.mktmpdir('wip-build-context-') do |dir|
        copy_included_files(Pathname(dir), on_progress)
        yield dir
      end
    end

    private

    def copy_included_files(destination, on_progress)
      files = included_files
      on_progress&.call(0, files.size)
      files.each_with_index do |relative_path, index|
        target = destination.join(relative_path)
        FileUtils.mkdir_p(target.dirname)
        # Keep links as links. Dereferencing a link here could copy arbitrary
        # host files outside the build context (for example, ~/.ssh/id_rsa)
        # into the staged directory and expose them to the image build.
        FileUtils.copy_entry(@root.join(relative_path), target, false, false, false)
        on_progress&.call(index + 1, files.size)
      end
    end

    # Walks the tree by hand (rather than Dir.glob-ing it all up front) so an
    # ignored directory — node_modules, vendor/bundle, a multi-gigabyte
    # storage/ — is never descended into just to be thrown away afterward.
    def included_files
      result = []
      Find.find(@root.to_s) do |path|
        next if path == @root.to_s

        entry = Pathname(path).relative_path_from(@root).to_s
        result << entry if visit?(path, entry)
      end
      result
    end

    # Returns whether `path` belongs in the staged context, pruning the walk
    # when it's an ignored directory so nothing under it is even visited.
    def visit?(path, entry)
      ignored = @ignore.ignored?(entry)
      return !ignored unless !File.symlink?(path) && File.directory?(path)

      Find.prune if ignored
      false
    end
  end
end
