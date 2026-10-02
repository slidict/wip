using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wip.Configuration;
using static Wip.Execution.ResourceJson;

namespace Wip.Execution;

/// <summary>Durable usage observations, keyed by immutable backend generation name.</summary>
public interface IVolumeUsageStore
{
    bool WasUsed(string backendName);
    void MarkUsed(string backendName);
}

public sealed record VolumeStatus(string Name, string? BackendName, bool Persistent, IReadOnlyList<string> References, bool WasUsed);

/// <summary>Storage only. Mount integration calls reconciliation after attach and confirmed detach.</summary>
public sealed class VolumeLifecycle(SandboxSettings settings, SandboxBackend backend, IVolumeUsageStore usage)
{
    public const string OwnerLabel = "io.slidict.wip.owner";
    public const string PolicyLabel = "io.slidict.wip.persistent";
    public const string InstanceLabel = "io.slidict.wip.instance";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(2);

    public static string BackendPrefix(string resourceNamespace, string name) => "wip-v-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceNamespace}:volume:{name}")))[..24].ToLowerInvariant() + "-";

    private VolumeDefinition Definition(string name) => settings.Volumes.SingleOrDefault(v => v.Name == name)
        ?? throw new ConfigException($"Unknown volume: {name}");
    private string Identity(string name) => $"v1:{settings.ResourceNamespace}:volume:{name}";

    // Each generation has a fresh backend name. A later create never reuses a removed
    // generation's name, unlike a name-only backend remove racing with normal recreation.
    private string? Find(string name)
    {
        var definition = Definition(name);
        var prefix = BackendPrefix(settings.ResourceNamespace!, name);
        var rows = Records(Probe(["volume", "list", "--format", "json"]), allowEmpty: true);
        var matches = rows.Select(r => Text(r, "Name") ?? throw Failure(name, "volume list omitted a name"))
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw Failure(name, "multiple generations exist; do not automatically choose or delete one");
        var candidate = matches[0];
        var inspected = Records(Probe(["volume", "inspect", "--format", "json", candidate]));
        if (inspected.Count != 1) throw Failure(name, "ambiguous volume inspection");
        var record = inspected[0];
        if (Text(record, "Name") != candidate || !record.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object ||
            Text(labels, OwnerLabel) != Identity(name) || Text(labels, PolicyLabel) != (definition.Persistent ? "true" : "false"))
            throw Failure(name, "ownership/persistence mismatch; refusing to adopt or delete");
        var instance = Text(labels, InstanceLabel);
        if (instance is null || !Guid.TryParseExact(instance, "N", out _) || candidate != prefix + instance)
            throw Failure(name, "invalid generation identity");
        return candidate;
    }

    public VolumeStatus Status(string name)
    {
        var definition = Definition(name);
        var candidate = Find(name);
        if (candidate is null) return new(name, null, definition.Persistent, [], false);
        var references = References(candidate);
        // Write only after the entire scan succeeded. Failed/partial scans never imply detach.
        if (references.Count != 0) usage.MarkUsed(candidate);
        return new(name, candidate, definition.Persistent, references, usage.WasUsed(candidate));
    }

    public int Create(string name)
    {
        var definition = Definition(name);
        if (Find(name) is not null) return 0;
        var instance = Guid.NewGuid().ToString("N");
        var candidate = BackendPrefix(settings.ResourceNamespace!, name) + instance;
        var result = backend(["volume", "create", "--label", $"{OwnerLabel}={Identity(name)}", "--label",
            $"{PolicyLabel}={(definition.Persistent ? "true" : "false")}", "--label", $"{InstanceLabel}={instance}", candidate], MutationTimeout, false);
        // Failed create may have left owned storage; preserve it, then recover through status.
        if (result.Code != 0) return result.Code;
        if (Find(name) != candidate) throw Failure(name, "creation outcome changed; retained storage requires investigation");
        return 0;
    }

    public int Destroy(string name)
    {
        var status = Status(name);
        return DestroyGeneration(status);
    }

    private int DestroyGeneration(VolumeStatus status)
    {
        var name = status.Name;
        if (status.BackendName is null) return 0;
        if (status.References.Count != 0) throw Failure(name, "still referenced by containers; remove every referencing container first");
        if (Find(name) != status.BackendName) throw Failure(name, "generation changed before removal; retry status");
        // WSLC also atomically rejects in-use storage. Never force or prune.
        var result = backend(["volume", "remove", status.BackendName], MutationTimeout, false);
        if (result.Code != 0) return result.Code;
        if (Find(name) is not null) throw Failure(name, "storage still exists after removal; no further deletion attempted");
        return 0;
    }

    public int Reconcile(string name)
    {
        var status = Status(name);
        if (status.BackendName is null || status.Persistent || status.References.Count != 0 || !status.WasUsed) return 0;
        return DestroyGeneration(status);
    }

    private IReadOnlyList<string> References(string backendName)
    {
        var references = new List<string>();
        var rows = Records(Probe(["list", "--all", "--format", "json"]), allowEmpty: true);
        foreach (var row in rows)
        {
            if (ContainerState(row) == "deleted") continue;
            var id = Text(row, "Id") ?? Text(row, "ID");
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith('-') || id.Any(char.IsControl)) throw new WipException("Volume reference scan returned invalid container ID");
            var records = Records(Probe(["inspect", "--type", "container", "--format", "json", id]));
            if (records.Count != 1) throw new WipException("Volume reference scan returned ambiguous container");
            var container = records[0];
            var fullId = Text(container, "Id") ?? Text(container, "ID");
            if (fullId != id && !(id.Length >= 12 && id.All(Uri.IsHexDigit) && fullId is { Length: 64 } &&
                fullId.All(Uri.IsHexDigit) && fullId.StartsWith(id, StringComparison.Ordinal))) throw new WipException("Container identity changed during volume reference scan");
            if (ContainerState(container) == "deleted") continue;
            if (!container.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array)
                throw new WipException("Volume reference scan omitted mounts; detach is unknown");
            foreach (var mount in mounts.EnumerateArray())
            {
                if (mount.ValueKind != JsonValueKind.Object || Text(mount, "Type") is not string type)
                    throw new WipException("Volume reference scan returned malformed mount");
                if (type != "volume") continue;
                var volumeName = Text(mount, "Name") ?? throw new WipException("Volume reference scan omitted volume name");
                if (volumeName == backendName) references.Add(fullId!);
            }
        }
        return references.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string ContainerState(JsonElement record)
    {
        if (!record.TryGetProperty("State", out var state)) throw new WipException("Volume reference scan omitted state; detach is unknown");
        if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Status", out var status)) state = status;
        var value = state.ValueKind == JsonValueKind.String ? state.GetString() :
            state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var number) ? number switch
            { 1 => "created", 2 => "running", 3 => "exited", 4 => "deleted", _ => null } : null;
        if (value is not ("created" or "running" or "exited" or "deleted")) throw new WipException("Unknown container state; volume detach is unknown");
        return value;
    }

    private string Probe(IReadOnlyList<string> arguments)
    {
        var result = backend(arguments, ProbeTimeout, true);
        if (result.Code != 0) throw new WipException($"Volume probe failed (exit {result.Code}); state is unknown, storage retained");
        return result.Output;
    }
    private static WipException Failure(string name, string reason) => new($"Volume {name}: {reason}");
}
