namespace Wip.Security;

/// <summary>
/// The immutable source and expected location of scanner rules. This declaration cannot
/// select a volume, mount, network, helper image, or backend option. Null
/// <paramref name="Subpaths"/> stage the whole tree; otherwise only those directories are
/// checked out, each keeping its position under <paramref name="ContainerPath"/>. They are a
/// set: order does not change equality, the checkout or the cache entry.
/// </summary>
internal sealed record PinnedRules(string RepositoryUrl, string Commit, string ContainerPath, IReadOnlyList<string>? Subpaths = null)
{
    /// <summary>The subpaths in ordinal order, or null for the whole tree.</summary>
    internal IReadOnlyList<string>? CanonicalSubpaths =>
        Subpaths?.Order(StringComparer.Ordinal).ToArray();

    public bool Equals(PinnedRules? other) =>
        other is not null && RepositoryUrl == other.RepositoryUrl && Commit == other.Commit &&
        ContainerPath == other.ContainerPath &&
        (CanonicalSubpaths, other.CanonicalSubpaths) switch
        {
            (null, null) => true,
            ({ } mine, { } theirs) => mine.SequenceEqual(theirs, StringComparer.Ordinal),
            _ => false
        };

    public override int GetHashCode() =>
        HashCode.Combine(RepositoryUrl, Commit, ContainerPath, CanonicalSubpaths is { } subpaths ? string.Join('\n', subpaths) : null);

    internal void Validate(string tool)
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl) || RepositoryUrl.Any(char.IsControl) ||
            !Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var repository) ||
            repository.Scheme != Uri.UriSchemeHttps || repository.UserInfo.Length != 0 ||
            !Uri.IsWellFormedUriString(RepositoryUrl, UriKind.Absolute))
            throw new ConfigException(
                $"Scanner {tool}: rules repository must be an absolute HTTPS URL without userinfo or control characters, " +
                "so preparation fetches an explicit source without embedded credentials");
        if (Commit is not { Length: 40 } || !Commit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ConfigException(
                $"Scanner {tool}: rules commit must contain exactly 40 lowercase hexadecimal characters; " +
                "a short hash, branch, or tag is not an immutable pin");
        if (ContainerPath == "/")
            throw new ConfigException($"Scanner {tool}: rules staging cannot replace the container root; choose a dedicated directory");
        if (string.IsNullOrWhiteSpace(ContainerPath) || !ContainerPath.StartsWith('/') || ContainerPath.Any(char.IsControl) ||
            (ContainerPath != "/" && ContainerPath[1..].Split('/').Any(part => part is "" or "." or "..")))
            throw new ConfigException(
                $"Scanner {tool}: rules container path must be a canonical absolute Linux path, " +
                "so staging targets exactly the directory declared by the adapter");
        if (ContainerPath[1..].Split('/').Length < 2)
            throw new ConfigException(
                $"Scanner {tool}: rules container path must have at least two nonempty path segments, " +
                "so staging cannot remove a top-level container directory");
        if (Subpaths is null) return;
        if (Subpaths.Count == 0)
            throw new ConfigException(
                $"Scanner {tool}: rules subpaths must not be empty; omit them to stage the whole tree");
        foreach (var subpath in Subpaths)
            if (subpath is null || subpath.Length == 0 || subpath.StartsWith(':') || subpath.Any(char.IsControl) ||
                subpath.IndexOfAny(['\\', '*', '?', '[']) >= 0 || subpath.Split('/').Any(part => part is "" or "." or ".."))
                throw new ConfigException(
                    $"Scanner {tool}: rules subpath must be a nonempty canonical relative POSIX path without glob or pathspec syntax, " +
                    "so the checkout selects exactly the declared directory");
        // A duplicate or nested entry stages nothing more, but would give the same tree a second cache key.
        if (Subpaths.Select((subpath, index) => (subpath, index)).Any(left => Subpaths.Where((_, index) => index != left.index)
                .Any(other => left.subpath == other || left.subpath.StartsWith(other + "/", StringComparison.Ordinal))))
            throw new ConfigException(
                $"Scanner {tool}: rules subpaths must not repeat or nest inside one another, so each staged tree has one cache key");
    }
}
