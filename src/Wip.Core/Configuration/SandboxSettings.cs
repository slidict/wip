using System.Text.RegularExpressions;
using Wip.Yaml;

namespace Wip.Configuration;

/// <param name="Source">
/// Host directory to mirror, relative to wip.yml's own directory unless absolute. Kept raw
/// here: resolving it belongs to execution, which knows where the config was loaded from,
/// and a raw value normalizes the same no matter which subdirectory wip was invoked from.
/// </param>
/// <param name="Mount">Where the mirror container sees the host source, read-only.</param>
/// <param name="Image">Image the mirror runs in; it must provide the sync binary.</param>
public sealed record VolumeSyncDefinition(
    string Source,
    string Mount,
    string Image,
    IReadOnlyList<string> Exclude,
    bool Delete);

/// <param name="Sync">
/// Opt-in: declares a host source this volume is mirrored from. Declaring it neither creates
/// the volume nor runs a mirror; <c>wip volume sync</c> does.
/// </param>
public sealed record VolumeDefinition(string Name, bool Persistent, string Mount, VolumeSyncDefinition? Sync = null);

/// <param name="Internal">
/// Must be declared explicitly. <c>true</c> creates the network with WSLC's
/// <c>--internal</c>: a boundary outside the container, which nothing running inside it can
/// lift. A sandbox may only attach to an internal network, so declaring <c>false</c> leaves
/// the network unreferenced rather than handing a sandbox unrestricted egress by accident.
/// </param>
public sealed record NetworkDefinition(string Name, bool Internal);

/// <param name="ReportRelay">
/// Opt-in: execs export the report FIFO's path so the sandbox's CLI can report agent state
/// through <see cref="Execution.ReportRelay"/>. Off unless declared.
/// </param>
/// <param name="Network">
/// Opt-in: the only network the container is attached to. Declaring it replaces the
/// backend's default bridge, which is what denies egress; leaving it out keeps the
/// pre-existing behaviour of inheriting whatever the backend attaches by default.
/// </param>
/// <param name="Command">
/// Optional argv appended after the image reference, without shell parsing. Null preserves
/// the image's default command; an explicitly empty list is invalid.
/// </param>
/// <param name="Entrypoint">Optional executable override; arguments are supplied by Command.</param>
public sealed record SandboxDefinition(
    string Name,
    string Image,
    IReadOnlyList<string> Volumes,
    bool ReportRelay = false,
    string? Network = null,
    IReadOnlyList<string>? Command = null,
    string? Entrypoint = null);

/// <summary>Declarative resources only; loading never creates, mounts or deletes resources.</summary>
public sealed partial class SandboxSettings
{
    public string? ResourceNamespace { get; }
    public IReadOnlyList<VolumeDefinition> Volumes { get; }
    public IReadOnlyList<NetworkDefinition> Networks { get; }
    public IReadOnlyList<SandboxDefinition> Sandboxes { get; }

    private SandboxSettings(
        string? resourceNamespace,
        List<VolumeDefinition> volumes,
        List<NetworkDefinition> networks,
        List<SandboxDefinition> sandboxes)
    {
        ResourceNamespace = resourceNamespace;
        Volumes = volumes.AsReadOnly();
        Networks = networks.AsReadOnly();
        Sandboxes = sandboxes.AsReadOnly();
    }

    public static SandboxSettings Parse(OrderedDictionary<string, object?> raw)
    {
        var volumes = new List<VolumeDefinition>();
        var byName = new Dictionary<string, VolumeDefinition>(StringComparer.Ordinal);
        foreach (var item in Sequence(raw, "volumes"))
        {
            var entry = Entry(item, "volumes", "name", "persistent", "mount", "sync");
            var name = Name(entry, "volumes");
            if (entry.GetValueOrDefault("persistent") is not bool persistent)
                throw new ConfigException($"volumes.{name}.persistent must explicitly be true or false");
            var mount = Text(entry, "mount", $"volumes.{name}");
            if (mount.Contains(','))
                throw new ConfigException($"volumes.{name}.mount cannot contain commas");
            if (!mount.StartsWith('/') || mount.Any(char.IsControl) ||
                (mount != "/" && mount[1..].Split('/').Any(part => part is "" or "." or "..")))
                throw new ConfigException($"volumes.{name}.mount must be a canonical absolute Linux path");
            var volume = new VolumeDefinition(name, persistent, mount, ReadSync(entry.GetValueOrDefault("sync"), name, mount));
            if (!byName.TryAdd(name, volume)) throw new ConfigException($"Duplicate volume name: {name}");
            volumes.Add(volume);
        }

        // Parsed before the sandboxes so a sandbox's network reference resolves here, rather
        // than at create time, where an undeclared name would only surface once a container
        // had already been asked for.
        var networks = new List<NetworkDefinition>();
        var networksByName = new Dictionary<string, NetworkDefinition>(StringComparer.Ordinal);
        foreach (var item in Sequence(raw, "networks"))
        {
            var entry = Entry(item, "networks", "name", "internal");
            var name = Name(entry, "networks");
            if (entry.GetValueOrDefault("internal") is not bool isInternal)
                throw new ConfigException($"networks.{name}.internal must explicitly be true or false");
            var network = new NetworkDefinition(name, isInternal);
            if (!networksByName.TryAdd(name, network)) throw new ConfigException($"Duplicate network name: {name}");
            networks.Add(network);
        }

        var sandboxes = new List<SandboxDefinition>();
        var sandboxNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Sequence(raw, "sandboxes"))
        {
            var entry = Entry(item, "sandboxes", "name", "image", "volumes", "report_relay", "network", "command", "entrypoint");
            var name = Name(entry, "sandboxes");
            if (!sandboxNames.Add(name)) throw new ConfigException($"Duplicate sandbox name: {name}");
            var image = Text(entry, "image", $"sandboxes.{name}");
            var references = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var mounts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in Sequence(entry, "volumes"))
            {
                if (reference is not string volumeName || !byName.TryGetValue(volumeName, out var volume))
                    throw new ConfigException($"sandboxes.{name} references an undefined volume");
                if (!names.Add(volumeName)) throw new ConfigException($"sandboxes.{name} repeats volume {volumeName}");
                if (!mounts.Add(volume.Mount)) throw new ConfigException($"sandboxes.{name} has conflicting mount destinations");
                references.Add(volumeName);
            }
            var reportRelay = false;
            if (entry.ContainsKey("report_relay"))
            {
                if (entry["report_relay"] is not bool relay)
                    throw new ConfigException($"sandboxes.{name}.report_relay must explicitly be true or false");
                reportRelay = relay;
            }
            string? network = null;
            if (entry.ContainsKey("network"))
            {
                network = Text(entry, "network", $"sandboxes.{name}");
                if (!networksByName.TryGetValue(network, out var networkDef))
                    throw new ConfigException($"sandboxes.{name} references an undefined network: {network}");
                // A non-internal network would hand the sandbox unrestricted egress under a
                // key that reads as if it restricted one.
                if (!networkDef.Internal)
                    throw new ConfigException($"sandboxes.{name} references network {network}, which is not internal");
            }
            sandboxes.Add(new SandboxDefinition(name, image, references.AsReadOnly(), reportRelay, network, ReadCommand(entry, name), ReadEntrypoint(entry, name)));
        }
        string? resourceNamespace = null;
        if (volumes.Count > 0 || networks.Count > 0 || sandboxes.Count > 0 || raw.ContainsKey("resource_namespace"))
        {
            resourceNamespace = Text(raw, "resource_namespace", "");
            if (!ResourceName().IsMatch(resourceNamespace)) throw new ConfigException("resource_namespace must match [a-z][a-z0-9_-]{0,62}");
        }
        return new SandboxSettings(resourceNamespace, volumes, networks, sandboxes);
    }

    public OrderedDictionary<string, object?> ToMapping()
    {
        var result = RubyValue.NewMapping();
        if (ResourceNamespace is not null) result["resource_namespace"] = ResourceNamespace;
        result["volumes"] = Volumes.Select(v =>
        {
            var mapping = Mapping(("name", v.Name), ("persistent", v.Persistent), ("mount", v.Mount));
            // Emitted only when declared, so volumes without a mirror normalize exactly as before.
            if (v.Sync is { } sync)
            {
                mapping["sync"] = Mapping(
                    ("source", sync.Source),
                    ("mount", sync.Mount),
                    ("image", sync.Image),
                    ("exclude", sync.Exclude.Cast<object?>().ToList()),
                    ("delete", sync.Delete));
            }
            return (object?)mapping;
        }).ToList();
        // Emitted only when declared, so configs without networks normalize exactly as before.
        if (Networks.Count > 0)
        {
            result["networks"] = Networks
                .Select(n => (object?)Mapping(("name", n.Name), ("internal", n.Internal)))
                .ToList();
        }
        result["sandboxes"] = Sandboxes.Select(s =>
        {
            var mapping = Mapping(("name", s.Name), ("image", s.Image), ("volumes", s.Volumes.Cast<object?>().ToList()));
            // Emitted only when enabled, so configs without a relay normalize exactly as before.
            if (s.ReportRelay) mapping["report_relay"] = true;
            if (s.Network is { } network) mapping["network"] = network;
            if (s.Entrypoint is { } entrypoint) mapping["entrypoint"] = entrypoint;
            if (s.Command is { } command) mapping["command"] = command.Cast<object?>().ToList();
            return (object?)mapping;
        }).ToList();
        return result;
    }

    // WSLC replaces only the executable; arguments belong in command, not a shell line.
    private static string? ReadEntrypoint(OrderedDictionary<string, object?> entry, string name)
    {
        if (!entry.ContainsKey("entrypoint")) return null;
        if (entry["entrypoint"] is not string executable || executable.Length == 0 ||
            executable.StartsWith('-') || executable.Any(char.IsWhiteSpace) || executable.Any(char.IsControl))
            throw new ConfigException($"sandboxes.{name}.entrypoint must be a nonempty executable string without whitespace, control characters or a leading '-'; put arguments in command");
        // --entrypoint also clears the image's CMD, so the executable would run with no operand.
        if (!entry.ContainsKey("command"))
            throw new ConfigException($"sandboxes.{name}.entrypoint requires command; the override clears the image's CMD, so give the executable's arguments in command, as in [infinity]");
        return executable;
    }

    private static IReadOnlyList<string>? ReadCommand(OrderedDictionary<string, object?> entry, string name)
    {
        if (!entry.ContainsKey("command")) return null;
        var path = $"sandboxes.{name}.command";
        var argv = RubyValue.AsSequence(entry["command"])
            ?? throw new ConfigException($"{path} must be a sequence of arguments");
        if (argv.Count == 0) throw new ConfigException($"{path} must not be empty");
        var command = new List<string>();
        foreach (var argument in argv)
        {
            // The message names quoting because `command: [sleep, 600]` is the natural
            // spelling and 600 loads as a number, which lands here looking like an empty entry.
            if (argument is not string text || text.Length == 0 || text.Any(char.IsControl))
                throw new ConfigException(
                    $"{path} entries must be nonempty strings without control characters; quote numbers and booleans, as in [sleep, '600']");
            // Catches a shell line written as one element -- `[sleep 600]` is valid YAML and
            // becomes argv[0] "sleep 600", which otherwise fails at run time with a message
            // that never mentions the command. A later argument may hold spaces legitimately,
            // as in [nginx, -g, 'daemon off;'].
            if (command.Count == 0 && text.Any(char.IsWhiteSpace))
                throw new ConfigException(
                    $"{path} first argument must not contain whitespace; split a command line into separate entries, and run a program whose own path contains a space through [sh, -c, '...']");
            // Each argument follows the image reference and is passed through ArgumentList,
            // so leading '-' values belong to the container command, not backend options.
            command.Add(text);
        }
        return command.AsReadOnly();
    }

    /// <summary>
    /// Validation only; the mirror itself is <c>wip volume sync</c>'s job. <c>image</c> is
    /// required because the mirror runs in a throwaway container of its own, and the sandbox
    /// images that mount the volume are not guaranteed to carry rsync.
    /// </summary>
    private static VolumeSyncDefinition? ReadSync(object? raw, string name, string target)
    {
        if (raw is null) return null;
        var path = $"volumes.{name}.sync";
        var entry = Entry(raw, path, "source", "mount", "image", "exclude", "delete");
        var source = entry.ContainsKey("source") ? Text(entry, "source", path) : ".";
        if (source.Contains(',')) throw new ConfigException($"{path}.source cannot contain commas");
        var mount = entry.ContainsKey("mount") ? Text(entry, "mount", path) : "/host-src";
        if (mount.Contains(',')) throw new ConfigException($"{path}.mount cannot contain commas");
        if (!mount.StartsWith('/') || (mount != "/" && mount[1..].Split('/').Any(part => part is "" or "." or "..")))
            throw new ConfigException($"{path}.mount must be a canonical absolute Linux path");
        // rsync copies the mount's contents into the target, so an overlapping pair would make
        // the source its own destination. Nesting either way counts: /data under /data/host
        // puts the target inside the source, and /data/host under /data the reverse.
        var mirrorSource = mount.TrimEnd('/');
        var mirrorTarget = target.TrimEnd('/');
        if (mirrorSource == mirrorTarget ||
            mirrorSource.StartsWith(mirrorTarget + "/", StringComparison.Ordinal) ||
            mirrorTarget.StartsWith(mirrorSource + "/", StringComparison.Ordinal))
            throw new ConfigException($"{path}.mount must not overlap the volume's own mount");
        var image = Text(entry, "image", path);
        // A leading dash would be read as an option by the backend's run, not as an image.
        if (image.StartsWith('-')) throw new ConfigException($"{path}.image must not start with '-'");
        var exclude = new List<string>();
        foreach (var pattern in Sequence(entry, "exclude"))
        {
            if (pattern is not string text || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
                throw new ConfigException($"{path}.exclude entries must be nonempty strings without control characters");
            if (text.StartsWith('-')) throw new ConfigException($"{path}.exclude entries must not start with '-'");
            exclude.Add(text);
        }
        var delete = true;
        if (entry.ContainsKey("delete"))
        {
            if (entry["delete"] is not bool value)
                throw new ConfigException($"{path}.delete must explicitly be true or false");
            delete = value;
        }
        return new(source, mount, image, exclude.AsReadOnly(), delete);
    }

    private static OrderedDictionary<string, object?> Mapping(params (string Key, object? Value)[] values)
    {
        var result = RubyValue.NewMapping();
        foreach (var (key, value) in values) result[key] = value;
        return result;
    }

    private static List<object?> Sequence(OrderedDictionary<string, object?> raw, string key) =>
        !raw.ContainsKey(key) ? [] : RubyValue.AsSequence(raw[key]) ?? throw new ConfigException($"{key} must be a sequence");

    private static OrderedDictionary<string, object?> Entry(object? value, string path, params string[] keys)
    {
        var entry = RubyValue.AsMapping(value) ?? throw new ConfigException($"{path} entries must be mappings");
        if (entry.Keys.Any(key => !keys.Contains(key))) throw new ConfigException($"{path} entry contains an unsupported key");
        return entry;
    }

    private static string Text(OrderedDictionary<string, object?> entry, string key, string path) =>
        entry.GetValueOrDefault(key) is string text && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl)
            ? text : throw new ConfigException($"{(path.Length == 0 ? key : path + "." + key)} must be a nonempty string without control characters");

    private static string Name(OrderedDictionary<string, object?> entry, string path)
    {
        var name = Text(entry, "name", path);
        if (!ResourceName().IsMatch(name)) throw new ConfigException($"{path}.name must match [a-z][a-z0-9_-]{{0,62}}");
        return name;
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceName();
}
