using System.Text.RegularExpressions;
using Wip.Compose;
using Wip.Yaml;

namespace Wip.Configuration;

/// <summary>Validated, defaulted access to a parsed wip.yml document.</summary>
public sealed partial class Config
{
    /// <summary>
    /// Applied to every <c>dependencies:</c> entry, primary container included — there is no
    /// separate, differently shaped bucket for "the one you exec into".
    /// </summary>
    private static readonly (string Key, object? Value)[] DependencyDefaults =
    [
        ("workdir", null), ("user", null), ("interactive", false), ("remove", true),
        ("env", null), ("ports", null), ("volumes", null), ("restart", "no"),
    ];

    /// <summary>
    /// Which orchestration path up/down/sync take. Explicit rather than inferred from a
    /// <c>compose:</c> block's presence, so a config reader doesn't have to know that rule to
    /// predict which mode wip runs in. <c>compose</c> bridges to an external
    /// compose-for-wslc binary; <c>compose-native</c> parses compose.yml itself and drives
    /// wslc directly — a stopgap for as long as those external tools stay incomplete.
    /// </summary>
    public static readonly string[] Modes = ["container", "compose", "compose-native"];

    private readonly OrderedDictionary<string, object?> raw;
    private readonly string? envFile;
    private SyncSettings? sync;
    private bool syncResolved;
    private ComposeFile? parsedComposeFile;

    public Config(object? document, string? path = null, string? envFile = null)
    {
        raw = RubyValue.AsMapping(document) ?? RubyValue.NewMapping();
        Path = path;
        this.envFile = envFile;
        Validate();
    }

    public string? Path { get; }

    public string WslcCommand => RubyValue.Presence(RubyValue.Dig(raw, "wslc", "command")) ?? "auto";

    /// <summary>
    /// <c>interaction:</c> is dip's name for the same concept, accepted as an alias so a
    /// dip.yml can be renamed to wip.yml with fewer edits. The two are mutually exclusive
    /// rather than merged, so a project doesn't end up with the same command split across
    /// both keys.
    /// </summary>
    public OrderedDictionary<string, object?> Commands
    {
        get
        {
            if (raw.TryGetValue("commands", out var commands))
            {
                return RubyValue.AsMapping(commands) ?? RubyValue.NewMapping();
            }

            return raw.TryGetValue("interaction", out var interaction)
                ? RubyValue.AsMapping(interaction) ?? RubyValue.NewMapping()
                : RubyValue.NewMapping();
        }
    }

    private object? RawCommandsValue =>
        raw.TryGetValue("commands", out var commands) ? commands : raw.GetValueOrDefault("interaction");

    private bool HasCommandsKey => raw.ContainsKey("commands") || raw.ContainsKey("interaction");

    /// <summary>
    /// Raw <c>dependencies:</c> block as written in wip.yml. Under compose-native this stays
    /// empty by construction; <see cref="Dependencies"/> is what callers actually want, since
    /// it is synthesized from compose.yml there.
    /// </summary>
    public OrderedDictionary<string, object?> RawDependencies =>
        RubyValue.AsMapping(raw.GetValueOrDefault("dependencies")) ?? RubyValue.NewMapping();

    public OrderedDictionary<string, object?> Dependencies =>
        IsComposeNative ? ParsedComposeFile.ToDependenciesMapping() : RawDependencies;

    /// <summary>
    /// Which <c>dependencies:</c> entry the built-in commands target by default — the one
    /// container wip itself considers "the app". Everything else is a sidecar wip only starts
    /// and stops. No default: guessing a name either matches by luck or fails in a way that
    /// doesn't point at the real problem, so a project with any dependencies must say which
    /// one explicitly. Under compose-native this is <c>compose.service</c>, since compose.yml
    /// already names the service.
    /// </summary>
    public string? Container =>
        IsComposeNative ? ComposeService : RubyValue.Presence(raw.GetValueOrDefault("container"));

    /// <summary>
    /// Under compose-native wip creates its own project network (<c>compose.project</c>, or
    /// the wip.yml directory's name) so services can reach each other by name — the same
    /// guarantee real Compose's per-project network gives.
    /// </summary>
    public string? Network =>
        IsComposeNative
            ? ComposeProject ?? DefaultComposeNetwork
            : RubyValue.Presence(raw.GetValueOrDefault("network"));

    public string Mode => RubyValue.Presence(raw.GetValueOrDefault("mode")) ?? "container";

    public OrderedDictionary<string, object?>? ComposeBlock => RubyValue.AsMapping(raw.GetValueOrDefault("compose"));

    public bool IsCompose => Mode == "compose";

    public bool IsComposeNative => Mode == "compose-native";

    public bool IsComposeMode => IsCompose || IsComposeNative;

    public string? ComposeService => RubyValue.Presence(RubyValue.Dig(raw, "compose", "service"));

    public string? ComposeFilePath => RubyValue.Presence(RubyValue.Dig(raw, "compose", "file"));

    public string? ComposeProject => RubyValue.Presence(RubyValue.Dig(raw, "compose", "project"));

    public string? ComposeCommand => RubyValue.Presence(RubyValue.Dig(raw, "compose", "command"));

    /// <summary>
    /// name =&gt; {context, dockerfile, tag} for compose-native services with a
    /// <c>build:</c> instead of an <c>image:</c>; empty otherwise. Consumed by <c>wip up</c>
    /// to build each one before starting it.
    /// </summary>
    public OrderedDictionary<string, object?> ComposeBuildSpecs =>
        IsComposeNative ? ParsedComposeFile.BuildSpecs() : RubyValue.NewMapping();

    public bool HasSync => Sync is not null;

    public SyncSettings? Sync
    {
        get
        {
            if (syncResolved)
            {
                return sync;
            }

            syncResolved = true;
            sync = raw.ContainsKey("sync") ? BuildSync() : null;
            return sync;
        }
    }

    /// <summary>The <c>dependencies:</c> entry <see cref="Container"/> points at, or null.</summary>
    public OrderedDictionary<string, object?>? Primary => Container is null ? null : Dependency(Container);

    public OrderedDictionary<string, object?>? Command(string name)
    {
        if (!Commands.TryGetValue(name, out var entry))
        {
            return null;
        }

        var merged = RubyValue.Merge(Primary ?? RubyValue.NewMapping(), null);
        merged["type"] = "exec";
        return RubyValue.Merge(merged, RubyValue.AsMapping(entry));
    }

    public OrderedDictionary<string, object?>? Dependency(string name)
    {
        if (!Dependencies.TryGetValue(name, out var entry))
        {
            return null;
        }

        var defaults = RubyValue.NewMapping();
        foreach (var (key, value) in DependencyDefaults)
        {
            defaults[key] = key switch
            {
                "env" => RubyValue.NewMapping(),
                "ports" or "volumes" => new List<object?>(),
                _ => value,
            };
        }

        return RubyValue.Merge(defaults, RubyValue.AsMapping(entry));
    }

    public OrderedDictionary<string, object?> ToMapping(bool redact = true)
    {
        var commands = RubyValue.NewMapping();
        foreach (var (name, entry) in Commands)
        {
            var merged = RubyValue.Merge(Primary ?? RubyValue.NewMapping(), null);
            merged["type"] = "exec";
            commands[name] = RubyValue.Merge(merged, RubyValue.AsMapping(entry));
        }

        var wslc = RubyValue.NewMapping();
        wslc["command"] = WslcCommand;

        var result = RubyValue.NewMapping();
        result["version"] = 1L;
        result["wslc"] = wslc;
        result["mode"] = Mode;
        result["container"] = Container;
        result["network"] = Network;
        result["dependencies"] = Dependencies;
        result["compose"] = ComposeBlock;
        result["sync"] = Sync?.ToMapping();
        result["commands"] = commands;

        return redact ? (OrderedDictionary<string, object?>)RedactSecrets(result)! : result;
    }

    private ComposeFile ParsedComposeFile
    {
        get
        {
            if (parsedComposeFile is not null)
            {
                return parsedComposeFile;
            }

            var file = ComposeFile.Load(
                ComposeBridge.FilePath(Path ?? ".", ComposeFilePath),
                ComposeInterpolationEnvironment());

            // compose.service is what wip always starts by default, and wip has no --profile
            // flag to activate one, so naming a profile-gated service here would otherwise
            // fail later with a misleading "No dependencies.<name> entry" instead of
            // pointing at the real cause.
            if (ComposeService is not null && file.IsProfiled(ComposeService))
            {
                throw new ConfigException(
                    $"compose.service '{ComposeService}' is gated behind profiles: in compose.yml, " +
                    "but wip has no --profile flag to activate one — pick a service with no profiles: " +
                    "or remove profiles: from it");
            }

            return parsedComposeFile = file;
        }
    }

    /// <summary>
    /// Compose interpolates <c>${VAR}</c> references from the shell environment and a project
    /// .env file, shell values winning on conflict. Mirrored here so wip doesn't need the
    /// value duplicated into wip.yml just to resolve a compose.yml reference, and using the
    /// same dotenv file wip would otherwise use, so interpolation and the env actually passed
    /// to containers never see two different .env files.
    /// </summary>
    private Dictionary<string, string> ComposeInterpolationEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = envFile ?? (Path is null
            ? null
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? ".", ".env"));

        if (file is not null)
        {
            foreach (var (key, value) in new DotenvLoader(file).Load())
            {
                result[key] = value;
            }
        }

        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            result[(string)entry.Key] = entry.Value as string ?? string.Empty;
        }

        return result;
    }

    private string? DefaultComposeNetwork =>
        Path is null ? null : new DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!).Name;

    private SyncSettings BuildSync() => new(
        raw.GetValueOrDefault("sync"),
        Path is null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)),
        RubyValue.Presence(Primary?.GetValueOrDefault("workdir")),
        Container,
        IsCompose);

    private void Validate()
    {
        var version = raw.GetValueOrDefault("version");
        if (version is not null && !version.Equals(1L))
        {
            throw new ConfigException($"Unsupported configuration version: {RubyValue.ToStringValue(version)}");
        }

        ValidateMode();
        ValidateCommands();
        ValidateDependencies();
        ValidateCompose();

        // Forces SyncSettings to build (and so run its own validation, including
        // sync.mode against mode:) at load time instead of on first access.
        if (raw.ContainsKey("sync"))
        {
            _ = Sync;
        }
    }

    private void ValidateMode()
    {
        if (!Modes.Contains(Mode))
        {
            throw new ConfigException($"mode must be one of {string.Join(", ", Modes)}");
        }

        if (IsComposeMode && !raw.ContainsKey("compose"))
        {
            throw new ConfigException($"mode: {Mode} requires a compose: block");
        }

        if (raw.ContainsKey("compose") && !IsComposeMode)
        {
            throw new ConfigException("a compose: block requires mode: compose or compose-native");
        }
    }

    private void ValidateCommands()
    {
        if (raw.ContainsKey("commands") && raw.ContainsKey("interaction"))
        {
            throw new ConfigException("commands is mutually exclusive with interaction — pick one");
        }

        if (HasCommandsKey && RubyValue.AsMapping(RawCommandsValue) is null && RawCommandsValue is not null)
        {
            throw new ConfigException("commands must be a mapping");
        }

        foreach (var (name, entry) in Commands)
        {
            ValidateCommand(name, entry);
        }
    }

    private void ValidateCommand(string name, object? entry)
    {
        var mapping = RubyValue.AsMapping(entry)
                      ?? throw new ConfigException($"commands.{name} must be a mapping");

        var type = RubyValue.Presence(mapping.GetValueOrDefault("type")) ?? (name == "build" ? "build" : "exec");
        if (type is not ("exec" or "run" or "build"))
        {
            throw new ConfigException($"Invalid command type for {name}: {type}");
        }

        ValidateShadowContext(name, mapping, type);
        StringifyEnvironment(mapping);
    }

    /// <summary>
    /// <c>shadow_context</c> used to name a Windows-side mirror of the build context, opted
    /// into per build command. The mirror is now unconditional — running on the Windows side,
    /// staging locally is what gives wslc a readable directory rather than a tuning choice —
    /// so the key does nothing. Rejecting it says so; silently ignoring it would leave a
    /// project believing its builds were still configured the way it wrote them.
    /// </summary>
    private static void ValidateShadowContext(string name, OrderedDictionary<string, object?> entry, string type)
    {
        _ = type;
        if (!entry.ContainsKey("shadow_context"))
        {
            return;
        }

        throw new ConfigException(
            $"commands.{name}.shadow_context is no longer supported — the build context is " +
            "always staged to a local cache now, so remove the key");
    }

    private void ValidateDependencies()
    {
        if (raw.TryGetValue("dependencies", out var dependencies) &&
            dependencies is not null &&
            RubyValue.AsMapping(dependencies) is null)
        {
            throw new ConfigException("dependencies must be a mapping");
        }

        // Under either compose mode, populated dependencies: is already an error on its own
        // (below); this only needs to fire for mode: container, where dependencies: having
        // entries but no container: is otherwise valid.
        if (RawDependencies.Count > 0 && Container is null && !IsComposeMode)
        {
            throw new ConfigException("container: must be set when dependencies: has entries");
        }

        foreach (var (name, entry) in RawDependencies)
        {
            ValidateDependency(name, entry);
        }
    }

    private void ValidateDependency(string name, object? entry)
    {
        var mapping = RubyValue.AsMapping(entry)
                      ?? throw new ConfigException($"dependencies.{name} must be a mapping");

        if (RubyValue.IsEmptyString(mapping.GetValueOrDefault("image")))
        {
            throw new ConfigException($"dependencies.{name} must set image");
        }

        StringifyEnvironment(mapping);
        NormalizeRestart(mapping);
    }

    /// <summary>
    /// Mirrors ComposeFile's restart handling: an unquoted <c>restart: no</c> parses as the
    /// boolean false rather than the string "no", and an explicit null or empty value should
    /// default the same way an absent key does — the defaults only fill in a genuinely
    /// missing key.
    /// </summary>
    private static void NormalizeRestart(OrderedDictionary<string, object?> entry)
    {
        if (!entry.TryGetValue("restart", out var restart))
        {
            return;
        }

        if (restart is false || RubyValue.IsEmptyString(restart))
        {
            entry["restart"] = "no";
        }
    }

    private static void StringifyEnvironment(OrderedDictionary<string, object?> entry)
    {
        if (RubyValue.AsMapping(entry.GetValueOrDefault("env")) is not { } environment)
        {
            return;
        }

        foreach (var key in environment.Keys.ToList())
        {
            environment[key] = RubyValue.ToStringValue(environment[key]);
        }
    }

    private void ValidateCompose()
    {
        if (!raw.ContainsKey("compose"))
        {
            return;
        }

        if (ComposeBlock is null)
        {
            throw new ConfigException("compose must be a mapping");
        }

        if (ComposeService is null)
        {
            throw new ConfigException("compose.service must not be empty");
        }

        if (RawDependencies.Count > 0)
        {
            throw new ConfigException("compose is mutually exclusive with dependencies");
        }

        if (RubyValue.IsTruthy(raw.GetValueOrDefault("network")))
        {
            throw new ConfigException("compose is mutually exclusive with network");
        }

        ValidateComposeCommand();
    }

    /// <summary>
    /// <c>compose.command</c> has opposite requiredness depending on which compose mode this
    /// is: <c>mode: compose</c> has no default, because every external compose-for-wslc
    /// implementation must be named explicitly, while <c>mode: compose-native</c> drives wslc
    /// directly and so has no external binary to name at all.
    /// </summary>
    private void ValidateComposeCommand()
    {
        if (IsComposeNative)
        {
            if (ComposeCommand is not null)
            {
                throw new ConfigException(
                    "compose.command is not used under mode: compose-native (wip drives wslc " +
                    "directly — there’s no external compose binary to name)");
            }

            return;
        }

        if (ComposeCommand is null)
        {
            throw new ConfigException("compose.command must not be empty");
        }
    }

    private static object? RedactSecrets(object? value) => value switch
    {
        List<object?> list => list.Select(RedactSecrets).ToList(),
        OrderedDictionary<string, object?> mapping => RedactMapping(mapping),
        _ => value,
    };

    private static OrderedDictionary<string, object?> RedactMapping(OrderedDictionary<string, object?> mapping)
    {
        var result = RubyValue.NewMapping();
        foreach (var (key, value) in mapping)
        {
            result[key] = SecretPattern().IsMatch(key) ? "[REDACTED]" : RedactSecrets(value);
        }

        return result;
    }

    // Keep this focused on names conventionally used for secret material. A blanket "key"
    // match would hide harmless fields such as public_key, while omitting API_KEY and
    // SSH_KEY would leak two of the most common credential names from `wip config`.
    [GeneratedRegex(
        "token|password|secret|credential|auth|passphrase|pwd|(?:api|access|private|ssh|encryption|signing)[_-]?key",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();
}
