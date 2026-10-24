using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wip.Configuration;
using static Wip.Execution.ResourceJson;

namespace Wip.Execution;

public sealed record NetworkStatus(string Name, string BackendName, bool Exists, bool Internal);

public interface INetworkLifecycle
{
    NetworkStatus Status(string name);
    int Create(string name);
    int Destroy(string name);

    /// <summary>
    /// The backend name of an existing network that has been confirmed owned and internal.
    /// Throws rather than returning anything a caller could treat as "good enough": the
    /// return value is the only thing that attaches a sandbox to a boundary.
    /// </summary>
    string VerifiedBackendName(string name);
}

/// <summary>
/// The outer, container-immutable network boundary a security scan runs behind. WSLC's
/// <c>--internal</c> is the whole mechanism: it denies egress at the network, not inside the
/// container, so nothing the scanned app or the scanner does can widen it.
/// </summary>
/// <remarks>
/// Deliberately not generation-named the way <see cref="VolumeLifecycle"/> is. A network
/// holds no data, so a removed one has nothing a later create could inherit, and a stable
/// name is what lets <see cref="Status"/> be a pure lookup after an abnormal exit.
/// </remarks>
public sealed class NetworkLifecycle(SandboxSettings settings, SandboxBackend backend) : INetworkLifecycle
{
    public const string OwnerLabel = "io.slidict.wip.owner";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(2);

    public static string BackendName(string resourceNamespace, string name) =>
        "wip-n-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceNamespace}:network:{name}")))[..40].ToLowerInvariant();

    private NetworkDefinition Definition(string name) => settings.Networks.SingleOrDefault(n => n.Name == name)
        ?? throw new ConfigException($"Unknown network: {name}");

    private string Identity(string name) => $"v1:{settings.ResourceNamespace}:network:{name}";

    public NetworkStatus Status(string name)
    {
        Definition(name);
        var backendName = BackendName(settings.ResourceNamespace!, name);
        var listed = Records(Probe(["network", "list", "--format", "json"]), allowEmpty: true);
        var matches = listed.Where(r => Text(r, "Name") == backendName).ToArray();
        if (matches.Length == 0) return new(name, backendName, false, false);
        if (matches.Length != 1) throw Failure(name, "ambiguous network identity");
        // `network list` renders Internal as a string and Labels as one flat "k=v,k=v" field.
        // Both are re-read from `network inspect`, which types them properly; the listing is
        // only ever used to decide whether a network exists at all.
        return new(name, backendName, true, Inspect(name, backendName) is not null);
    }

    public int Create(string name)
    {
        var definition = Definition(name);
        // There is no non-internal path on purpose: a sandbox cannot reference a network that
        // is not internal (SandboxSettings rejects it), so reaching here with one means the
        // caller bypassed the declaration rather than that a looser mode was requested.
        if (!definition.Internal) throw Failure(name, "only internal networks can be created; declare networks." + name + ".internal: true");
        var backendName = BackendName(settings.ResourceNamespace!, name);
        var existing = Status(name);
        if (existing.Exists)
        {
            // Not removed and recreated: a network someone else owns, or one whose boundary
            // is not what was declared, is a condition to report, never one to repair.
            if (!existing.Internal) throw Failure(name, "an existing network with this name is not owned by wip or not internal; refusing to adopt, recreate or delete it");
            return 0;
        }
        var created = backend(
            ["network", "create", "--internal", "--label", $"{OwnerLabel}={Identity(name)}", backendName],
            MutationTimeout,
            SandboxConsoleMode.Stream);
        // A failed create can still have left a network behind. It is kept for an
        // ownership-checked look, exactly as a failed volume create is.
        if (created.Code != 0) return created.Code;
        var confirmed = Status(name);
        if (!confirmed.Exists || !confirmed.Internal)
            throw Failure(name, "creation did not produce an owned internal network; no container was started");
        return 0;
    }

    public int Destroy(string name)
    {
        Definition(name);
        var backendName = BackendName(settings.ResourceNamespace!, name);
        var status = Status(name);
        if (!status.Exists) return 0;
        // Removing a network wip does not own, or one whose boundary does not match the
        // declaration, would be destroying someone else's resource on a name collision.
        if (!status.Internal) throw Failure(name, "an existing network with this name is not owned by wip or not internal; refusing to delete it");
        // Several sandboxes can share one declared network -- a scanner and its target do --
        // so the first of them being destroyed must leave the boundary standing for the rest.
        // Removal is what the last one does, which is why this is a no-op and not an error.
        if (Attachments(name, backendName) != 0) return 0;
        // WSLC rejects a network that still has attachments. Never force or prune.
        var removed = backend(["network", "remove", status.BackendName], MutationTimeout, SandboxConsoleMode.Stream);
        if (removed.Code != 0) return removed.Code;
        if (Status(name).Exists) throw Failure(name, "network still exists after removal; no further deletion attempted");
        return 0;
    }

    public string VerifiedBackendName(string name)
    {
        var backendName = BackendName(settings.ResourceNamespace!, name);
        if (Inspect(name, backendName) is null)
            throw Failure(name, "the declared network does not exist, is not owned by wip, or is not internal; isolation is unverified so no container was started");
        return backendName;
    }

    /// <summary>
    /// How many containers the network still has attached, as <c>inspect</c> reports them in
    /// its <c>Containers</c> map. Throws rather than guessing when the map cannot be read: an
    /// unknown attachment count must not become a removal.
    /// </summary>
    private int Attachments(string name, string backendName)
    {
        // Deliberately a scan over containers rather than `network inspect`'s own
        // `Containers` map, the way VolumeLifecycle counts volume references. Measured
        // 2026-10-24: that map lists only *running* containers, and WSLC will remove a
        // network a stopped container is still attached to -- after which that container can
        // never start again. Its attachment is pinned to the removed network's id, so
        // recreating the same name does not restore it (`start` fails with
        // "network <id> not found") and the container's own inspect still names the network,
        // so nothing downstream can tell the difference either.
        var rows = Records(Probe(["list", "--all", "--format", "json"]), allowEmpty: true);
        var ids = new List<string>();
        foreach (var row in rows)
        {
            // Older WSLC keeps deleted tombstones in `list --all`; there is no container left
            // to inspect, and including them would fail the completeness check below.
            if (row.TryGetProperty("State", out _) && IsDeleted(row)) continue;
            var id = Text(row, "Id") ?? Text(row, "ID");
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith('-') || id.Any(char.IsControl))
                throw Failure(name, "the attachment scan returned an invalid container ID; nothing was deleted");
            ids.Add(id);
        }
        if (ids.Count == 0) return 0;
        var attached = 0;
        foreach (var batch in ids.Chunk(100))
        {
            var records = Records(Probe(["inspect", "--type", "container", "--format", "json", .. batch]));
            if (records.Count != batch.Length)
                throw Failure(name, "the attachment scan returned an incomplete inspection; attachments are unknown so nothing was deleted");
            foreach (var container in records)
            {
                // A container whose attachments cannot be read counts as attached. Keeping a
                // network nothing uses leaves an orphan to clean up; removing one something
                // still uses leaves a container that can never start, so the unknown case
                // goes the harmless way.
                if (!container.TryGetProperty("NetworkSettings", out var networkSettings) || networkSettings.ValueKind != JsonValueKind.Object ||
                    !networkSettings.TryGetProperty("Networks", out var networks) || networks.ValueKind != JsonValueKind.Object)
                {
                    attached++;
                    continue;
                }
                if (networks.EnumerateObject().Any(n => n.Name == backendName)) attached++;
            }
        }
        return attached;
    }

    /// <summary>Whether a <c>list</c> row is a tombstone, in either spelling WSLC has used.</summary>
    private static bool IsDeleted(JsonElement record)
    {
        if (!record.TryGetProperty("State", out var state)) return false;
        if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Status", out var status)) state = status;
        return state.ValueKind switch
        {
            JsonValueKind.String => state.GetString() == "deleted",
            JsonValueKind.Number => state.TryGetInt32(out var number) && number == 4,
            _ => false,
        };
    }

    /// <summary>
    /// The inspected record of an owned, internal network, or null when any part of that
    /// cannot be confirmed. Null is the fail-closed answer: callers start nothing on it.
    /// </summary>
    private JsonElement? Inspect(string name, string backendName)
    {
        var result = backend(["network", "inspect", "--format", "json", backendName], ProbeTimeout, SandboxConsoleMode.Capture);
        if (result.Code != 0) return null;
        var records = Records(result.Output, allowEmpty: true);
        if (records.Count != 1) return null;
        var record = records[0];
        if (Text(record, "Name") != backendName) throw Failure(name, "network identity changed during inspection; retry status");
        if (!record.TryGetProperty("Internal", out var isInternal) || isInternal.ValueKind != JsonValueKind.True) return null;
        if (!record.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object) return null;
        if (Text(labels, OwnerLabel) != Identity(name)) return null;
        return record;
    }

    private string Probe(IReadOnlyList<string> arguments)
    {
        var result = backend(arguments, ProbeTimeout, SandboxConsoleMode.Capture);
        if (result.Code != 0) throw new WipException($"Network probe failed (exit {result.Code}); isolation is unknown, no container was started");
        return result.Output;
    }

    private static WipException Failure(string name, string reason) => new($"Network {name}: {reason}");
}
