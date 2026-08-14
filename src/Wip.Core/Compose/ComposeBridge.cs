namespace Wip.Compose;

/// <summary>
/// Builds argument arrays for compose-for-wslc invocations, delegating orchestration to a
/// real compose.yml instead of wip's own dependencies/network handling.
/// </summary>
public sealed class ComposeBridge
{
    public static readonly string[] Filenames =
        ["compose.yml", "compose.yaml", "docker-compose.yml", "docker-compose.yaml"];

    /// <summary>
    /// No default candidates: wip doesn't favour any one compose-for-wslc implementation.
    /// <c>compose.command</c> must name the one you've installed.
    /// </summary>
    public const string InstallHint = """
        wip doesn't bundle or pin a compose-for-wslc implementation — install one and set
        compose.command in wip.yml to its binary name or path, e.g.:

          https://github.com/bacarndiaye/wslc-compose
          https://github.com/inuyume/wslc-compose
        """;

    private readonly string composeCommand;
    private readonly string file;
    private readonly string? project;

    public ComposeBridge(string composeCommand, string file, string? project = null)
    {
        this.composeCommand = composeCommand;
        this.file = file;
        this.project = project;
    }

    /// <summary>
    /// A relative <c>compose.file</c> resolves against wip.yml, not the current directory,
    /// so wip behaves the same from any subdirectory — matching the auto-detection below.
    /// </summary>
    public static string FilePath(string configPath, string? configuredFile)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        if (configuredFile is not null)
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, configuredFile));
        }

        foreach (var name in Filenames)
        {
            var candidate = Path.Combine(baseDirectory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ConfigException(
            $"compose mode: no compose file found next to {configPath} " +
            $"(looked for {string.Join(", ", Filenames)})");
    }

    public IReadOnlyList<string> Up(bool detach = true)
    {
        var command = Base();
        command.Add("up");
        if (detach)
        {
            command.Add("-d");
        }

        return command;
    }

    public IReadOnlyList<string> Stop()
    {
        var command = Base();
        command.Add("stop");
        return command;
    }

    public IReadOnlyList<string> Down()
    {
        var command = Base();
        command.Add("down");
        return command;
    }

    public IReadOnlyList<string> Exec(string service, IEnumerable<string> arguments, bool interactive = true)
    {
        var command = Base();
        command.Add("exec");
        if (!interactive)
        {
            command.Add("-T");
        }

        command.Add(service);
        command.AddRange(arguments);
        return command;
    }

    public IReadOnlyList<string> Logs(IEnumerable<string>? services = null, bool follow = true)
    {
        var command = Base();
        command.Add("logs");
        if (follow)
        {
            command.Add("-f");
        }

        command.AddRange(services ?? []);
        return command;
    }

    private List<string> Base()
    {
        var command = new List<string> { composeCommand, "-f", file };
        if (project is not null)
        {
            command.Add("-p");
            command.Add(project);
        }

        return command;
    }
}
