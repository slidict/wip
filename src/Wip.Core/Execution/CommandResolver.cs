namespace Wip.Execution;

/// <summary>Locates an executable on the current system, trying a list of candidates.</summary>
public sealed class CommandResolver
{
    /// <summary>
    /// wslc.exe is a Windows binary, so it is found on PATH or in System32. The Ruby build
    /// also tried <c>/mnt/c/Windows/System32/wslc.exe</c>, which only meant anything when wip
    /// itself ran inside a distribution; that candidate is gone.
    /// </summary>
    public static IReadOnlyList<string> DefaultCandidates { get; } = BuildDefaultCandidates();

    public const string DefaultInstallHint = """
        Install or update the WSL container tooling with:

          wsl --update

        If Microsoft Store updates are unavailable, download the stable release from GitHub:

          wsl --update --web-download

        WSLC is generally available in WSL 3.0.1 and later. Check your version with:

          wsl --version

        Then run:

          wip doctor
        """;

    private readonly IReadOnlyList<string> candidates;
    private readonly string label;
    private readonly string installHint;
    private readonly Func<string, string?> resolveExecutable;

    public CommandResolver(
        IReadOnlyList<string>? candidates = null,
        string label = "WSLC",
        string? installHint = null,
        Func<string, string?>? resolveExecutable = null)
    {
        this.candidates = candidates ?? DefaultCandidates;
        this.label = label;
        this.installHint = installHint ?? DefaultInstallHint;
        this.resolveExecutable = resolveExecutable ?? ResolveExecutable;
    }

    public string Resolve(string configured = "auto")
    {
        if (configured != "auto")
        {
            RejectRelativePath(configured);
            RejectNonLocalPath(configured);
            return TryGetFullPath(resolveExecutable(configured)) ?? throw NotFound([configured]);
        }

        foreach (var candidate in candidates)
        {
            var resolved = TryGetFullPath(resolveExecutable(candidate));
            if (resolved is not null)
            {
                return resolved;
            }
        }

        throw NotFound(candidates);
    }

    /// <summary>
    /// A configured command must be a bare name, searched on PATH, or a fully qualified path.
    /// Anything else with a separator in it, or rooted, is refused before any resolver sees
    /// it, an injected one included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value comes from <c>wip.yml</c>, which is part of the project tree. A relative path
    /// would be resolved against whatever directory wip was run from, so a <c>wip.yml</c> in a
    /// freshly cloned repository could pick a binary inside that repository and have it run with
    /// the user's privileges. A bare name is not affected: the PATH walk already skips entries
    /// that are not fully qualified.
    /// </para>
    /// <para>
    /// Refusing beats re-rooting against the project root. Re-rooting removes only the "depends
    /// on where you ran wip from" part; the value would still come from the cloned tree and
    /// still name a binary in it. <see cref="Path.IsPathFullyQualified(string)"/> rather than
    /// <c>IsPathRooted</c>, because the drive-relative <c>C:wslc.exe</c> and the root-relative
    /// <c>\wslc.exe</c> are rooted yet still depend on the current drive or directory.
    /// </para>
    /// <para>
    /// Thrown as <see cref="CommandNotFoundException"/>, keeping it the only failure callers
    /// have to handle; the message is what says why.
    /// </para>
    /// </remarks>
    private void RejectRelativePath(string configured)
    {
        // Rooted catches C:wslc.exe, which has no separator, yet as a bare name would reach
        // Path.Combine in the PATH walk and come back as itself.
        var isPath = configured.Contains(Path.DirectorySeparatorChar)
                     || configured.Contains(Path.AltDirectorySeparatorChar)
                     || Path.IsPathRooted(configured);
        if (!isPath || Path.IsPathFullyQualified(configured))
        {
            return;
        }

        throw new CommandNotFoundException(
            $"""
             {label} was configured as a relative path, which wip refuses: "{configured}"

             Configure an absolute path, or a bare name to search for on PATH. A relative
             path would be resolved against whatever directory wip was run from, so a wip.yml
             in a cloned repository could choose a binary inside that repository.
             """);
    }

    /// <summary>
    /// On Windows a configured path must also sit on a drive: UNC (<c>\\server\share\...</c>,
    /// <c>//server/share/...</c>) and device paths (<c>\\?\...</c>, <c>\\.\...</c>) are
    /// refused, although <see cref="Path.IsPathFullyQualified(string)"/> accepts them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The threat is the one <see cref="RejectRelativePath"/> closes, only easier: a
    /// <c>wip.yml</c> naming <c>\\host\share\wslc.exe</c> needs no knowledge of where the
    /// victim cloned it, and the binary is fetched over SMB and run with the user's privileges.
    /// </para>
    /// <para>
    /// A mapped network drive (<c>Z:\...</c>) is allowed on purpose. It looks exactly like a
    /// local drive, it has to exist on the user's machine already, so the repository cannot
    /// bring it along, and refusing it would break an operator who keeps tools there.
    /// </para>
    /// <para>
    /// Unix has no UNC form, and a fully qualified path there already means one starting with
    /// <c>/</c>, so nothing is added on Unix.
    /// </para>
    /// </remarks>
    private void RejectNonLocalPath(string configured)
    {
        // A bare name has no root; RejectRelativePath has already refused any rooted value
        // that is not fully qualified.
        if (!OperatingSystem.IsWindows() || !Path.IsPathRooted(configured)
            || IsDriveRoot(Path.GetPathRoot(configured)))
        {
            return;
        }

        throw new CommandNotFoundException(
            $"""
             {label} was configured as a network or device path, which wip refuses: "{configured}"

             Configure an absolute path on a drive, such as C:\, or a bare name to search for
             on PATH. A UNC or device path lets a wip.yml in a cloned repository run a binary
             from another machine.
             """);
    }

    /// <summary>True for a root shaped like <c>C:\</c> or <c>C:/</c>, the only root a configured path may have on Windows.</summary>
    internal static bool IsDriveRoot(string? root) =>
        root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] is '\\' or '/';

    private static IReadOnlyList<string> BuildDefaultCandidates()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
                "wslc.exe"));
        }

        candidates.Add("wslc.exe");
        candidates.Add("wslc");

        return candidates;
    }

    /// <summary>
    /// A name containing a separator is taken as a path; anything else is searched for on
    /// PATH, skipping any entry that is not fully qualified. On Windows an extensionless name
    /// is retried against each PATHEXT suffix, which is what lets a bare "wslc" resolve to wslc.exe.
    /// </summary>
    private static string? ResolveExecutable(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return ResolveFile(command);
        }

        var path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // A relative entry such as "." is the working directory, which this search must not reach.
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var resolved = ResolveFile(Path.Combine(directory, command));
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveFile(string candidate)
    {
        // Freeze relative paths before checking them so the returned value cannot be
        // reinterpreted against a different working directory by the eventual caller.
        var fullPath = TryGetFullPath(candidate);
        if (fullPath is not null && File.Exists(fullPath))
        {
            return fullPath;
        }

        if (!OperatingSystem.IsWindows() || Path.HasExtension(candidate))
        {
            return null;
        }

        var extensions = System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        foreach (var extension in extensions.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var extendedPath = TryGetFullPath(candidate + extension);
            if (extendedPath is not null && File.Exists(extendedPath))
            {
                return extendedPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes a candidate against the current working directory, including one produced
    /// by an injected resolver, so a later directory change cannot reinterpret it. A path the
    /// runtime refuses to normalize (invalid characters, too long, unsupported format) counts
    /// as unresolved, keeping <see cref="CommandNotFoundException"/> the only failure callers
    /// have to handle.
    /// </summary>
    private static string? TryGetFullPath(string? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception exception)
            when (exception is ArgumentException or IOException or NotSupportedException
                or System.Security.SecurityException)
        {
            return null;
        }
    }

    private CommandNotFoundException NotFound(IReadOnlyList<string> attempted) => new(
        $"""
         {label} was not found.

         Checked:
           {string.Join("\n  ", attempted)}

         {installHint}
         """);
}
