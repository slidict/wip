using Wip.Compose;
using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;

namespace Wip.Diagnostics;

/// <summary>Runs environment diagnostics and reports pass/warn/fail results.</summary>
public sealed class Doctor
{
    public enum Level
    {
        Ok,
        Warn,
        Fail,
    }

    public readonly record struct Result(Level Level, string Message);

    private readonly ConfigLoader loader;
    private readonly CommandResolver resolver;
    private readonly CommandResolver composeResolver;
    private readonly IEnvironment environment;

    public Doctor(
        ConfigLoader loader,
        IEnvironment environment,
        CommandResolver? resolver = null,
        CommandResolver? composeResolver = null)
    {
        this.loader = loader;
        this.environment = environment;
        this.resolver = resolver ?? new CommandResolver();
        this.composeResolver = composeResolver ??
                               new CommandResolver([], "compose command", ComposeBridge.InstallHint);
    }

    public IReadOnlyList<Result> Call()
    {
        var results = new List<Result>
        {
            new(environment.IsWsl2 ? Level.Ok : Level.Fail,
                environment.IsWsl2 ? "WSL2 is available" : "WSL2 is not available"),
            new(Level.Ok, $"Architecture: {environment.Architecture}"),
        };

        var config = LoadConfig(results);
        CheckConfig(config, results);

        results.Add(new Result(
            CommandAvailable("git") ? Level.Ok : Level.Warn,
            CommandAvailable("git") ? "Git is available" : "Git is not available to the WSLC build environment"));

        return results;
    }

    private Config? LoadConfig(List<Result> results)
    {
        try
        {
            var config = loader.Load();
            results.Add(ContainerResult(config));
            return config;
        }
        catch (ConfigException exception)
        {
            results.Add(new Result(Level.Fail, exception.Message));
            return null;
        }
    }

    /// <summary>
    /// A dependency with an empty image is already a load-time error, so the only way to get
    /// here with a broken primary container is a <c>container:</c> naming an entry that isn't
    /// defined — or, under compose-native, a <c>compose.service</c> naming a service
    /// compose.yml doesn't define.
    /// </summary>
    private static Result ContainerResult(Config config)
    {
        if (config.IsCompose || config.Primary is not null)
        {
            return new Result(Level.Ok, "Loaded wip.yml");
        }

        return config.IsComposeNative
            ? new Result(Level.Fail, $"compose.service '{config.Container}' has no matching service in compose.yml")
            : new Result(Level.Fail, $"No dependencies.{config.Container} entry");
    }

    private void CheckConfig(Config? config, List<Result> results)
    {
        if (config is null)
        {
            return;
        }

        CheckWslc(config, results);
        CheckProjectLocation(config, results);

        if (config.IsCompose)
        {
            CheckCompose(config, results);
        }

        if (config.IsComposeNative)
        {
            CheckComposeNative(config, results);
        }

        if (config.HasSync)
        {
            CheckSync(config, results);
        }
    }

    private void CheckWslc(Config config, List<Result> results)
    {
        if (Resolve(resolver, config.WslcCommand, results) is { } command)
        {
            CheckVersion(command, results);
        }
    }

    private static string? Resolve(CommandResolver resolver, string configured, List<Result> results)
    {
        try
        {
            var command = resolver.Resolve(configured);
            results.Add(new Result(Level.Ok, $"Found {command}"));
            return command;
        }
        catch (CommandNotFoundException exception)
        {
            results.Add(new Result(Level.Fail, exception.Message));
            return null;
        }
    }

    private static void CheckVersion(string command, List<Result> results, string label = "WSLC")
    {
        results.Add(ProcessProbe.Succeeds(command, ["version"])
            ? new Result(Level.Ok, $"{label} is available")
            : new Result(Level.Fail, $"{label} version failed"));
    }

    private void CheckCompose(Config config, List<Result> results)
    {
        if (Resolve(composeResolver, config.ComposeCommand ?? "auto", results) is { } command)
        {
            CheckVersion(command, results, "compose command");
        }

        CheckComposeFile(config, results);
    }

    /// <summary>
    /// Returns whether the file was found, so the compose-native check can skip parsing a
    /// file already reported missing.
    /// </summary>
    private static bool CheckComposeFile(Config config, List<Result> results)
    {
        try
        {
            var path = ComposeBridge.FilePath(config.Path ?? ".", config.ComposeFilePath);
            var found = File.Exists(path);
            results.Add(new Result(
                found ? Level.Ok : Level.Fail,
                found ? $"Found compose file {path}" : $"Compose file not found: {path}"));
            return found;
        }
        catch (ConfigException exception)
        {
            results.Add(new Result(Level.Fail, exception.Message));
            return false;
        }
    }

    /// <summary>
    /// compose-native has no external binary to check — the wslc check above already covers
    /// the one binary it drives — just that compose.yml exists and parses.
    /// </summary>
    private static void CheckComposeNative(Config config, List<Result> results)
    {
        if (!CheckComposeFile(config, results))
        {
            return;
        }

        try
        {
            ComposeFile.Load(ComposeBridge.FilePath(config.Path ?? ".", config.ComposeFilePath));
            results.Add(new Result(Level.Ok, "Parsed compose file"));
        }
        catch (ConfigException exception)
        {
            results.Add(new Result(Level.Fail, exception.Message));
        }
    }

    /// <summary>
    /// A project on the WSL filesystem reaches wip as a UNC path, and wslc resolves a
    /// bind-mount source as a Windows path — mounting an empty directory rather than
    /// failing when it does not exist. <c>sync.source</c> says so itself by refusing to
    /// resolve, but <c>volumes:</c> entries are passed to wslc verbatim, so nothing else
    /// would report the one thing that explains an empty container.
    /// </summary>
    private static void CheckProjectLocation(Config config, List<Result> results)
    {
        if (config.Path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(config.Path));
        if (directory is null || !WslPath.IsWslPath(directory))
        {
            return;
        }

        results.Add(new Result(Level.Fail,
            $"Project is on the WSL filesystem ({directory}): wslc bind mounts " +
            "(volumes:, sync.source) cannot reach it, and mount empty rather than " +
            "failing. Move the project onto the Windows filesystem, e.g. C:\\src\\myproject"));
    }

    private static void CheckSync(Config config, List<Result> results)
    {
        var sync = config.Sync!;

        // Resolving the source is what refuses a WSL-side path, and doctor is exactly where
        // that has to read as a result rather than as wip falling over.
        string source;
        try
        {
            source = sync.Source;
        }
        catch (ConfigException exception)
        {
            results.Add(new Result(Level.Fail, exception.Message));
            return;
        }

        var found = Directory.Exists(source);
        results.Add(new Result(
            found ? Level.Ok : Level.Fail,
            found
                ? $"Sync source {source} mirrors into volume {sync.Volume} at {sync.Target}"
                : $"Sync source not found: {source}"));

        if (!sync.IsExec || (sync.Image is null && sync.Build is null))
        {
            return;
        }

        results.Add(new Result(Level.Warn,
            "sync.image/sync.build only cover `wip up`’s one-time pre-boot mirror " +
            "(the primary container isn’t running yet, so that step always uses a " +
            "throwaway container) — sync.mode: exec’s `wip sync`/`wip sync --watch` run " +
            "rsync inside the primary container instead, so its image needs rsync too"));
    }

    private static bool CommandAvailable(string name)
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => extensions.Any(extension => File.Exists(Path.Combine(directory, name + extension))));
    }
}
