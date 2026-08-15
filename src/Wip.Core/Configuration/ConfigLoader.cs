using Wip.Yaml;

namespace Wip.Configuration;

/// <summary>Finds and parses wip.yml, searching parent directories when unset.</summary>
public sealed class ConfigLoader
{
    public const string Filename = "wip.yml";

    private readonly string startDirectory;
    private readonly string? path;
    private readonly string? envFile;

    public ConfigLoader(string? startDirectory = null, string? path = null, string? envFile = null)
    {
        this.startDirectory = Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory());
        this.path = path;
        this.envFile = envFile;
    }

    public string? Find()
    {
        if (path is not null)
        {
            return Path.GetFullPath(path);
        }

        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, Filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public Config Load()
    {
        var found = Find();
        if (found is null || !File.Exists(found))
        {
            throw new ConfigException(
                $"wip.yml was not found (searched from {startDirectory} to the filesystem root)");
        }

        // Aliases stay disallowed, as they were under Psych's safe_load: a wip.yml is small
        // enough that anchors buy little, and refusing them keeps every later walk over this
        // tree free of cycle handling.
        var document = YamlLoader.LoadFile(found, allowAliases: false);
        return new Config(document, found, envFile);
    }
}
