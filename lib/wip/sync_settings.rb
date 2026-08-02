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
    # Minimal set for a fast local-to-local mirror: -r walks the tree, -l
    # keeps symlinks as symlinks, -t preserves mtimes so re-syncs can quick-
    # check (size+mtime) instead of re-transferring unchanged files, and
    # --whole-file skips the delta-transfer checksum pass that only pays off
    # over a slow network. Owner/group/perm preservation (-o -g -p, part of
    # -a) is left out since both sides are the same user; add them back via
    # sync.options if a project needs them.
    BASE_OPTIONS = %w[-r -l -t --whole-file].freeze
    # Trailing mount options wslc/docker accept after the container path.
    VOLUME_MODES = %w[ro rw z Z cached delegated consistent].freeze
    # exec mirrors inside the already-running, wip-managed container (fast,
    # but only correct when wip itself created that container with the sync
    # mounts attached). run always mirrors from a disposable container, since
    # compose owns its own services' mounts and never guarantees that shape.
    SYNC_MODES = %w[exec run].freeze

    attr_reader :target, :mount, :volume, :exclude, :binary, :extra_options, :interval, :mode, :image

    def initialize(raw, base: nil, workdir: nil, container: nil, compose: false)
      raise ConfigError, 'sync must be a mapping' unless raw.is_a?(Hash)

      @base = base
      assign_paths(raw, workdir: workdir, container: container)
      assign_mirror(raw)
      assign_mode(raw, compose: compose)
      validate!
    end

    def delete? = !!@delete
    def exec? = mode == 'exec'

    # Expanded against the wip.yml directory so the mirror covers the same tree
    # no matter which subdirectory wip was invoked from.
    def source
      @source ||= @base ? Pathname(@base).join(@raw_source).expand_path.to_s : @raw_source
    end

    # What `-v` specs the main container needs: the source read-only, and the
    # named volume where the app actually runs.
    def volume_specs = ["#{source}:#{mount}:ro", "#{volume}:#{target}"]

    # True for a configured volume that sync replaces, so `.:/app` in the
    # primary container's `volumes` quietly becomes the read-only mount plus the volume.
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
        'interval' => interval, 'mode' => mode, 'image' => image }
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

    def assign_mode(raw, compose:)
      @mode = presence(raw['mode']) || (compose ? 'run' : 'exec')
      @image = presence(raw['image'])
      raise ConfigError, "sync.mode must be one of #{SYNC_MODES.join(', ')}" unless SYNC_MODES.include?(@mode)

      validate_image!(compose)
      return unless compose && exec?

      raise ConfigError, 'sync.mode: exec needs mode: container (compose owns its services’ mounts, ' \
                         'so it can’t guarantee the running container has the sync mounts attached)'
    end

    # compose mode has no dependencies: entry to fall back to for the mirror
    # container's image, so sync.image can't be left implicit there.
    def validate_image!(compose)
      return if !compose || @image

      raise ConfigError, 'sync.image is required under mode: compose (there’s no dependencies: entry ' \
                         'to borrow the mirror container’s image from)'
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
