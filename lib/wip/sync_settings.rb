# frozen_string_literal: true

require 'pathname'

module Wip
  # Validated access to the `sync:` block, which mirrors the host source tree
  # into a named volume instead of bind-mounting it live.
  #
  # A bind-mounted app directory is shared into the container's VM over
  # virtiofs, so every stat/open a boot-time directory scan makes is a round
  # trip. Mirroring the tree into a named volume (native storage inside the VM)
  # and re-running the mirror on demand keeps the edit-on-the-host workflow
  # while leaving the running app on fast disk.
  class SyncSettings
    DEFAULT_MOUNT = '/host-src'
    DEFAULT_TARGET = '/app'
    DEFAULT_BINARY = 'rsync'
    DEFAULT_INTERVAL = 2
    # -a preserves modes and timestamps so file watchers and bundler see a
    # faithful copy rather than a tree that looks freshly written every sync.
    BASE_OPTIONS = %w[-a].freeze
    # Trailing mount options wslc/docker accept after the container path.
    VOLUME_MODES = %w[ro rw z Z cached delegated consistent].freeze

    attr_reader :target, :mount, :volume, :exclude, :binary, :extra_options, :interval

    def initialize(raw, base: nil, workdir: nil, container: nil)
      raise ConfigError, 'sync must be a mapping' unless raw.is_a?(Hash)

      @base = base
      assign_paths(raw, workdir: workdir, container: container)
      assign_mirror(raw)
      validate!
    end

    def delete? = !!@delete

    # Expanded against the wip.yml directory so the mirror covers the same tree
    # no matter which subdirectory wip was invoked from.
    def source
      @source ||= @base ? Pathname(@base).join(@raw_source).expand_path.to_s : @raw_source
    end

    # What `-v` specs the main container needs: the source read-only, and the
    # named volume where the app actually runs.
    def volume_specs = ["#{source}:#{mount}:ro", "#{volume}:#{target}"]

    # True for a configured volume that sync replaces, so `.:/app` in
    # `defaults.volumes` quietly becomes the read-only mount plus the volume.
    def replaces?(spec)
      [target.chomp('/'), mount.chomp('/')].include?(container_path(spec))
    end

    # Trailing slashes matter to rsync: they copy the *contents* of the mount
    # into the target rather than nesting it one directory deeper.
    def mirror_command
      command = [binary, *BASE_OPTIONS]
      command << '--delete' if delete?
      command.concat(exclude.map { |pattern| "--exclude=#{pattern}" })
      command.concat(extra_options)
      command.push("#{mount.chomp('/')}/", "#{target.chomp('/')}/")
    end

    def to_h
      { 'source' => source, 'target' => target, 'mount' => mount, 'volume' => volume,
        'delete' => delete?, 'exclude' => exclude, 'command' => binary, 'options' => extra_options,
        'interval' => interval }
    end

    private

    def assign_paths(raw, workdir:, container:)
      @raw_source = presence(raw['source']) || '.'
      @target = presence(raw['target']) || presence(workdir) || DEFAULT_TARGET
      @mount = presence(raw['mount']) || DEFAULT_MOUNT
      @volume = presence(raw['volume']) || "#{presence(container) || 'wip'}-src"
    end

    def assign_mirror(raw)
      @delete = raw.fetch('delete', true)
      @exclude = Array(raw['exclude']).map(&:to_s)
      @binary = presence(raw['command']) || DEFAULT_BINARY
      @extra_options = Array(raw['options']).map(&:to_s)
      @interval = raw.key?('interval') ? raw['interval'] : DEFAULT_INTERVAL
    end

    def validate!
      raise ConfigError, 'sync.target must be an absolute path' unless @target.start_with?('/')
      raise ConfigError, 'sync.mount must be an absolute path' unless @mount.start_with?('/')
      raise ConfigError, 'sync.mount must differ from sync.target' if @mount.chomp('/') == @target.chomp('/')
      return if @interval.is_a?(Numeric) && @interval.positive?

      raise ConfigError, 'sync.interval must be a positive number'
    end

    def container_path(spec)
      parts = spec.to_s.split(':')
      parts.pop if parts.size > 2 && VOLUME_MODES.include?(parts.last)
      parts.last.to_s.chomp('/')
    end

    def presence(value) = value.to_s.empty? ? nil : value.to_s
  end
end
