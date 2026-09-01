using Wip.Configuration;
using Wip.Platform;
using Wip.Yaml;

namespace Wip.Execution;

/// <summary>
/// Builds the argument arrays for wslc build/exec/run/custom invocations.
/// </summary>
/// <remarks>
/// Everything here returns an argv array that is handed to the process API as a list, never
/// as a command string, so nothing in wip.yml is ever exposed to shell interpretation.
/// </remarks>
public sealed class CommandBuilder
{
    private readonly string wslc;
    private readonly Config config;
    private readonly IEnvironment environment;
    private readonly OrderedDictionary<string, string> dotenv;

    public CommandBuilder(
        string wslc,
        Config config,
        IEnvironment environment,
        OrderedDictionary<string, string>? dotenv = null)
    {
        this.wslc = wslc;
        this.config = config;
        this.environment = environment;
        this.dotenv = dotenv ?? new OrderedDictionary<string, string>(StringComparer.Ordinal);
    }

    public bool Tty(bool requested) => requested && environment.IsInteractive;

    public IReadOnlyList<string> Exec(
        IEnumerable<string> arguments,
        OrderedDictionary<string, object?>? settings = null,
        bool interactive = true)
    {
        // dependencies: entries don't carry their own name (it's the mapping key), so the
        // exec target defaults to config.Container; a commands: entry can still redirect it
        // by setting its own container:.
        var values = PrimaryValues();
        values["container"] = RequiredContainer();
        values = RubyValue.Merge(values, settings);

        var command = new List<string> { wslc, "exec" };
        if (Tty(interactive))
        {
            command.Add("-it");
        }

        command.AddRange(Options(values, includeContainer: true, includePublish: false));
        command.AddRange(arguments);
        return command;
    }

    public IReadOnlyList<string> Run(
        IEnumerable<string> arguments,
        OrderedDictionary<string, object?>? settings = null,
        bool interactive = true)
    {
        var values = RubyValue.Merge(PrimaryValues(), settings);
        var command = new List<string> { wslc, "run" };
        if (RubyValue.IsTruthy(values.GetValueOrDefault("remove")))
        {
            command.Add("--rm");
        }

        if (Tty(interactive))
        {
            command.Add("-it");
        }

        command.AddRange(Options(values));
        command.Add(Required(values, "image"));
        command.AddRange(arguments);
        return command;
    }

    public IReadOnlyList<string> Up(bool detach = false)
    {
        var values = PrimaryValues();
        var command = new List<string> { wslc, "run", "--name", RequiredContainer() };
        if (config.Network is { } network)
        {
            command.Add("--network");
            command.Add(network);
        }

        if (detach)
        {
            command.Add("-d");
        }

        if (!detach && Tty(true))
        {
            command.Add("-it");
        }

        command.AddRange(Options(values));
        command.Add(Required(values, "image"));
        command.AddRange(Shellwords.Split(RubyValue.ToStringValue(values.GetValueOrDefault("command"))));
        return command;
    }

    public IReadOnlyList<string> Start(bool detach = false)
    {
        var command = new List<string> { wslc, "start", RequiredContainer() };
        if (!detach)
        {
            command.Add("-a");
            command.Add("-i");
        }

        return command;
    }

    public IReadOnlyList<string> Find() => Listing(RequiredContainer());

    public IReadOnlyList<string> Stop() => [wslc, "stop", RequiredContainer()];

    public IReadOnlyList<string> Remove() => [wslc, "remove", "-f", RequiredContainer()];

    public IReadOnlyList<string> NetworkCreate() => [wslc, "network", "create", RequiredNetwork()];

    public IReadOnlyList<string> NetworkList() => [wslc, "network", "list", "--format", "json"];

    /// <summary>
    /// Tears down the whole WSLC session, not anything scoped to this <c>wip.yml</c> —
    /// <see cref="Wip.Diagnostics.ErrorInterpreter"/> already points a user at this same
    /// command by hand, as the recovery step for a session-wide mounted-volume limit. Every
    /// other command in this file resolves a specific container or network first; this one
    /// takes no argument at all, because there is nothing project-scoped about it.
    /// </summary>
    public IReadOnlyList<string> SystemSessionTerminate() => [wslc, "system", "session", "terminate"];

    /// <summary>
    /// Mirrors into the volume from a throwaway container, for <c>sync.mode: run</c>. The
    /// image comes from <c>sync.build</c>'s tag, else <c>sync.image</c>, else the primary
    /// dependencies entry; compose mode requires one of the first two, since it has no
    /// dependencies entry to fall back to.
    /// </summary>
    public IReadOnlyList<string> SyncRun()
    {
        var sync = RequiredSync();
        var command = new List<string> { wslc, "run", "--rm" };
        foreach (var spec in sync.VolumeSpecs)
        {
            command.Add("-v");
            command.Add(spec);
        }

        var image = RubyValue.Presence(sync.Build?.GetValueOrDefault("tag"))
                    ?? sync.Image
                    ?? Required(PrimaryValues(), "image");

        command.Add(image);
        command.AddRange(sync.MirrorCommand());
        return command;
    }

    /// <summary>
    /// Builds <c>sync.build</c>'s image from a Dockerfile staged in <paramref name="context"/>,
    /// a caller-managed directory. Doesn't touch dependencies at all, so it works the same
    /// under compose mode as under container mode.
    /// </summary>
    public IReadOnlyList<string> SyncBuild(string context)
    {
        var sync = RequiredSync();
        if (sync.Build is null)
        {
            throw new ConfigException("No sync.build configured in wip.yml");
        }

        return [wslc, "build", "-t", RubyValue.ToStringValue(sync.Build["tag"]), context];
    }

    /// <summary>
    /// Mirrors from inside the already-running container. Only valid for
    /// <c>sync.mode: exec</c>, since only a container wip itself booted is guaranteed to have
    /// both the read-only source mount and the volume attached.
    /// </summary>
    public IReadOnlyList<string> SyncExec()
    {
        var command = new List<string> { wslc, "exec", RequiredContainer() };
        command.AddRange(RequiredSync().MirrorCommand());
        return command;
    }

    /// <summary>
    /// Single container only, mirroring wslc's own <c>logs</c>: there is no multi-service log
    /// aggregation the way a real compose tool provides.
    /// </summary>
    public IReadOnlyList<string> Logs(string name, bool follow = true)
    {
        var command = new List<string> { wslc, "logs" };
        if (follow)
        {
            command.Add("-f");
        }

        command.Add(name);
        return command;
    }

    public IReadOnlyList<string> DependencyUp(string name, bool detach = true)
    {
        var values = DependencyValues(name);
        var command = new List<string> { wslc, "run", "--name", name };
        if (config.Network is { } network)
        {
            command.Add("--network");
            command.Add(network);
        }

        if (detach)
        {
            command.Add("-d");
        }

        command.AddRange(Options(values, sync: false));
        command.Add(Required(values, "image"));
        command.AddRange(Shellwords.Split(RubyValue.ToStringValue(values.GetValueOrDefault("command"))));
        return command;
    }

    public IReadOnlyList<string> DependencyStart(string name) => [wslc, "start", name];

    /// <summary>
    /// Runs a healthcheck's <c>test</c> inside a sidecar. Unlike <see cref="Exec"/>, this
    /// targets <paramref name="name"/> directly rather than <c>config.Container</c>, since a
    /// readiness check runs against whichever dependency it belongs to, not the primary.
    /// </summary>
    public IReadOnlyList<string> DependencyExec(string name, IEnumerable<string> arguments)
    {
        var command = new List<string> { wslc, "exec", name };
        command.AddRange(arguments);
        return command;
    }

    public IReadOnlyList<string> DependencyFind(string name) => Listing(name);

    public IReadOnlyList<string> DependencyStop(string name) => [wslc, "stop", name];

    public IReadOnlyList<string> DependencyRemove(string name) => [wslc, "remove", "-f", name];

    /// <summary>
    /// <c>wslc build</c> reuses matching local layers by default (like <c>docker build</c>
    /// without <c>--pull</c>) — it has no <c>--cache-from</c> flag to ask for that
    /// explicitly, and passing one is a hard error.
    /// </summary>
    public IReadOnlyList<string> Build(OrderedDictionary<string, object?>? settings = null, IEnumerable<string>? extra = null)
    {
        var values = RubyValue.Merge(PrimaryValues(), settings);
        var context = RubyValue.Presence(values.GetValueOrDefault("context")) ?? ".";
        var tag = RubyValue.Presence(values.GetValueOrDefault("tag"))
                  ?? RubyValue.Presence(values.GetValueOrDefault("image"));

        if (tag is null)
        {
            throw new ConfigException("Build image/tag must not be empty");
        }

        var command = new List<string> { wslc, "build", "-t", tag };
        command.AddRange(extra ?? []);
        command.Add(context);
        return command;
    }

    public IReadOnlyList<string> Custom(string name, IEnumerable<string> arguments)
    {
        var values = config.Command(name) ?? throw new ConfigException($"Unknown command: {name}");
        var type = RubyValue.Presence(values.GetValueOrDefault("type")) ?? (name == "build" ? "build" : "exec");
        var argumentList = arguments.ToList();

        if (type == "build")
        {
            return Build(values, argumentList);
        }

        var command = Shellwords.Split(RubyValue.ToStringValue(values.GetValueOrDefault("command"))).ToList();
        command.AddRange(argumentList);

        var interactive = RubyValue.IsTruthy(values.GetValueOrDefault("interactive"));
        return type == "run"
            ? Run(command, values, interactive)
            : Exec(command, values, interactive);
    }

    private IReadOnlyList<string> Listing(string name) =>
        [wslc, "list", "--all", "--filter", $"name={name}", "--format", "json"];

    private List<string> Options(
        OrderedDictionary<string, object?> values,
        bool includeContainer = false,
        bool includePublish = true,
        bool sync = true)
    {
        var result = new List<string>();

        if (RubyValue.Presence(values.GetValueOrDefault("workdir")) is { } workdir)
        {
            result.Add("-w");
            result.Add(workdir);
        }

        if (RubyValue.Presence(values.GetValueOrDefault("user")) is { } user)
        {
            result.Add("-u");
            result.Add(user);
        }

        foreach (var (key, value) in MergedEnvironment(values))
        {
            result.Add("-e");
            result.Add($"{key}={value}");
        }

        if (includePublish)
        {
            foreach (var port in RubyValue.AsArray(values.GetValueOrDefault("ports")))
            {
                result.Add("-p");
                result.Add(RubyValue.ToStringValue(port));
            }

            foreach (var volume in VolumeSpecs(values, sync))
            {
                result.Add("-v");
                result.Add(volume);
            }
        }

        if (includeContainer)
        {
            result.Add(Required(values, "container"));
        }

        return result;
    }

    /// <summary>
    /// With sync configured, a live bind mount of the target (<c>.:/app</c>) is swapped for
    /// the read-only source mount plus the named volume, so the running app only ever touches
    /// the volume.
    /// </summary>
    private List<string> VolumeSpecs(OrderedDictionary<string, object?> values, bool sync)
    {
        var specs = RubyValue.AsArray(values.GetValueOrDefault("volumes")).Select(RubyValue.ToStringValue).ToList();
        var settings = sync ? config.Sync : null;
        if (settings is null)
        {
            return specs;
        }

        var kept = specs.Where(spec => !settings.Replaces(spec)).ToList();
        kept.AddRange(settings.VolumeSpecs);
        return kept;
    }

    /// <summary>.env supplies defaults; env set in wip.yml wins on conflict.</summary>
    private OrderedDictionary<string, string> MergedEnvironment(OrderedDictionary<string, object?> values)
    {
        var merged = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in dotenv)
        {
            merged[key] = value;
        }

        if (RubyValue.AsMapping(values.GetValueOrDefault("env")) is { } environment)
        {
            foreach (var (key, value) in environment)
            {
                merged[key] = RubyValue.ToStringValue(value);
            }
        }

        return merged;
    }

    private static string Required(OrderedDictionary<string, object?> values, string key) =>
        RubyValue.Presence(values.GetValueOrDefault(key))
        ?? throw new ConfigException($"Configured {key} must not be empty");

    private string RequiredNetwork() =>
        config.Network ?? throw new ConfigException("Configured network must not be empty");

    private SyncSettings RequiredSync() =>
        config.Sync ?? throw new ConfigException("No sync: block configured in wip.yml");

    private OrderedDictionary<string, object?> DependencyValues(string name) =>
        config.Dependency(name) ?? throw new ConfigException($"Unknown dependency: {name}");

    private string RequiredContainer() =>
        config.Container ?? throw new ConfigException("container: must be set in wip.yml");

    private OrderedDictionary<string, object?> PrimaryValues()
    {
        var container = RequiredContainer();
        return config.Dependency(container)
               ?? throw new ConfigException(
                   $"No dependencies.{container} entry (check container: in wip.yml)");
    }
}
