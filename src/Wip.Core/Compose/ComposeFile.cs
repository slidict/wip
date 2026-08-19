using Wip.Platform;
using Wip.Yaml;

namespace Wip.Compose;

/// <summary>
/// Parses compose.yml into the shape <c>mode: compose-native</c> needs to drive wslc
/// directly, in place of an external compose-for-wslc binary.
/// </summary>
/// <remarks>
/// This exists only because wslc has no native Compose support yet (tracked upstream in
/// microsoft/WSL#40948). Delete this file — and its hooks in Config, the CLI, Doctor, and
/// Initializer — once wslc ships that support, or a compose-for-wslc tool reliably supports
/// <c>run</c>.
/// </remarks>
public sealed class ComposeFile
{
    private static readonly string[] ServiceKeys =
    [
        "image", "build", "command", "environment", "ports", "volumes", "working_dir", "user", "restart",
        "depends_on", "profiles",
    ];

    /// <summary>
    /// Real Compose keys that read as meaningful here but have nothing to map onto: TTY and
    /// stdin allocation is decided per invocation, not per service; every service already
    /// shares one project network; and <c>wslc run</c>/<c>exec</c> has no capability flag to
    /// forward <c>cap_add:</c> to.
    /// </summary>
    private static readonly string[] IgnoredServiceKeys = ["tty", "stdin_open", "networks", "cap_add"];

    // shadow_context is deliberately absent: the build context is now always staged to a
    // local cache, so the key has no effect and falls through to the unsupported-key error.
    private static readonly string[] BuildKeys = ["context", "dockerfile", "args"];
    private static readonly string[] SupportedConditions = ["service_started"];
    private const string ListHint = "only supports short syntax (\"host:container\"), not long-syntax mappings";

    private readonly string path;
    private readonly OrderedDictionary<string, Service> services;
    private readonly List<string> order;

    private ComposeFile(OrderedDictionary<string, object?> rawServices, string path)
    {
        this.path = path;
        services = new OrderedDictionary<string, Service>(StringComparer.Ordinal);
        foreach (var (name, entry) in rawServices)
        {
            services[name] = BuildService(name, entry);
        }

        ValidateDependsOn();
        order = TopologicalOrder();
    }

    /// <summary>
    /// <paramref name="environment"/> interpolates compose.yml's <c>${VAR}</c> references the
    /// way <c>docker compose</c> does, so values like <c>user: ${USER_ID}:${GROUP_ID}</c>
    /// reach wslc already substituted instead of literally.
    /// </summary>
    public static ComposeFile Load(string path, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Compose file not found: {path}");
        }

        var raw = Interpolate(YamlLoader.LoadFile(path, allowAliases: true), environment ?? new Dictionary<string, string>());
        var document = RubyValue.AsMapping(raw);
        var rawServices = document is null ? null : RubyValue.AsMapping(document.GetValueOrDefault("services"));
        if (rawServices is null)
        {
            throw new ConfigException($"{path}: services: must be a mapping");
        }

        return new ComposeFile(rawServices, path);
    }

    public IReadOnlyList<string> ServiceNamesInDependencyOrder => [.. order];

    /// <summary>
    /// True if <paramref name="name"/> is gated behind <c>profiles:</c> — wip has no
    /// <c>--profile</c> flag. False for an unknown name.
    /// </summary>
    public bool IsProfiled(string name) =>
        services.TryGetValue(name, out var service) && service.Profiles.Count > 0;

    /// <summary>
    /// name =&gt; {context, dockerfile, tag} for every service with a <c>build:</c>.
    /// Profile-gated services are skipped.
    /// </summary>
    public OrderedDictionary<string, object?> BuildSpecs()
    {
        var specs = RubyValue.NewMapping();
        foreach (var name in StartableOrder())
        {
            var service = services[name];
            if (service.Build is null)
            {
                continue;
            }

            var spec = RubyValue.Merge(service.Build, null);
            spec["tag"] = ImageTag(name, service);
            specs[name] = spec;
        }

        return specs;
    }

    /// <summary>
    /// Shaped the way <see cref="Configuration.Config"/>'s dependency defaults expect, in
    /// dependency order so callers iterating sidecars start them before their dependents.
    /// </summary>
    public OrderedDictionary<string, object?> ToDependenciesMapping()
    {
        var result = RubyValue.NewMapping();
        foreach (var name in StartableOrder())
        {
            var service = services[name];
            var entry = RubyValue.NewMapping();
            entry["image"] = service.Build is not null ? ImageTag(name, service) : service.Image;
            entry["command"] = service.Command;
            entry["env"] = service.Environment;
            entry["ports"] = service.Ports.Cast<object?>().ToList();
            entry["volumes"] = service.Volumes.Cast<object?>().ToList();
            entry["workdir"] = service.Workdir;
            entry["user"] = service.User;
            entry["restart"] = service.Restart;
            result[name] = entry;
        }

        return result;
    }

    /// <summary>
    /// A profile-gated service is never among the ones wip starts on its own, but still
    /// participates in ordering and depends_on validation.
    /// </summary>
    private IEnumerable<string> StartableOrder() => order.Where(name => !IsProfiled(name));

    /// <summary>
    /// A service naming both <c>build:</c> and <c>image:</c> builds via the former and tags
    /// the result with the latter — real Compose's own rule for that combination — instead of
    /// the auto-generated tag a build-only service gets.
    /// </summary>
    private static string ImageTag(string name, Service service) => service.Image ?? $"wip-compose-{name}:latest";

    private Service BuildService(string name, object? entry)
    {
        var mapping = RubyValue.AsMapping(entry)
                      ?? throw new ConfigException($"{path}: services.{name} must be a mapping");

        var unknown = mapping.Keys.Where(key => !ServiceKeys.Contains(key) && !IgnoredServiceKeys.Contains(key))
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ConfigException($"{path}: services.{name} has unsupported key(s): {string.Join(", ", unknown)}");
        }

        var image = RubyValue.Presence(mapping.GetValueOrDefault("image"));
        var build = NormalizeBuild(name, mapping.GetValueOrDefault("build"));
        if (image is null && build is null)
        {
            throw new ConfigException($"{path}: services.{name} must set image or build");
        }

        return new Service(
            image,
            build,
            NormalizeCommand(mapping.GetValueOrDefault("command")),
            NormalizeKeyValues(name, mapping.GetValueOrDefault("environment"), "environment"),
            NormalizeList(name, mapping.GetValueOrDefault("ports"), "ports", ListHint),
            NormalizeList(name, mapping.GetValueOrDefault("volumes"), "volumes", ListHint),
            NormalizeList(name, mapping.GetValueOrDefault("profiles"), "profiles", "must be an array of strings"),
            RubyValue.Presence(mapping.GetValueOrDefault("working_dir")),
            RubyValue.Presence(mapping.GetValueOrDefault("user")),
            NormalizeRestart(mapping.GetValueOrDefault("restart")),
            NormalizeDependsOn(name, mapping.GetValueOrDefault("depends_on")));
    }

    /// <summary>
    /// Compose allows both shell form ("bin/rails s") and exec form (["bin/rails", "s"]).
    /// <see cref="Execution.CommandBuilder"/> re-splits whatever is here, so exec form is
    /// joined back into one string rather than stringified as a list.
    /// </summary>
    private static string? NormalizeCommand(object? value)
    {
        var list = RubyValue.AsSequence(value);
        return list is null
            ? RubyValue.Presence(value)
            : Shellwords.Join(list.Select(RubyValue.ToStringValue)).Presence();
    }

    private OrderedDictionary<string, object?>? NormalizeBuild(string name, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            var shorthand = RubyValue.NewMapping();
            shorthand["context"] = ResolveContext(text);
            return shorthand;
        }

        var mapping = RubyValue.AsMapping(value)
                      ?? throw new ConfigException($"{path}: services.{name}.build must be a string or mapping");

        var unknown = mapping.Keys.Where(key => !BuildKeys.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new ConfigException(
                $"{path}: services.{name}.build has unsupported key(s): {string.Join(", ", unknown)}");
        }

        var build = RubyValue.NewMapping();
        build["context"] = ResolveContext(RubyValue.Presence(mapping.GetValueOrDefault("context")) ?? ".");

        // Kept relative to context (not resolved against it) so `-f` still finds it once
        // `wip up` / `wip build` chdir into a staged copy of that context.
        var dockerfile = RubyValue.Presence(mapping.GetValueOrDefault("dockerfile"));
        if (dockerfile is not null)
        {
            build["dockerfile"] = dockerfile;
        }

        var args = NormalizeKeyValues(name, mapping.GetValueOrDefault("args"), "build.args");
        if (args.Count > 0 || mapping.ContainsKey("args"))
        {
            build["args"] = args;
        }

        return build;
    }

    /// <summary>
    /// <c>build.context</c> is relative to compose.yml's own directory — Compose's own rule —
    /// not wherever wip happens to be invoked from.
    /// </summary>
    /// <remarks>
    /// This stays a host path in the host's own spelling, UNC included: wslc never sees it.
    /// <c>BuildContext</c> reads the tree itself and stages it into a Windows-local cache,
    /// and the build then runs from there with <c>context: "."</c> (§3.3 of the migration
    /// plan). Putting it through <c>WslPath.ForWslc</c> would refuse — or, before that,
    /// silently mistranslate — a WSL-side context that staging handles perfectly well.
    /// </remarks>
    private string ResolveContext(string raw)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return Path.GetFullPath(Path.Combine(baseDirectory, raw));
    }

    /// <summary>
    /// Shared by <c>environment:</c> and <c>build.args:</c> — both accept a mapping or a
    /// KEY=VALUE array, and neither supports host environment pass-through (a null mapping
    /// value, or a bare KEY with no '=').
    /// </summary>
    private OrderedDictionary<string, object?> NormalizeKeyValues(string name, object? value, string label)
    {
        var result = RubyValue.NewMapping();
        if (value is null)
        {
            return result;
        }

        if (RubyValue.AsMapping(value) is { } mapping)
        {
            foreach (var (key, entry) in mapping)
            {
                if (entry is null)
                {
                    throw new ConfigException(
                        $"{path}: services.{name}.{label}.{key} must have a value " +
                        "(host environment pass-through is not supported)");
                }

                result[key] = RubyValue.ToStringValue(entry);
            }

            return result;
        }

        if (RubyValue.AsSequence(value) is not { } sequence)
        {
            throw new ConfigException(
                $"{path}: services.{name}.{label} must be a mapping or an array of KEY=VALUE");
        }

        foreach (var item in sequence)
        {
            var text = RubyValue.ToStringValue(item);
            var separator = text.IndexOf('=');
            if (separator < 0)
            {
                throw new ConfigException($"{path}: services.{name}.{label} entries must be KEY=VALUE");
            }

            result[text[..separator]] = text[(separator + 1)..];
        }

        return result;
    }

    private List<string> NormalizeList(string name, object? value, string key, string hint)
    {
        if (value is null)
        {
            return [];
        }

        if (RubyValue.AsSequence(value) is not { } sequence)
        {
            throw new ConfigException($"{path}: services.{name}.{key} must be an array");
        }

        if (sequence.Any(item => item is OrderedDictionary<string, object?> or List<object?>))
        {
            throw new ConfigException($"{path}: services.{name}.{key} {hint}");
        }

        return sequence.Select(RubyValue.ToStringValue).ToList();
    }

    private List<string> NormalizeDependsOn(string name, object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (RubyValue.AsSequence(value) is { } sequence)
        {
            return sequence.Select(RubyValue.ToStringValue).ToList();
        }

        if (RubyValue.AsMapping(value) is not { } mapping)
        {
            throw new ConfigException($"{path}: services.{name}.depends_on must be an array or a mapping");
        }

        foreach (var (dependency, options) in mapping)
        {
            var condition = RubyValue.Presence(RubyValue.Dig(options, "condition"));
            if (condition is not null && !SupportedConditions.Contains(condition))
            {
                throw new ConfigException(
                    $"{path}: services.{name}.depends_on.{dependency}: condition '{condition}' is not " +
                    $"supported (only {string.Join(", ", SupportedConditions)} — no health checks)");
            }
        }

        return mapping.Keys.ToList();
    }

    /// <summary>
    /// Also rejects a startable (unprofiled) service depending on a profile-gated one: with
    /// no profile activation, real Compose treats that as an invalid model, since the
    /// dependency would silently never start.
    /// </summary>
    private void ValidateDependsOn()
    {
        foreach (var (name, service) in services)
        {
            foreach (var dependency in service.DependsOn)
            {
                if (!services.ContainsKey(dependency))
                {
                    throw new ConfigException($"{path}: services.{name} depends_on unknown service '{dependency}'");
                }

                if (service.Profiles.Count > 0 || !IsProfiled(dependency))
                {
                    continue;
                }

                throw new ConfigException(
                    $"{path}: services.{name} depends_on '{dependency}', gated behind profiles: " +
                    $"({string.Join(", ", services[dependency].Profiles)}) wip never activates " +
                    "(no --profile flag)");
            }
        }
    }

    private List<string> TopologicalOrder()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var name in services.Keys)
        {
            Visit(name, visited, visiting, result);
        }

        return result;
    }

    private void Visit(string name, HashSet<string> visited, HashSet<string> visiting, List<string> result)
    {
        if (visited.Contains(name))
        {
            return;
        }

        if (!visiting.Add(name))
        {
            throw new ConfigException($"{path}: services.{name} is part of a depends_on cycle");
        }

        foreach (var dependency in services[name].DependsOn)
        {
            Visit(dependency, visited, visiting, result);
        }

        visiting.Remove(name);
        visited.Add(name);
        result.Add(name);
    }

    /// <summary>
    /// Compose's own default (<c>no</c>) applies whether <c>restart:</c> is absent or
    /// explicitly falsy — including the very common unquoted <c>restart: no</c>, which YAML
    /// resolves to the boolean false. Every other value is accepted as written, even ones
    /// outside always/unless-stopped/on-failure[:N]: this parser's job is to read what is in
    /// compose.yml, not police it. Rejecting a real, valid Compose value here would break
    /// projects that already work today, since compose.yml predates wip.
    /// </summary>
    private static string NormalizeRestart(object? value) =>
        value is false ? "no" : RubyValue.Presence(value) ?? "no";

    /// <summary>
    /// Walks an already-parsed YAML structure and interpolates string values only: real
    /// Compose interpolates values, never mapping keys, and doing this after parsing means a
    /// substituted value cannot introduce YAML syntax (a literal '#' turning into a comment
    /// marker, say).
    /// </summary>
    private static object? Interpolate(object? value, IReadOnlyDictionary<string, string> environment) => value switch
    {
        string text => VariableInterpolation.Call(text, environment),
        List<object?> list => list.Select(item => Interpolate(item, environment)).ToList(),
        OrderedDictionary<string, object?> mapping => InterpolateMapping(mapping, environment),
        _ => value,
    };

    private static OrderedDictionary<string, object?> InterpolateMapping(
        OrderedDictionary<string, object?> mapping,
        IReadOnlyDictionary<string, string> environment)
    {
        var result = RubyValue.NewMapping();
        foreach (var (key, value) in mapping)
        {
            result[key] = Interpolate(value, environment);
        }

        return result;
    }

    private sealed record Service(
        string? Image,
        OrderedDictionary<string, object?>? Build,
        string? Command,
        OrderedDictionary<string, object?> Environment,
        List<string> Ports,
        List<string> Volumes,
        List<string> Profiles,
        string? Workdir,
        string? User,
        string Restart,
        List<string> DependsOn);
}
