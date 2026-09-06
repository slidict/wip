using System.Text.Json.Nodes;
using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;
using Wip.Yaml;

namespace Wip.Tests.ComposeCompat;

/// <summary>
/// What wip's own <c>mode: compose-native</c> makes of a fixture, read back out of the
/// argument arrays it would hand wslc rather than out of its internal types — an argv is the
/// last thing wip decides, so nothing can drift between what this records and what runs.
/// </summary>
internal static class WipComposeInterpreter
{
    private const string Wslc = "wslc.exe";

    internal static ComposeSemanticModel Interpret(string fixture)
    {
        var directory = ComposeCompatCorpus.DirectoryOf(fixture);
        try
        {
            return ComposeSemanticModel.Ok(Build(fixture, directory));
        }
        catch (WipException exception)
        {
            return ComposeSemanticModel.Failed(ComposeCompatCorpus.Anonymize(exception.Message, fixture));
        }
        catch (WslcArgumentSemantics.SpecException exception)
        {
            // A spec wip forwards happily that wslc itself would reject still belongs in the
            // record: the service never starts either way.
            return ComposeSemanticModel.Failed(
                ComposeCompatCorpus.Anonymize($"wslc rejects a forwarded argument: {exception.Message}", fixture));
        }
    }

    /// <summary>
    /// The argv wip runs for each service, kept beside the model so a reader can check the
    /// interpretation against the command line rather than taking it on trust.
    /// </summary>
    internal static JsonObject Invocation(string fixture)
    {
        var (config, builder) = Load(ComposeCompatCorpus.DirectoryOf(fixture));
        var result = new JsonObject();
        try
        {
            result["network_create"] = config.Network is null
                ? null
                : new JsonArray(builder.NetworkCreate().Select(argument => (JsonNode?)argument).ToArray());

            var services = new JsonObject();
            foreach (var name in config.Dependencies.Keys)
            {
                services[name] = new JsonArray(
                    builder.DependencyUp(name).Select(argument => (JsonNode?)argument).ToArray());
            }

            result["services"] = services;
        }
        catch (WipException exception)
        {
            result["error"] = ComposeCompatCorpus.Anonymize(exception.Message, fixture);
        }

        return result;
    }

    private static (Config Config, CommandBuilder Builder) Load(string directory)
    {
        var envFile = Path.Combine(directory, ".env");
        var dotenv = new DotenvLoader(envFile).Load();
        var config = new ConfigLoader(
            path: Path.Combine(directory, "wip.yml"),
            envFile: File.Exists(envFile) ? envFile : null).Load();

        return (config, new CommandBuilder(Wslc, config, new CompatEnvironment(), dotenv));
    }

    private static JsonObject Build(string fixture, string directory)
    {
        var (config, builder) = Load(directory);

        var services = new JsonObject();
        foreach (var name in config.Dependencies.Keys)
        {
            services[name] = Service(fixture, config, builder, name);
        }

        return new JsonObject
        {
            // compose.project, else the wip.yml directory's name -- and the network is that
            // name unchanged, with no _default suffix.
            ["project"] = config.ComposeProject ?? new DirectoryInfo(directory).Name,
            ["network"] = config.Network,
            ["port_binding_default"] = WslcArgumentSemantics.SessionDefaultBindingAddress,
            ["start_order"] = new JsonArray(config.Dependencies.Keys.Select(key => (JsonNode?)key).ToArray()),
            ["services_excluded_by_profiles"] = ProfiledServices(config, directory),
            ["services"] = services,
        };
    }

    private static JsonObject Service(string fixture, Config config, CommandBuilder builder, string name)
    {
        var values = config.Dependency(name)!;
        var argv = builder.DependencyUp(name);
        var parsed = ParseRunArgv(argv);

        var mounts = new JsonArray();
        foreach (var volume in parsed.Volumes)
        {
            var mount = WslcArgumentSemantics.ParseVolume(volume);
            mount["source"] = ComposeCompatCorpus.Anonymize(
                mount["source"]!.GetValue<string>().Replace('\\', '/'), fixture);
            mounts.Add(mount);
        }

        var ports = new JsonArray();
        foreach (var port in parsed.Ports)
        {
            ports.Add(WslcArgumentSemantics.ParsePort(port));
        }

        return new JsonObject
        {
            // wip has no per-service container_name: the dependencies key -- which under
            // compose-native is the compose service name -- is the container name.
            ["container_name"] = name,
            ["image"] = parsed.Image,
            ["command_argv"] = new JsonArray(parsed.Command.Select(word => (JsonNode?)word).ToArray()),
            ["environment"] = new JsonArray(parsed.Environment.Select(entry => (JsonNode?)entry).ToArray()),
            ["working_dir"] = parsed.WorkingDirectory,
            ["mounts"] = mounts,
            ["ports"] = ports,
            // wip passes no --network-alias: the container's own --name is what the
            // user-defined network resolves, which is the compose service name.
            ["network_aliases"] = config.Network is null
                ? new JsonArray()
                : new JsonArray(name),
            ["build"] = BuildSpec(fixture, config, name),
            ["healthcheck"] = RubyJson.ToJson(values.GetValueOrDefault("healthcheck")),
            ["restart"] = RubyValue.ToStringValue(values.GetValueOrDefault("restart")),
        };
    }

    private static JsonNode? BuildSpec(string fixture, Config config, string name)
    {
        if (config.ComposeBuildSpecs.GetValueOrDefault(name) is not OrderedDictionary<string, object?> spec)
        {
            return null;
        }

        var result = RubyJson.ToJson(spec)!.AsObject();
        result["context"] = ComposeCompatCorpus.Anonymize(
            RubyValue.ToStringValue(spec["context"]).Replace('\\', '/'), fixture);
        return result;
    }

    /// <summary>
    /// compose.yml's services minus the ones wip is willing to start. Under compose-native
    /// that difference is exactly the profile-gated ones: wip has no <c>--profile</c> flag.
    /// </summary>
    private static JsonArray ProfiledServices(Config config, string directory)
    {
        var document = RubyValue.AsMapping(YamlLoader.LoadFile(Path.Combine(directory, "compose.yml"), allowAliases: true));
        IEnumerable<string> declared =
            RubyValue.AsMapping(document?.GetValueOrDefault("services"))?.Keys ?? Enumerable.Empty<string>();
        var startable = config.Dependencies.Keys.ToHashSet(StringComparer.Ordinal);
        return new JsonArray(declared.Where(name => !startable.Contains(name)).Select(name => (JsonNode?)name).ToArray());
    }

    /// <summary>
    /// Reads back <c>wslc run --name N [--network X] -d [-w] [-u] [-e]* [-p]* [-v]* IMAGE ARGV...</c>.
    /// The first token that is not a flag or a flag's value is the image; everything after it
    /// is the container's argv.
    /// </summary>
    private static RunArgv ParseRunArgv(IReadOnlyList<string> argv)
    {
        var environment = new List<string>();
        var ports = new List<string>();
        var volumes = new List<string>();
        string? workingDirectory = null;
        string? user = null;

        var index = 2;
        while (index < argv.Count)
        {
            var token = argv[index];
            switch (token)
            {
                case "--name" or "--network":
                    index += 2;
                    continue;
                case "-d" or "-it" or "--rm":
                    index += 1;
                    continue;
                case "-w":
                    workingDirectory = argv[index + 1];
                    index += 2;
                    continue;
                case "-u":
                    user = argv[index + 1];
                    index += 2;
                    continue;
                case "-e":
                    environment.Add(argv[index + 1]);
                    index += 2;
                    continue;
                case "-p":
                    ports.Add(argv[index + 1]);
                    index += 2;
                    continue;
                case "-v":
                    volumes.Add(argv[index + 1]);
                    index += 2;
                    continue;
                default:
                    return new RunArgv(
                        token, argv.Skip(index + 1).ToList(), environment, ports, volumes, workingDirectory, user);
            }
        }

        throw new InvalidOperationException($"No image found in: {string.Join(' ', argv)}");
    }

    private sealed record RunArgv(
        string Image,
        IReadOnlyList<string> Command,
        IReadOnlyList<string> Environment,
        IReadOnlyList<string> Ports,
        IReadOnlyList<string> Volumes,
        string? WorkingDirectory,
        string? User);

    /// <summary>Non-interactive and fixed, so a recording does not depend on how the suite was launched.</summary>
    private sealed class CompatEnvironment : IEnvironment
    {
        public bool IsInteractive => false;

        public bool IsWsl2 => true;

        public string Architecture => "linux/amd64";
    }
}
