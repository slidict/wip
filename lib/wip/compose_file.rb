# frozen_string_literal: true

require 'yaml'
require 'pathname'
require 'shellwords'

module Wip
  # Parses compose.yml/docker-compose.yml into the shape mode: compose-native
  # needs to drive `wslc` directly, in place of an external compose-for-wslc
  # binary (see ComposeBridge/mode: compose).
  #
  # This exists only because `wslc` has no native Compose support yet
  # (tracked upstream in microsoft/WSL#40948). Delete this file — and its
  # hooks in config.rb, cli.rb, doctor.rb and initializer.rb (see README
  # "Compose mode (native)") — once wslc ships that support, or a
  # compose-for-wslc tool reliably supports `run`.
  class ComposeFile
    Service = Struct.new(:image, :build, :command, :env, :ports, :volumes, :workdir, :user, :depends_on,
                         keyword_init: true)

    SERVICE_KEYS = %w[image build command environment ports volumes working_dir user depends_on].freeze
    # Real Compose keys that read as meaningful here but have nothing to map onto: TTY/stdin
    # allocation is already decided per invocation (see CommandBuilder#tty?), not fixed per
    # service; there's no per-service network to join beyond the one project network `network`
    # (config.rb) already puts every service on; `wslc run`/`exec` has no restart-policy or
    # capability flag to forward `restart:`/`cap_add:` to; and `profiles:` gates which services a
    # real `docker compose up` starts by default, which doesn't apply here since wip never starts
    # "every service" — only `compose.service` and whatever it reaches via `depends_on`.
    IGNORED_SERVICE_KEYS = %w[tty stdin_open networks restart cap_add profiles].freeze
    BUILD_KEYS = %w[context dockerfile args].freeze
    SUPPORTED_CONDITIONS = %w[service_started].freeze

    def self.load(path)
      raw = YAML.safe_load_file(path, aliases: true)
      raise ConfigError, "#{path}: services: must be a mapping" unless raw.is_a?(Hash) && raw['services'].is_a?(Hash)

      new(raw['services'], path: path)
    rescue Psych::Exception => e
      raise ConfigError, "Could not parse #{path}: #{e.message}"
    rescue Errno::ENOENT
      raise ConfigError, "Compose file not found: #{path}"
    end

    def initialize(services, path:)
      @path = path
      @services = services.to_h { |name, entry| [name.to_s, build_service(name.to_s, entry)] }
      validate_depends_on!
      @order = topological_order
    end

    def service_names_in_dependency_order = @order.dup

    # name => {context:, dockerfile:, tag:} for every service with a build:.
    def build_specs
      @order.filter_map do |name|
        service = @services.fetch(name)
        next unless service.build

        [name, service.build.merge('tag' => image_tag(name, service))]
      end.to_h
    end

    # Shaped like Config::DEPENDENCY_DEFAULTS expects: image/command/env/ports/volumes/workdir,
    # in dependency order so callers iterating sidecars start them before their dependents.
    def to_dependencies_hash
      @order.to_h do |name|
        service = @services.fetch(name)
        [name, { 'image' => service.build ? image_tag(name, service) : service.image, 'command' => service.command,
                 'env' => service.env, 'ports' => service.ports, 'volumes' => service.volumes,
                 'workdir' => service.workdir, 'user' => service.user }]
      end
    end

    private

    # A service naming both `build:` and `image:` builds via the former and tags the
    # result with the latter (real Compose's own rule for that combination), instead
    # of the auto-generated tag a build:-only service gets.
    def image_tag(name, service) = service.image || "wip-compose-#{name}:latest"

    def build_service(name, entry)
      raise ConfigError, "#{@path}: services.#{name} must be a mapping" unless entry.is_a?(Hash)

      unknown = entry.keys.map(&:to_s) - SERVICE_KEYS - IGNORED_SERVICE_KEYS
      raise ConfigError, "#{@path}: services.#{name} has unsupported key(s): #{unknown.join(', ')}" if unknown.any?

      image, build = image_or_build(name, entry)
      Service.new(image: image, build: build, command: normalize_command(entry['command']),
                  env: normalize_env(name, entry['environment']), **normalize_service_lists(name, entry),
                  workdir: presence(entry['working_dir']), user: presence(entry['user']),
                  depends_on: normalize_depends_on(name, entry['depends_on']))
    end

    def normalize_service_lists(name, entry)
      { ports: normalize_list(name, entry['ports'], 'ports'),
        volumes: normalize_list(name, entry['volumes'], 'volumes') }
    end

    def image_or_build(name, entry)
      image = presence(entry['image'])
      build = normalize_build(name, entry['build'])
      raise ConfigError, "#{@path}: services.#{name} must set image or build" unless image || build

      [image, build]
    end

    # Compose allows both shell form ("bin/rails s") and exec form (["bin/rails", "s"]);
    # CommandBuilder re-splits whatever's here with Shellwords, so join exec form back into
    # one string instead of letting Array#to_s (via presence) turn it into a literal
    # "[\"bin/rails\", \"s\"]" that Shellwords would then split wrong.
    def normalize_command(value)
      return presence(value) unless value.is_a?(Array)

      presence(Shellwords.join(value.map(&:to_s)))
    end

    def normalize_build(name, value)
      return nil unless value

      case value
      when String then { 'context' => resolve_context(value) }
      when Hash then normalize_build_mapping(name, value)
      else raise ConfigError, "#{@path}: services.#{name}.build must be a string or mapping"
      end
    end

    def normalize_build_mapping(name, value)
      unknown = value.keys.map(&:to_s) - BUILD_KEYS
      if unknown.any?
        raise ConfigError,
              "#{@path}: services.#{name}.build has unsupported key(s): #{unknown.join(', ')}"
      end

      context = resolve_context(presence(value['context']) || '.')
      dockerfile = presence(value['dockerfile'])
      { 'context' => context, 'dockerfile' => dockerfile && Pathname(context).join(dockerfile).to_s,
        'args' => normalize_kv(name, value['args'], 'build.args') }.compact
    end

    # build.context is relative to compose.yml's own directory (Compose's own rule),
    # not wherever `wip` happens to be invoked from.
    def resolve_context(raw) = Pathname(@path).expand_path.dirname.join(raw).to_s

    def normalize_env(name, value) = normalize_kv(name, value, 'environment')

    # Shared by `environment:` and `build.args:` — both accept a mapping or a
    # KEY=VALUE array, and neither supports host environment pass-through (a
    # null mapping value, or a bare KEY with no `=`).
    def normalize_kv(name, value, label)
      return {} unless value

      case value
      when Hash then normalize_kv_mapping(name, value, label)
      when Array then normalize_kv_array(name, value, label)
      else raise ConfigError, "#{@path}: services.#{name}.#{label} must be a mapping or an array of KEY=VALUE"
      end
    end

    def normalize_kv_mapping(name, value, label)
      value.to_h do |key, val|
        if val.nil?
          raise ConfigError, "#{@path}: services.#{name}.#{label}.#{key} must have a value " \
                             '(host environment pass-through is not supported)'
        end

        [key.to_s, val.to_s]
      end
    end

    def normalize_kv_array(name, value, label)
      value.to_h do |item|
        key, val = item.to_s.split('=', 2)
        raise ConfigError, "#{@path}: services.#{name}.#{label} entries must be KEY=VALUE" unless val

        [key, val]
      end
    end

    def normalize_list(name, value, key)
      return [] unless value
      raise ConfigError, "#{@path}: services.#{name}.#{key} must be an array" unless value.is_a?(Array)
      if value.any?(Hash)
        raise ConfigError, "#{@path}: services.#{name}.#{key} only supports short syntax (\"host:container\"), " \
                           'not long-syntax mappings'
      end

      value.map(&:to_s)
    end

    def normalize_depends_on(name, value)
      return [] unless value

      case value
      when Array then value.map(&:to_s)
      when Hash then value.map { |dep, opts| depends_on_entry(name, dep, opts) }
      else raise ConfigError, "#{@path}: services.#{name}.depends_on must be an array or a mapping"
      end
    end

    def depends_on_entry(name, dep, opts)
      condition = opts.is_a?(Hash) ? opts['condition'] : nil
      if condition && !SUPPORTED_CONDITIONS.include?(condition)
        raise ConfigError, "#{@path}: services.#{name}.depends_on.#{dep}: condition '#{condition}' is not " \
                           "supported (only #{SUPPORTED_CONDITIONS.join(', ')} — no health checks)"
      end
      dep.to_s
    end

    def validate_depends_on!
      @services.each do |name, service|
        service.depends_on.each do |dep|
          raise ConfigError, "#{@path}: services.#{name} depends_on unknown service '#{dep}'" unless @services.key?(dep)
        end
      end
    end

    def topological_order
      visited = {}
      visiting = {}
      order = []
      @services.each_key { |name| visit(name, visited, visiting, order) }
      order
    end

    def visit(name, visited, visiting, order)
      return if visited[name]
      raise ConfigError, "#{@path}: services.#{name} is part of a depends_on cycle" if visiting[name]

      visiting[name] = true
      @services.fetch(name).depends_on.each { |dep| visit(dep, visited, visiting, order) }
      visiting.delete(name)
      visited[name] = true
      order << name
    end

    def presence(value) = value.to_s.empty? ? nil : value.to_s
  end
end
