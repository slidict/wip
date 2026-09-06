using System.Text.Json.Nodes;

namespace Wip.Tests.ComposeCompat;

/// <summary>
/// One implementation's answer to "what does this compose.yml actually mean?", reduced to the
/// values a container runtime is handed: names, image, argv, environment, working directory,
/// mounts, published ports, network and aliases.
/// </summary>
/// <remarks>
/// Deliberately not a mirror of either side's internal types. Both interpreters project onto
/// this one shape so a difference between them is a difference in meaning, not in how the two
/// code bases happen to spell a struct.
/// </remarks>
internal sealed record ComposeSemanticModel(JsonObject? Model, string? Error)
{
    /// <summary>Every axis the compatibility matrix is allowed to track.</summary>
    internal static readonly string[] Features =
    [
        "image", "command", "environment", "working_dir", "volumes", "ports", "port_binding_default",
        "container_name", "service_name", "network", "service_alias",
        "build", "depends_on", "healthcheck", "restart", "profiles", "interpolation",
    ];

    /// <summary>Recorded where a model has no notion of a feature at all, as upstream has none of the WIP-only ones.</summary>
    private const string Absent = "<not-modelled>";

    internal static ComposeSemanticModel Failed(string message) => new(null, message);

    internal static ComposeSemanticModel Ok(JsonObject model) => new(model, null);

    internal bool IsOk => Error is null;

    internal JsonObject ToJson()
    {
        var result = new JsonObject();
        if (Error is not null)
        {
            result["status"] = "error";
            result["message"] = Error;
            return result;
        }

        result["status"] = "ok";
        result["model"] = Model!.DeepClone();
        return result;
    }

    /// <summary>
    /// The slice of the model one feature is responsible for. Comparing projections rather
    /// than whole models is what lets a fixture be compatible on <c>image</c> and different on
    /// <c>network</c> at the same time.
    /// </summary>
    internal JsonNode Projection(string feature)
    {
        if (Error is not null)
        {
            return new JsonObject { ["error"] = Error };
        }

        return feature switch
        {
            "service_name" => new JsonArray(Model!["services"]!.AsObject().Select(pair => (JsonNode?)pair.Key).ToArray()),
            "network" => new JsonObject
            {
                ["project"] = Model!["project"]?.DeepClone(),
                ["network"] = Model["network"]?.DeepClone(),
            },
            "port_binding_default" => Model!["port_binding_default"]?.DeepClone() ?? Absent,
            "profiles" => Model!["services_excluded_by_profiles"]?.DeepClone() ?? Absent,
            "depends_on" => Model!["start_order"]?.DeepClone() ?? Absent,
            "interpolation" => Model!.DeepClone(),
            _ => PerService(feature),
        };
    }

    private JsonNode PerService(string feature)
    {
        var key = feature switch
        {
            "image" => "image",
            "command" => "command_argv",
            "environment" => "environment",
            "working_dir" => "working_dir",
            "volumes" => "mounts",
            "ports" => "ports",
            "container_name" => "container_name",
            "service_alias" => "network_aliases",
            "build" => "build",
            "healthcheck" => "healthcheck",
            "restart" => "restart",
            _ => throw new InvalidOperationException($"Unknown compatibility feature: {feature}"),
        };

        var result = new JsonObject();
        foreach (var (service, entry) in Model!["services"]!.AsObject())
        {
            result[service] = entry!.AsObject().TryGetPropertyValue(key, out var value)
                ? value?.DeepClone()
                : (JsonNode?)Absent;
        }

        return result;
    }
}
