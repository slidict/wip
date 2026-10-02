using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wip.Configuration;

namespace Wip.Execution;

public sealed record SandboxCommandResult(int Code, string Output);
public sealed record SandboxStatus(string Name, string BackendName, string? Id, string State);

/// <summary>Backend argv are passed directly, never through a shell. Capture is only needed for probes.</summary>
public delegate SandboxCommandResult SandboxBackend(IReadOnlyList<string> arguments, TimeSpan timeout, bool capture);

public sealed class SandboxLifecycle(SandboxSettings settings, SandboxBackend backend)
{
    public const string OwnerLabel = "io.slidict.wip.owner";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(2);

    public static string BackendName(string resourceNamespace, string name) =>
        "wip-s-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceNamespace}:sandbox:{name}")))[..40].ToLowerInvariant();

    private SandboxDefinition Definition(string name) => settings.Sandboxes.SingleOrDefault(s => s.Name == name)
        ?? throw new ConfigException($"Unknown sandbox: {name}");

    private string Identity(string name) => $"v1:{settings.ResourceNamespace}:sandbox:{name}";

    public SandboxStatus Status(string name)
    {
        Definition(name);
        var backendName = BackendName(settings.ResourceNamespace!, name);
        var listed = Probe(["list", "--all", "--filter", $"name={backendName}", "--format", "json"]);
        var matches = Records(listed, allowEmpty: true).Where(r => Names(r).Contains(backendName, StringComparer.Ordinal)).ToArray();
        if (matches.Length == 0) return new(name, backendName, null, "not found");
        if (matches.Length != 1) throw Failure(name, "ambiguous backend identity");
        // Older WSLC retains deleted tombstones in list --all. No live container exists
        // to inspect or remove; treating it as absent performs no destructive operation.
        if (matches[0].TryGetProperty("State", out _) && State(matches[0]) == "deleted")
            return new(name, backendName, null, "not found");
        var id = Text(matches[0], "Id") ?? Text(matches[0], "ID");
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith('-') || id.Any(char.IsControl))
            throw Failure(name, "backend returned an invalid ID");
        var inspected = Records(Probe(["inspect", "--type", "container", "--format", "json", id]));
        if (inspected.Count != 1) throw Failure(name, "ambiguous ownership metadata");
        var record = inspected[0];
        var inspectedId = Text(record, "Id") ?? Text(record, "ID");
        var matchesId = inspectedId == id || (id.Length >= 12 && id.All(Uri.IsHexDigit) &&
            inspectedId is { Length: 64 } && inspectedId.All(Uri.IsHexDigit) && inspectedId.StartsWith(id, StringComparison.Ordinal));
        if (!matchesId || !Names(record).Contains(backendName, StringComparer.Ordinal))
            throw Failure(name, "identity changed during inspection; retry status");
        JsonElement labels;
        if ((!record.TryGetProperty("Labels", out labels) || labels.ValueKind != JsonValueKind.Object) &&
            !(record.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out labels)))
            throw Failure(name, "missing ownership metadata; refusing to adopt this container");
        if (labels.ValueKind != JsonValueKind.Object || Text(labels, OwnerLabel) != Identity(name))
            throw Failure(name, "ownership mismatch; refusing to adopt this container");
        var state = State(record);
        if (state == "deleted") return new(name, backendName, null, "not found");
        return new(name, backendName, inspectedId, state);
    }

    public int Create(string name)
    {
        var definition = Definition(name);
        if (definition.Volumes.Count != 0)
            throw new ConfigException($"Sandbox {name}: volume mounts are not implemented yet; no container was created");
        if (definition.Image.StartsWith('-')) throw new ConfigException("Sandbox image must not start with '-'");
        var existing = Status(name);
        if (existing.Id is not null)
        {
            if (existing.State == "running") return 0;
            if (existing.State is not ("created" or "exited"))
                throw Failure(name, $"cannot start state {existing.State}; run sandbox destroy, confirm absence, then sandbox create");
            var started = backend(["start", existing.Id], MutationTimeout, false);
            if (started.Code != 0) return started.Code;
        }
        else
        {
            var created = backend(["run", "--name", existing.BackendName, "-d", "--label", $"{OwnerLabel}={Identity(name)}", definition.Image], MutationTimeout, false);
            // Even a failed/timed-out run can leave a container. Keep it for ownership-checked recovery.
            if (created.Code != 0) return created.Code;
        }
        if (Status(name).State != "running") throw Failure(name, "creation/start did not produce a running sandbox; inspect status and image CMD, then retry or destroy");
        return 0;
    }

    public int Destroy(string name)
    {
        var existing = Status(name);
        if (existing.Id is null) return 0;
        // Deliberately no volume removal flag or volume command.
        var removed = backend(["remove", "-f", existing.Id], MutationTimeout, false);
        if (removed.Code != 0) return removed.Code;
        if (Status(name).Id is not null) throw Failure(name, "container still exists after removal; retry status before recovery");
        return 0;
    }

    public int Exec(string name, IReadOnlyList<string> argv, TimeSpan timeout)
    {
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0]) || argv[0].StartsWith('-'))
            throw new ConfigException("sandbox exec requires an executable (use -- before its argv)");
        if (timeout <= TimeSpan.Zero) throw new ConfigException("sandbox exec timeout must be positive");
        var existing = Status(name);
        if (existing.Id is null || existing.State != "running") throw Failure(name, "sandbox is not running; run sandbox create first");
        return backend(["exec", existing.Id, .. argv], timeout, false).Code;
    }

    private string Probe(IReadOnlyList<string> argv)
    {
        var result = backend(argv, ProbeTimeout, true);
        if (result.Code != 0) throw new WipException($"Sandbox probe failed (exit {result.Code}); existence/ownership is unknown. Retry status; no destructive recovery was attempted.");
        return result.Output;
    }

    private static WipException Failure(string name, string reason) => new($"Sandbox {name}: {reason}");

    private static string? Text(JsonElement record, string key) =>
        record.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Names(JsonElement record)
    {
        if (Text(record, "Name") is string name) return [name.TrimStart('/')];
        if (!record.TryGetProperty("Names", out var names)) throw new WipException("Sandbox backend returned a record without a name");
        if (names.ValueKind == JsonValueKind.String) return names.GetString()!.Split(',').Select(n => n.Trim().TrimStart('/'));
        if (names.ValueKind == JsonValueKind.Array && names.EnumerateArray().All(n => n.ValueKind == JsonValueKind.String))
            return names.EnumerateArray().Select(n => n.GetString()!.TrimStart('/')).ToArray();
        throw new WipException("Sandbox backend returned invalid names");
    }

    private static string State(JsonElement record)
    {
        if (!record.TryGetProperty("State", out var state)) throw new WipException("Sandbox backend omitted state");
        if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Status", out var status)) state = status;
        if (state.ValueKind == JsonValueKind.String) return state.GetString()!.ToLowerInvariant();
        if (state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var number))
            return number switch { 1 => "created", 2 => "running", 3 => "exited", 4 => "deleted", _ => "unknown" };
        throw new WipException("Sandbox backend returned invalid state");
    }

    private static IReadOnlyList<JsonElement> Records(string output, bool allowEmpty = false)
    {
        // Successful Docker-format list uses no output for zero rows. Inspect must return a record.
        if (allowEmpty && string.IsNullOrWhiteSpace(output)) return [];
        if (string.IsNullOrWhiteSpace(output)) throw new WipException("Sandbox backend returned empty JSON; existence is unknown");
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object) return [root.Clone()];
            if (root.ValueKind == JsonValueKind.Array && root.EnumerateArray().All(r => r.ValueKind == JsonValueKind.Object))
                return root.EnumerateArray().Select(r => r.Clone()).ToArray();
        }
        catch (JsonException)
        {
            // WSLC's Docker-compatible format emits one object per line for list.
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 1)
            {
                var records = new List<JsonElement>();
                try
                {
                    foreach (var line in lines)
                    {
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                        records.Add(document.RootElement.Clone());
                    }
                    return records;
                }
                catch (JsonException) { }
            }
        }
        throw new WipException("Sandbox backend returned malformed JSON; existence is unknown");
    }
}
