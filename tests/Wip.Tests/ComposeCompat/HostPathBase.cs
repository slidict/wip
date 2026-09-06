namespace Wip.Tests.ComposeCompat;

/// <summary>
/// Windows path rules, spelled out rather than taken from the BCL.
/// </summary>
/// <remarks>
/// <para>
/// wslc is a Windows program, so <c>C:\src</c> is absolute, <c>/srv/shared</c> is only
/// <em>root</em>-relative — it inherits a drive from whatever it is resolved against — and
/// both facts hold whichever machine the compatibility suite runs on. <see cref="Path"/> would
/// answer the opposite question on Linux and the recordings would then differ per platform for
/// reasons that have nothing to do with either Compose implementation.
/// </para>
/// <para>
/// The drive a root-relative source lands on is a property of the resolving process, not of
/// the fixture, so it is recorded as <see cref="DrivePlaceholder"/>.
/// </para>
/// </remarks>
internal static class HostPathBase
{
    internal const string DrivePlaceholder = "<DRIVE>";

    internal enum Anchor
    {
        /// <summary>A drive or UNC root of its own: <c>C:\src</c>, <c>\\host\share</c>.</summary>
        Absolute,

        /// <summary>Rooted but driveless: <c>/srv/shared</c> takes the resolver's drive.</summary>
        RootRelative,

        /// <summary>Everything else: <c>.</c>, <c>./conf</c>, <c>conf</c>.</summary>
        Relative,
    }

    internal static Anchor AnchorOf(string path)
    {
        if ((path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') ||
            (path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/'))
        {
            return Anchor.Absolute;
        }

        return path.Length >= 1 && path[0] is '\\' or '/' ? Anchor.RootRelative : Anchor.Relative;
    }

    internal static bool IsAbsolute(string path) => AnchorOf(path) == Anchor.Absolute;

    /// <summary>
    /// <c>std::filesystem::path::lexically_normal</c>, restricted to what a compose source can
    /// contain: separators unified to '/', '.' dropped, '..' folded, no trailing separator.
    /// </summary>
    internal static string Normalize(string path)
    {
        var unified = path.Replace('\\', '/');
        var prefix = string.Empty;

        if (unified.Length >= 2 && char.IsAsciiLetter(unified[0]) && unified[1] == ':')
        {
            prefix = unified[..2];
            unified = unified[2..];
        }

        var rooted = unified.StartsWith('/');
        var segments = new List<string>();
        foreach (var segment in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." && segments.Count > 0 && segments[^1] != "..")
            {
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        var body = string.Join('/', segments);
        return prefix + (rooted ? "/" + body : body);
    }

    /// <summary>
    /// Resolves <paramref name="source"/> the way <c>operator/</c> then
    /// <c>std::filesystem::absolute</c> would: an absolute source stands alone, a root-relative
    /// one drops <paramref name="baseDirectory"/>'s directory part and keeps only its drive,
    /// and a relative one hangs off the directory itself.
    /// </summary>
    internal static string Resolve(string baseDirectory, string source) => AnchorOf(source) switch
    {
        Anchor.Absolute => Normalize(source),
        Anchor.RootRelative => DrivePlaceholder + Normalize(source),
        _ => Normalize($"{baseDirectory.Replace('\\', '/')}/{source}"),
    };
}
