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
    private readonly Func<string, bool> isExecutable;

    public CommandResolver(
        IReadOnlyList<string>? candidates = null,
        string label = "WSLC",
        string? installHint = null,
        Func<string, bool>? isExecutable = null)
    {
        this.candidates = candidates ?? DefaultCandidates;
        this.label = label;
        this.installHint = installHint ?? DefaultInstallHint;
        this.isExecutable = isExecutable ?? IsExecutable;
    }

    public string Resolve(string configured = "auto")
    {
        if (configured != "auto")
        {
            return isExecutable(configured) ? configured : throw NotFound([configured]);
        }

        foreach (var candidate in candidates)
        {
            if (isExecutable(candidate))
            {
                return candidate;
            }
        }

        throw NotFound(candidates);
    }

    private static IReadOnlyList<string> BuildDefaultCandidates()
    {
        var candidates = new List<string> { "wslc.exe", "wslc" };
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
                "wslc.exe"));
        }

        return candidates;
    }

    /// <summary>
    /// A name containing a separator is taken as a path; anything else is searched for on
    /// PATH. On Windows an extensionless name is retried against each PATHEXT suffix, which
    /// is what lets a bare "wslc" resolve to wslc.exe.
    /// </summary>
    private static bool IsExecutable(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return FileExists(command);
        }

        var path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (FileExists(Path.Combine(directory, command)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FileExists(string candidate)
    {
        if (File.Exists(candidate))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows() || Path.HasExtension(candidate))
        {
            return false;
        }

        var extensions = System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        return extensions.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(extension => File.Exists(candidate + extension));
    }

    private CommandNotFoundException NotFound(IReadOnlyList<string> attempted) => new(
        $"""
         {label} was not found.

         Checked:
           {string.Join("\n  ", attempted)}

         {installHint}
         """);
}
