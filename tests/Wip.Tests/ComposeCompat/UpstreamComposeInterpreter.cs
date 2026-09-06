using System.Text.Json.Nodes;
using Wip.Yaml;

namespace Wip.Tests.ComposeCompat;

/// <summary>
/// What Microsoft's <c>wslc compose</c> makes of the same fixture, at the upstream commit
/// pinned in tests/compose-compat/upstream/PINNED.md.
/// </summary>
/// <remarks>
/// <para>
/// A reading of upstream's code, kept here so the comparison has something to compare
/// against: nothing under src/ references it, and no behaviour of wip is derived from it.
/// Each rule below cites the upstream function it mirrors, so re-reading a changed
/// <c>feature/compose</c> is a matter of walking the same list.
/// </para>
/// <para>
/// Upstream's own YAML reader is yaml-cpp; this uses wip's loader, because the fixtures stay
/// inside the subset the two agree on. The YAML layer is not part of what is being compared —
/// the interpretation on top of it is.
/// </para>
/// </remarks>
internal static class UpstreamComposeInterpreter
{
    /// <summary>ComposeSpec.cpp ParseComposeFile: every other key is a hard error.</summary>
    private static readonly string[] SupportedKeys =
        ["name", "container_name", "image", "environment", "working_dir", "command", "volumes", "ports"];

    private sealed class InvalidComposeFile(string message) : Exception(message);

    internal static ComposeSemanticModel Interpret(string fixture)
    {
        try
        {
            return ComposeSemanticModel.Ok(Parse(fixture));
        }
        catch (InvalidComposeFile exception)
        {
            return ComposeSemanticModel.Failed(ComposeCompatCorpus.Anonymize(exception.Message, fixture));
        }
        catch (WipException exception)
        {
            return ComposeSemanticModel.Failed(
                ComposeCompatCorpus.Anonymize($"YAML could not be read: {exception.Message}", fixture));
        }
    }

    private static JsonObject Parse(string fixture)
    {
        var path = ComposeCompatCorpus.ComposePath(fixture);
        var directory = ComposeCompatCorpus.DirectoryOf(fixture).Replace('\\', '/');
        var document = RubyValue.AsMapping(YamlLoader.LoadFile(path, allowAliases: true));
        var services = RubyValue.AsMapping(document?.GetValueOrDefault("services"));
        if (services is null || services.Count == 0)
        {
            throw new InvalidComposeFile("the file must contain a non-empty services map");
        }

        // ProjectName = Path.stem(). Upstream marks this "TODO: Implement this properly", so
        // it is recorded as an observation, never adopted: Compose derives the project name
        // from the directory, not from the file name.
        var project = Path.GetFileNameWithoutExtension(path);

        var result = new JsonObject();
        foreach (var (serviceName, entry) in services)
        {
            result[serviceName] = Service(serviceName, entry, directory);
        }

        return new JsonObject
        {
            ["project"] = project,
            ["network"] = $"{project}_default",
            // CreateComposeContainers -> AddPort(..., AF_INET), whose default binding address
            // is the loopback literal rather than the session setting the CLI path consults.
            ["port_binding_default"] = "127.0.0.1",
            ["start_order"] = new JsonArray(services.Keys.Select(name => (JsonNode?)name).ToArray()),
            ["services"] = result,
        };
    }

    private static JsonObject Service(string serviceName, object? entry, string directory)
    {
        var settings = RubyValue.AsMapping(entry) ?? throw new InvalidComposeFile("each service must be a map");

        foreach (var key in settings.Keys)
        {
            if (!SupportedKeys.Contains(key))
            {
                throw new InvalidComposeFile($"the '{key}' property is not supported");
            }
        }

        var image = RubyValue.Presence(settings.GetValueOrDefault("image"));
        if (settings.GetValueOrDefault("image") is null)
        {
            throw new InvalidComposeFile($"the '{serviceName}' service must specify an image");
        }

        if (image is null)
        {
            throw new InvalidComposeFile($"the '{serviceName}' service has an empty image");
        }

        // 'name' is an upstream extension -- the Compose Specification has no service-level
        // 'name' key -- and it wins over the spec's own container_name when both are present.
        var nameNode = settings.GetValueOrDefault("name") ?? settings.GetValueOrDefault("container_name");
        var name = nameNode is null ? serviceName : RubyValue.ToStringValue(nameNode);
        if (name.Length == 0)
        {
            throw new InvalidComposeFile($"the '{serviceName}' service has an empty name");
        }

        return new JsonObject
        {
            ["container_name"] = name,
            ["image"] = image,
            ["command_argv"] = Command(serviceName, settings.GetValueOrDefault("command")),
            ["environment"] = Environment(serviceName, settings.GetValueOrDefault("environment")),
            ["working_dir"] = WorkingDirectory(serviceName, settings.GetValueOrDefault("working_dir")),
            ["mounts"] = Volumes(serviceName, settings.GetValueOrDefault("volumes"), directory),
            ["ports"] = Ports(serviceName, settings.GetValueOrDefault("ports")),
            // CreateComposeContainers: AddPrimaryNetworkAlias(definition.Name) -- the
            // *container* name, so container_name silently renames the alias too. Compose
            // aliases the service name and leaves it alone when container_name is set.
            ["network_aliases"] = new JsonArray(name),
        };
    }

    /// <summary>
    /// ParseComposeFile: a scalar command is split on single spaces, marked
    /// "TODO: Implement proper parsing" upstream — quotes survive into the argv as literal
    /// characters and runs of spaces collapse. A sequence is taken as written.
    /// </summary>
    private static JsonArray Command(string serviceName, object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (RubyValue.AsSequence(value) is { } sequence)
        {
            return new JsonArray(sequence.Select(item => (JsonNode?)Scalar(serviceName, item, "command")).ToArray());
        }

        if (value is OrderedDictionary<string, object?>)
        {
            throw new InvalidComposeFile(
                $"the 'command' property for service '{serviceName}' must be a string or list");
        }

        return new JsonArray(RubyValue.ToStringValue(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => (JsonNode?)word)
            .ToArray());
    }

    /// <summary>
    /// ParseComposeEnvironment: a mapping becomes KEY=VALUE, and a null value becomes an
    /// empty string rather than the Compose Specification's "take this one from the host".
    /// A list is passed through as written, so a bare KEY stays a bare KEY with no '='.
    /// </summary>
    private static JsonArray Environment(string serviceName, object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (RubyValue.AsSequence(value) is { } sequence)
        {
            return new JsonArray(sequence.Select(item => (JsonNode?)Scalar(serviceName, item, "environment")).ToArray());
        }

        if (RubyValue.AsMapping(value) is not { } mapping)
        {
            throw new InvalidComposeFile(
                $"the 'environment' property for service '{serviceName}' must be a map or list");
        }

        var result = new JsonArray();
        foreach (var (key, item) in mapping)
        {
            if (item is OrderedDictionary<string, object?> or List<object?>)
            {
                throw new InvalidComposeFile(
                    $"the 'environment' property for service '{serviceName}' must contain scalar values");
            }

            result.Add($"{key}={(item is null ? string.Empty : RubyValue.ToStringValue(item))}");
        }

        return result;
    }

    private static string? WorkingDirectory(string serviceName, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is OrderedDictionary<string, object?> or List<object?>)
        {
            throw new InvalidComposeFile(
                $"the 'working_dir' property for service '{serviceName}' must be a string");
        }

        var text = RubyValue.ToStringValue(value);
        if (!text.StartsWith('/'))
        {
            throw new InvalidComposeFile(
                $"the working directory for service '{serviceName}' must be an absolute Linux path");
        }

        return text;
    }

    /// <summary>
    /// ParseComposeVolume: short syntax only. The named-volume test is structural (does the
    /// source look like a path?) rather than Docker's name pattern, and a relative bind source
    /// is resolved against compose.yml's own directory.
    /// </summary>
    private static JsonArray Volumes(string serviceName, object? value, string directory)
    {
        if (value is null)
        {
            return [];
        }

        if (RubyValue.AsSequence(value) is not { } sequence)
        {
            throw new InvalidComposeFile($"the 'volumes' property for service '{serviceName}' must be a list");
        }

        var result = new JsonArray();
        foreach (var item in sequence)
        {
            if (item is OrderedDictionary<string, object?> or List<object?>)
            {
                throw new InvalidComposeFile(
                    $"the 'volumes' property for service '{serviceName}' must contain only short-syntax strings");
            }

            result.Add(Volume(serviceName, RubyValue.ToStringValue(item), directory));
        }

        return result;
    }

    private static JsonObject Volume(string serviceName, string raw, string directory)
    {
        var value = raw;
        var readOnly = false;
        if (value.EndsWith(":ro", StringComparison.Ordinal) || value.EndsWith(":rw", StringComparison.Ordinal))
        {
            readOnly = value.EndsWith(":ro", StringComparison.Ordinal);
            value = value[..^3];
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator + 1 == value.Length)
        {
            throw new InvalidComposeFile(
                $"the '{value}' volume for service '{serviceName}' must use source:destination[:ro|rw] syntax");
        }

        var source = value[..separator];
        var destination = value[(separator + 1)..];
        if (!destination.StartsWith('/'))
        {
            throw new InvalidComposeFile(
                $"the '{destination}' volume destination for service '{serviceName}' must be an absolute Linux path");
        }

        var bind = HostPathBase.IsAbsolute(source) ||
                   source.StartsWith('.') ||
                   source.Contains('/', StringComparison.Ordinal) ||
                   source.Contains('\\', StringComparison.Ordinal);

        if (!bind)
        {
            return new JsonObject
            {
                ["type"] = "volume",
                ["source"] = source,
                ["source_base"] = null,
                ["target"] = destination,
                ["read_only"] = readOnly,
            };
        }

        return new JsonObject
        {
            ["type"] = "bind",
            ["source"] = HostPathBase.Resolve(ComposeCompatCorpus.FixturePlaceholder, source),
            ["source_base"] = HostPathBase.AnchorOf(source) switch
            {
                HostPathBase.Anchor.Absolute => "absolute",
                HostPathBase.Anchor.RootRelative => "compose-file-drive",
                _ => "compose-file-dir",
            },
            ["target"] = destination,
            ["read_only"] = readOnly,
        };
    }

    /// <summary>
    /// ParseComposePort: host:container only. Exactly one colon, both sides plain uint16
    /// digits, container port non-zero — no ranges, no protocol suffix, no bind address, and
    /// no container-port-only form.
    /// </summary>
    private static JsonArray Ports(string serviceName, object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (RubyValue.AsSequence(value) is not { } sequence)
        {
            throw new InvalidComposeFile($"the 'ports' property for service '{serviceName}' must be a list");
        }

        var result = new JsonArray();
        foreach (var item in sequence)
        {
            if (item is OrderedDictionary<string, object?> or List<object?>)
            {
                throw new InvalidComposeFile(
                    $"the 'ports' property for service '{serviceName}' must contain only host:container strings");
            }

            result.Add(Port(serviceName, RubyValue.ToStringValue(item)));
        }

        return result;
    }

    private static JsonObject Port(string serviceName, string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator + 1 == value.Length || separator != value.LastIndexOf(':'))
        {
            throw new InvalidComposeFile(
                $"the '{value}' port for service '{serviceName}' must use host:container syntax");
        }

        // The host side alone may be 0, which the runtime reads as "pick an ephemeral port".
        var host = ParsePort(serviceName, value, value[..separator], allowZero: true);
        var container = ParsePort(serviceName, value, value[(separator + 1)..], allowZero: false);

        return new JsonObject
        {
            ["host_ip"] = null,
            ["host_port"] = host == 0 ? "ephemeral" : host.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["container_port"] = container.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["protocol"] = "tcp",
        };
    }

    private static int ParsePort(string serviceName, string original, string text, bool allowZero)
    {
        if (text.Length == 0 || !text.All(char.IsAsciiDigit) ||
            !int.TryParse(text, out var port) || port > 65535 || (!allowZero && port == 0))
        {
            throw new InvalidComposeFile(
                $"the '{original}' port for service '{serviceName}' contains an invalid port number");
        }

        return port;
    }

    private static string Scalar(string serviceName, object? item, string property)
    {
        if (item is OrderedDictionary<string, object?> or List<object?>)
        {
            throw new InvalidComposeFile(
                $"the '{property}' property for service '{serviceName}' must contain only strings");
        }

        return RubyValue.ToStringValue(item);
    }
}
