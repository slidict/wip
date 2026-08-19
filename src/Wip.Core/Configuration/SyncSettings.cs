using Wip.Platform;
using Wip.Yaml;

namespace Wip.Configuration;

/// <summary>
/// Validated access to the <c>sync:</c> block, which mirrors the host source tree into a
/// named volume instead of bind-mounting it live.
/// </summary>
/// <remarks>
/// A bind-mounted app directory is shared into the container's VM over virtiofs, so every
/// stat and open a boot-time directory scan makes is a round trip. Mirroring the tree into a
/// named volume — native storage inside the VM — and re-running the mirror on demand keeps
/// the edit-on-the-host workflow while leaving the running app on fast disk.
/// </remarks>
public sealed class SyncSettings
{
    public const string DefaultMount = "/host-src";
    public const string DefaultTarget = "/app";
    public const string DefaultBinary = "rsync";
    // Long rather than double so an unset interval serialises as "2", the way the Ruby
    // integer default did, instead of as a float.
    public const long DefaultInterval = 2;

    /// <summary>
    /// Minimal set for a fast local-to-local mirror: <c>-r</c> walks the tree, <c>-l</c>
    /// keeps symlinks as symlinks, <c>-t</c> preserves mtimes so re-syncs can quick-check
    /// (size plus mtime) instead of re-transferring unchanged files, and
    /// <c>--whole-file</c> skips the delta-transfer checksum pass that only pays off over a
    /// slow network. Owner, group, and permission preservation is left out since both sides
    /// are the same user; add them back via <c>sync.options</c> if a project needs them.
    /// </summary>
    private static readonly string[] BaseOptions = ["-r", "-l", "-t", "--whole-file"];

    /// <summary>Trailing mount options wslc and docker accept after the container path.</summary>
    private static readonly string[] VolumeModes = ["ro", "rw", "z", "Z", "cached", "delegated", "consistent"];

    private static readonly string[] Modes = ["exec", "run"];

    private readonly string rawSource;
    private readonly string? basePath;
    private string? resolvedSource;

    public SyncSettings(
        object? raw,
        string? basePath = null,
        string? workdir = null,
        string? container = null,
        bool compose = false)
    {
        var mapping = RubyValue.AsMapping(raw)
                      ?? throw new ConfigException("sync must be a mapping");

        this.basePath = basePath;
        rawSource = RubyValue.Presence(mapping.GetValueOrDefault("source")) ?? ".";
        Target = RubyValue.Presence(mapping.GetValueOrDefault("target")) ?? workdir.Presence() ?? DefaultTarget;
        Mount = RubyValue.Presence(mapping.GetValueOrDefault("mount")) ?? DefaultMount;
        ContainerName = container.Presence() ?? "wip";
        Volume = RubyValue.Presence(mapping.GetValueOrDefault("volume")) ?? $"{ContainerName}-src";

        Delete = !mapping.TryGetValue("delete", out var delete) || RubyValue.IsTruthy(delete);
        Exclude = RubyValue.AsArray(mapping.GetValueOrDefault("exclude")).Select(RubyValue.ToStringValue).ToList();
        Binary = RubyValue.Presence(mapping.GetValueOrDefault("command")) ?? DefaultBinary;
        ExtraOptions = RubyValue.AsArray(mapping.GetValueOrDefault("options")).Select(RubyValue.ToStringValue).ToList();
        RawInterval = mapping.TryGetValue("interval", out var interval) ? interval : DefaultInterval;

        Build = ReadBuild(mapping.GetValueOrDefault("build"));
        ReadMode(mapping, compose);
        Validate();
    }

    public string Target { get; }

    public string Mount { get; }

    public string Volume { get; }

    public string ContainerName { get; }

    public bool Delete { get; }

    public IReadOnlyList<string> Exclude { get; }

    public string Binary { get; }

    public IReadOnlyList<string> ExtraOptions { get; }

    public object? RawInterval { get; }

    public string Mode { get; private set; } = "exec";

    public string? Image { get; private set; }

    public OrderedDictionary<string, object?>? Build { get; }

    public bool IsExec => Mode == "exec";

    public double Interval => Convert.ToDouble(RawInterval, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Expanded against the wip.yml directory so the mirror covers the same tree no matter
    /// which subdirectory wip was invoked from.
    /// </summary>
    public string Source =>
        resolvedSource ??= basePath is null
            ? rawSource
            : WslPath.ForWslc(Path.GetFullPath(Path.Combine(basePath, rawSource)), "sync.source");

    /// <summary>
    /// What <c>-v</c> specs the main container needs: the source read-only, and the named
    /// volume where the app actually runs.
    /// </summary>
    public IReadOnlyList<string> VolumeSpecs => [$"{Source}:{Mount}:ro", $"{Volume}:{Target}"];

    /// <summary>
    /// True for a configured volume that sync replaces, so <c>.:/app</c> in the primary
    /// container's <c>volumes</c> quietly becomes the read-only mount plus the volume.
    /// </summary>
    public bool Replaces(string spec)
    {
        var containerPath = ContainerPath(spec);
        return containerPath == Target.TrimEnd('/') || containerPath == Mount.TrimEnd('/');
    }

    /// <summary>
    /// Trailing slashes matter to rsync: they copy the <em>contents</em> of the mount into
    /// the target rather than nesting it one directory deeper.
    /// </summary>
    public IReadOnlyList<string> MirrorCommand()
    {
        var command = new List<string> { Binary };
        command.AddRange(BaseOptions);
        if (Delete)
        {
            command.Add("--delete");
        }

        command.AddRange(Exclude.Select(pattern => $"--exclude={pattern}"));
        command.AddRange(ExtraOptions);
        command.Add($"{Mount.TrimEnd('/')}/");
        command.Add($"{Target.TrimEnd('/')}/");
        return command;
    }

    public OrderedDictionary<string, object?> ToMapping()
    {
        var result = RubyValue.NewMapping();
        result["source"] = Source;
        result["target"] = Target;
        result["mount"] = Mount;
        result["volume"] = Volume;
        result["delete"] = Delete;
        result["exclude"] = Exclude.Cast<object?>().ToList();
        result["command"] = Binary;
        result["options"] = ExtraOptions.Cast<object?>().ToList();
        result["interval"] = RawInterval;
        result["mode"] = Mode;
        result["image"] = Image;
        result["build"] = Build;
        return result;
    }

    /// <summary>
    /// <c>sync.build</c> lets wip build a small, dedicated mirror image itself (alpine plus
    /// rsync, say) instead of requiring one to already exist — handy since
    /// <c>sync.mode: run</c> boots a fresh container per mirror, and reusing the app's full
    /// image just adds startup overhead for something that only ever runs rsync.
    /// </summary>
    private OrderedDictionary<string, object?>? ReadBuild(object? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var mapping = RubyValue.AsMapping(raw) ?? throw new ConfigException("sync.build must be a mapping");
        var dockerfile = RubyValue.Presence(mapping.GetValueOrDefault("dockerfile"))
                         ?? throw new ConfigException("sync.build.dockerfile must not be empty");

        var build = RubyValue.NewMapping();
        build["dockerfile"] = dockerfile;
        build["tag"] = RubyValue.Presence(mapping.GetValueOrDefault("tag")) ?? $"wip-sync-{ContainerName}:latest";
        return build;
    }

    private void ReadMode(OrderedDictionary<string, object?> mapping, bool compose)
    {
        Mode = RubyValue.Presence(mapping.GetValueOrDefault("mode")) ?? (compose ? "run" : "exec");
        Image = RubyValue.Presence(mapping.GetValueOrDefault("image"));

        if (!Modes.Contains(Mode))
        {
            throw new ConfigException($"sync.mode must be one of {string.Join(", ", Modes)}");
        }

        // compose mode has no dependencies: entry to fall back to for the mirror container's
        // image, so it cannot be left implicit there.
        if (compose && Image is null && Build is null)
        {
            throw new ConfigException(
                "sync.image or sync.build is required under mode: compose (there’s no " +
                "dependencies: entry to borrow the mirror container’s image from)");
        }

        if (compose && IsExec)
        {
            throw new ConfigException(
                "sync.mode: exec needs mode: container (compose owns its services’ mounts, " +
                "so it can’t guarantee the running container has the sync mounts attached)");
        }
    }

    private void Validate()
    {
        if (!Target.StartsWith('/'))
        {
            throw new ConfigException("sync.target must be an absolute path");
        }

        if (!Mount.StartsWith('/'))
        {
            throw new ConfigException("sync.mount must be an absolute path");
        }

        if (Mount.TrimEnd('/') == Target.TrimEnd('/'))
        {
            throw new ConfigException("sync.mount must differ from sync.target");
        }

        if (RawInterval is not (long or int or double) || Interval <= 0)
        {
            throw new ConfigException("sync.interval must be a positive number");
        }
    }

    private static string ContainerPath(string spec)
    {
        var parts = spec.Split(':').ToList();
        if (parts.Count > 2 && VolumeModes.Contains(parts[^1]))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.Count == 0 ? string.Empty : parts[^1].TrimEnd('/');
    }
}
