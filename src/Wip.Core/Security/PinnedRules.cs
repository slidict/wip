namespace Wip.Security;

/// <summary>
/// The immutable source and expected location of scanner rules. This declaration cannot
/// select a volume, mount, network, helper image, or backend option. A null
/// <paramref name="Subpath"/> stages the whole tree; otherwise only that directory is checked
/// out, keeping its position under <paramref name="ContainerPath"/>.
/// </summary>
internal sealed record PinnedRules(string RepositoryUrl, string Commit, string ContainerPath, string? Subpath = null)
{
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
        if (Subpath is not null && (Subpath.Length == 0 || Subpath.StartsWith(':') || Subpath.Any(char.IsControl) ||
            Subpath.IndexOfAny(['\\', '*', '?', '[']) >= 0 || Subpath.Split('/').Any(part => part is "" or "." or "..")))
            throw new ConfigException(
                $"Scanner {tool}: rules subpath must be a nonempty canonical relative POSIX path without glob or pathspec syntax, " +
                "so the checkout selects exactly the declared directory");
    }
}
