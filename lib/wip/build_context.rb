# frozen_string_literal: true

require 'digest'
require 'fileutils'
require 'find'
require 'json'
require 'pathname'
require 'tmpdir'

module Wip
  # Filters build contexts for wslc and keeps WSL-hosted sources in a fast,
  # persistent Windows-side shadow directory.
  class BuildContext
    def initialize(context, ignore: nil, environment: Environment.new, shadow_root: nil)
      @root = Pathname(context).expand_path
      @ignore = ignore || DockerIgnore.load(@root.join('.dockerignore'))
      @environment = environment
      @shadow_root = validated_shadow_root(shadow_root) if shadow_root
    end

    # on_progress fires before copying and after each file, as `|count, total|`,
    # so a caller can report elapsed progress even while copying one large file.
    def stage(on_progress: nil, &block)
      return stage_shadow(on_progress, &block) if shadow_required?
      return block.call(@root.to_s) if @ignore.empty?

      Dir.mktmpdir('wip-build-context-') do |dir|
        copy_included_files(Pathname(dir), on_progress)
        block.call(dir)
      end
    end

    # Whether the upcoming stage will use the Windows-side shadow directory
    # (configured shadow_root, on WSL2, with a context outside /mnt) rather
    # than staging in place or under a WSL-side tmpdir.
    def shadow? = shadow_required?

    private

    # A shadow root under the context would itself be walked by included_files
    # on the next build and copied into itself at ever-deeper paths, so the
    # cache would grow without bound and the build would eventually fail.
    def validated_shadow_root(shadow_root)
      root = Pathname(shadow_root).expand_path
      if root == @root || root.to_s.start_with?("#{@root}/")
        raise ConfigError, "shadow_context (#{root}) must not be inside the build context (#{@root})"
      end

      root
    end

    def shadow_required?
      return false unless @shadow_root

      @environment.wsl2? && !@root.to_s.match?(%r{\A/mnt/[a-z](?:/|\z)}i)
    end

    # Keep one stable Windows-side context per source path. Its manifest lives
    # beside (rather than inside) the context so it is never sent to wslc.
    def stage_shadow(on_progress)
      key = Digest::SHA256.hexdigest(@root.to_s)
      cache = @shadow_root.join(key)
      context = cache.join('context')
      FileUtils.mkdir_p(cache)

      File.open(cache.join('lock'), File::RDWR | File::CREAT, 0o600) do |lock|
        lock.flock(File::LOCK_EX)
        synchronize_shadow(context, cache.join('manifest.json'), on_progress)
        # Keep the shadow immutable until wslc has finished reading it.
        yield context.to_s
      end
    end

    def synchronize_shadow(context, manifest_path, on_progress)
      current = included_files.to_h { |entry| [entry, fingerprint(@root.join(entry))] }
      previous = previous_manifest(context, manifest_path)
      changed = current.keys.reject { |entry| current[entry] == previous[entry] }
      removed = previous.keys - current.keys

      apply_shadow_changes(context, changed, removed, on_progress)
      FileUtils.mkdir_p(context)
      write_manifest(manifest_path, current)
    end

    # A context we can't describe is a context we can't update incrementally:
    # with no manifest there is no way to tell which of its entries are stale,
    # so it gets discarded and rebuilt rather than left holding deleted or
    # newly ignored files.
    def previous_manifest(context, manifest_path)
      return {} unless context.directory?

      manifest = load_manifest(manifest_path)
      return manifest if manifest

      FileUtils.rm_rf(context)
      {}
    end

    def apply_shadow_changes(context, changed, removed, on_progress)
      total = changed.size + removed.size
      on_progress&.call(0, total)

      removed.each_with_index do |entry, index|
        FileUtils.rm_rf(context.join(entry))
        prune_empty_parents(context.join(entry).dirname, context)
        on_progress&.call(index + 1, total)
      end
      changed.each_with_index do |entry, index|
        copy_entry_atomically(@root.join(entry), context.join(entry))
        on_progress&.call(removed.size + index + 1, total)
      end
    end

    # Returns nil — not an empty manifest — when the manifest is missing,
    # unreadable, or not a manifest at all, so callers can tell "nothing was
    # synced yet" apart from "we no longer know what was synced".
    def load_manifest(path)
      manifest = JSON.parse(path.read)
      manifest if manifest.is_a?(Hash)
    rescue JSON::ParserError, SystemCallError
      nil
    end

    def write_manifest(path, contents)
      temporary = Pathname("#{path}.tmp-#{Process.pid}")
      temporary.write(JSON.generate(contents))
      File.rename(temporary, path)
    ensure
      FileUtils.rm_f(temporary) if temporary
    end

    def fingerprint(path)
      stat = path.lstat
      if stat.symlink?
        { 'type' => 'link', 'target' => path.readlink.to_s }
      else
        { 'type' => 'file', 'size' => stat.size, 'mtime_ns' => stat.mtime.nsec + (stat.mtime.to_i * 1_000_000_000),
          'mode' => stat.mode }
      end
    end

    # preserve: true keeps the source mode, so an executable stays executable
    # even when the shadow lives on a DrvFs mount whose fmask would otherwise
    # strip the bit and break a `RUN ./script` in the image build.
    def copy_entry_atomically(source, target)
      FileUtils.mkdir_p(target.dirname)
      temporary = target.dirname.join(".#{target.basename}.wip-#{Process.pid}")
      FileUtils.rm_rf(temporary)
      FileUtils.copy_entry(source, temporary, true, false, false)
      replace_atomically(temporary, target)
    ensure
      FileUtils.rm_rf(temporary) if temporary
    end

    # rename replaces an existing entry in a single step, so an interrupted
    # update leaves the previous copy in place instead of no copy at all. Only
    # a target rename can't overwrite — a directory where a file now lives, or
    # a filesystem without overwrite semantics — needs the unsafe fallback.
    def replace_atomically(temporary, target)
      File.rename(temporary, target)
    rescue Errno::EISDIR, Errno::ENOTDIR, Errno::ENOTEMPTY, Errno::EEXIST, Errno::EPERM, Errno::EACCES
      FileUtils.rm_rf(target)
      File.rename(temporary, target)
    end

    def prune_empty_parents(directory, root)
      while empty_descendant?(directory, root)
        directory.rmdir
        directory = directory.dirname
      end
    end

    def empty_descendant?(directory, root)
      directory != root && directory.to_s.start_with?("#{root}/") &&
        directory.directory? && directory.children.empty?
    end

    def copy_included_files(destination, on_progress)
      files = included_files
      on_progress&.call(0, files.size)
      files.each_with_index do |relative_path, index|
        target = destination.join(relative_path)
        FileUtils.mkdir_p(target.dirname)
        # Keep links as links. Dereferencing a link here could copy arbitrary
        # host files outside the build context (for example, ~/.ssh/id_rsa)
        # into the staged directory and expose them to the image build.
        FileUtils.copy_entry(@root.join(relative_path), target, true, false, false)
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
    # when it's an ignored directory so nothing under it is even visited —
    # unless a later negated rule could still re-include something beneath it.
    def visit?(path, entry)
      ignored = @ignore.ignored?(entry)
      if !File.symlink?(path) && File.directory?(path)
        Find.prune if ignored && @ignore.prunable?(entry)
        return false
      end

      !ignored && (File.file?(path) || File.symlink?(path))
    end
  end
end
