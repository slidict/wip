using System.Text.RegularExpressions;
using Wip.Yaml;

namespace Wip.Configuration;

public sealed record VolumeDefinition(string Name, bool Persistent, string Mount);
public sealed record SandboxDefinition(string Name, string Image, IReadOnlyList<string> Volumes);

/// <summary>Declarative resources only; loading never creates, mounts or deletes resources.</summary>
public sealed partial class SandboxSettings
{
    public string? ResourceNamespace { get; }
    public IReadOnlyList<VolumeDefinition> Volumes { get; }
    public IReadOnlyList<SandboxDefinition> Sandboxes { get; }

    private SandboxSettings(string? resourceNamespace, List<VolumeDefinition> volumes, List<SandboxDefinition> sandboxes)
    {
        ResourceNamespace = resourceNamespace;
        Volumes = volumes.AsReadOnly();
        Sandboxes = sandboxes.AsReadOnly();
    }

    public static SandboxSettings Parse(OrderedDictionary<string, object?> raw)
    {
        var volumes = new List<VolumeDefinition>();
        var byName = new Dictionary<string, VolumeDefinition>(StringComparer.Ordinal);
        foreach (var item in Sequence(raw, "volumes"))
        {
            var entry = Entry(item, "volumes", "name", "persistent", "mount");
            var name = Name(entry, "volumes");
            if (entry.GetValueOrDefault("persistent") is not bool persistent)
                throw new ConfigException($"volumes.{name}.persistent must explicitly be true or false");
            var mount = Text(entry, "mount", $"volumes.{name}");
            if (mount.Contains(','))
                throw new ConfigException($"volumes.{name}.mount cannot contain commas");
            if (!mount.StartsWith('/') || mount.Any(char.IsControl) ||
                (mount != "/" && mount[1..].Split('/').Any(part => part is "" or "." or "..")))
                throw new ConfigException($"volumes.{name}.mount must be a canonical absolute Linux path");
            var volume = new VolumeDefinition(name, persistent, mount);
            if (!byName.TryAdd(name, volume)) throw new ConfigException($"Duplicate volume name: {name}");
            volumes.Add(volume);
        }

        var sandboxes = new List<SandboxDefinition>();
        var sandboxNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Sequence(raw, "sandboxes"))
        {
            var entry = Entry(item, "sandboxes", "name", "image", "volumes");
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
            sandboxes.Add(new SandboxDefinition(name, image, references.AsReadOnly()));
        }
        string? resourceNamespace = null;
        if (volumes.Count > 0 || sandboxes.Count > 0 || raw.ContainsKey("resource_namespace"))
        {
            resourceNamespace = Text(raw, "resource_namespace", "");
            if (!ResourceName().IsMatch(resourceNamespace)) throw new ConfigException("resource_namespace must match [a-z][a-z0-9_-]{0,62}");
        }
        return new SandboxSettings(resourceNamespace, volumes, sandboxes);
    }

    public OrderedDictionary<string, object?> ToMapping()
    {
        var result = RubyValue.NewMapping();
        if (ResourceNamespace is not null) result["resource_namespace"] = ResourceNamespace;
        result["volumes"] = Volumes.Select(v => (object?)Mapping(("name", v.Name), ("persistent", v.Persistent), ("mount", v.Mount))).ToList();
        result["sandboxes"] = Sandboxes.Select(s => (object?)Mapping(("name", s.Name), ("image", s.Image), ("volumes", s.Volumes.Cast<object?>().ToList()))).ToList();
        return result;
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
