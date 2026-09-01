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
        Install or update the WSL container tooling, then run:

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
    /// PATH. On Windows an extensionless name is retried against each PATHEXT suffix, which
    /// is what lets a bare "wslc" resolve to wslc.exe.
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
