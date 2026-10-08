using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wip.Configuration;

namespace Wip.Execution;

public sealed record SandboxCommandResult(int Code, string Output);
public sealed record SandboxMount(string Type, string Name, string Destination);
public sealed record SandboxStatus(string Name, string BackendName, string? Id, string State, IReadOnlyList<SandboxMount>? Mounts = null)
{
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = Mounts ?? [];
}

/// <summary>How a backend invocation uses wip's own console.</summary>
/// <remarks>
/// One mode rather than a pair of booleans, because capturing a child's output and handing
/// it the console are mutually exclusive: the interactive child writes straight to the
/// terminal, so there is nothing left for wip to read.
/// </remarks>
public enum SandboxConsoleMode
{
    /// <summary>Stream the child's output through wip, keeping none of it.</summary>
    Stream,

    /// <summary>Capture the child's output so a probe can parse it.</summary>
    Capture,

    /// <summary>Hand wip's own stdin/stdout/stderr to the child so a user can drive it.</summary>
    Interactive,
}

/// <summary>Backend argv are passed directly, never through a shell. Capture is only needed for probes.</summary>
public delegate SandboxCommandResult SandboxBackend(IReadOnlyList<string> arguments, TimeSpan timeout, SandboxConsoleMode console);

/// <param name="reportRelayDirectory">
/// Host directory for a sandbox's report relay socket; required only to create a sandbox
/// that declares <c>report_relay</c>.
/// </param>
public sealed class SandboxLifecycle(SandboxSettings settings, SandboxBackend backend, IVolumeLifecycle? volumes = null,
    Func<string, string>? reportRelayDirectory = null)
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
        var mounts = ExtractMounts(record);
        return new(name, backendName, inspectedId, state, mounts);
    }

    public int Create(string name)
    {
        var definition = Definition(name);
        if (definition.Image.StartsWith('-')) throw new ConfigException("Sandbox image must not start with '-'");
        var volumeDefs = ValidateVolumes(name, definition);
        if (volumeDefs.Count != 0 && volumes is null)
            throw new ConfigException($"Sandbox {name}: volume lifecycle is not configured; no container was created");
        var relayDirectory = RelayDirectory(name, definition);
        var existing = Status(name);
        if (existing.Id is not null)
        {
            VerifyExistingMounts(name, existing, volumeDefs);
            VerifyRelayMount(name, existing, definition);
            if (existing.State == "running")
            {
                if (volumeDefs.Count != 0 && volumes is not null)
                {
                    foreach (var vDef in volumeDefs)
                    {
                        var code = volumes.Reconcile(vDef.Name);
                        if (code != 0) return code;
                    }
                }
                return 0;
            }
            if (existing.State is not ("created" or "exited"))
                throw Failure(name, $"cannot start state {existing.State}; run sandbox destroy, confirm absence, then sandbox create");
            var started = backend(["start", existing.Id], MutationTimeout, SandboxConsoleMode.Stream);
            if (started.Code != 0) return started.Code;
        }
        else
        {
            var runArgs = new List<string>
            {
                "run",
                "--name", existing.BackendName,
                "-d",
                "--label", $"{OwnerLabel}={Identity(name)}",
            };
            if (volumeDefs.Count != 0)
            {
                if (volumes is null) throw new ConfigException($"Sandbox {name}: volume lifecycle is not configured; no container was created");
                foreach (var vDef in volumeDefs)
                {
                    var code = volumes.Create(vDef.Name);
                    if (code != 0) return code;
                }
                var sortedVolumeDefs = volumeDefs.OrderBy(v => v.Mount.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
                foreach (var vDef in sortedVolumeDefs)
                {
                    var vStatus = volumes.Status(vDef.Name);
                    if (vStatus.BackendName is null) throw Failure(name, $"volume {vDef.Name} was not found after creation");
                    runArgs.Add("--mount");
                    runArgs.Add($"type=volume,source={vStatus.BackendName},target={vDef.Mount}");
                }
            }
            if (relayDirectory is not null)
            {
                // Read-only: the sandbox may connect to the socket but never plant files on the host.
                runArgs.Add("--mount");
                runArgs.Add($"type=bind,source={relayDirectory},target={ReportRelay.MountPath},readonly");
            }
            runArgs.Add(definition.Image);
            var created = backend(runArgs, MutationTimeout, SandboxConsoleMode.Stream);
            // Even a failed/timed-out run can leave a container. Keep it for ownership-checked recovery.
            if (created.Code != 0) return created.Code;
        }
        if (Status(name).State != "running") throw Failure(name, "creation/start did not produce a running sandbox; inspect status and image CMD, then retry or destroy");
        if (volumeDefs.Count != 0 && volumes is not null)
        {
            foreach (var vDef in volumeDefs)
            {
                var code = volumes.Reconcile(vDef.Name);
                if (code != 0) return code;
            }
        }
        return 0;
    }

    public int Stop(string name)
    {
        var existing = Status(name);
        if (existing.Id is null || existing.State is "created" or "exited") return 0;
        if (existing.State != "running")
            throw Failure(name, $"cannot stop state {existing.State}; inspect status before recovery");
        // Keep the container and all mounts. No volume lifecycle/reconciliation is needed.
        var stopped = backend(["stop", existing.Id], MutationTimeout, SandboxConsoleMode.Stream);
        if (stopped.Code != 0) return stopped.Code; // Outcome may be unknown; never retry here.
        var confirmed = Status(name);
        if (confirmed.Id != existing.Id || confirmed.State != "exited")
            throw Failure(name, "stop did not confirm the same stopped container; outcome is unknown, inspect status before recovery");
        return 0;
    }

    public int Destroy(string name)
    {
        var definition = Definition(name);
        var existing = Status(name);
        var reconcileVolumes = VolumesToReconcileOnDestroy(definition, existing);
        if (reconcileVolumes.Count != 0 && volumes is null)
            throw new ConfigException($"Sandbox {name}: volume lifecycle is not configured; container removal cannot reconcile volumes");

        if (existing.Id is not null)
        {
            if (reconcileVolumes.Count != 0 && volumes is not null)
            {
                foreach (var vName in reconcileVolumes)
                {
                    var code = volumes.Reconcile(vName);
                    if (code != 0) return code;
                }
            }
            // Deliberately no volume removal flag or volume command.
            var removed = backend(["remove", "-f", existing.Id], MutationTimeout, SandboxConsoleMode.Stream);
            if (removed.Code != 0) return removed.Code;
            if (Status(name).Id is not null) throw Failure(name, "container still exists after removal; retry status before recovery");
        }
        if (reconcileVolumes.Count != 0)
        {
            if (volumes is null) throw new ConfigException($"Sandbox {name}: volume lifecycle is not configured; container removal cannot reconcile volumes");
            foreach (var vName in reconcileVolumes)
            {
                var code = volumes.Reconcile(vName);
                if (code != 0) return code;
            }
        }
        return 0;
    }

    private IReadOnlyList<string> VolumesToReconcileOnDestroy(SandboxDefinition definition, SandboxStatus existing)
    {
        var result = new HashSet<string>(definition.Volumes, StringComparer.Ordinal);
        if (existing.Id is not null)
        {
            foreach (var mount in existing.Mounts)
            {
                if (!string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase)) continue;
                var matched = settings.Volumes.FirstOrDefault(v =>
                    mount.Name.StartsWith(VolumeLifecycle.BackendPrefix(settings.ResourceNamespace!, v.Name), StringComparison.Ordinal));
                if (matched is not null)
                {
                    result.Add(matched.Name);
                }
            }
        }
        return result.ToArray();
    }

    private void VerifyExistingMounts(string name, SandboxStatus existing, IReadOnlyList<VolumeDefinition> volumeDefs)
    {
        var wipVolumeMounts = existing.Mounts
            .Where(m => string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase) &&
                        m.Name.StartsWith("wip-v-", StringComparison.Ordinal))
            .ToArray();
        if (wipVolumeMounts.Length != volumeDefs.Count)
            throw Failure(name, "existing container mounts do not match declared volumes; run sandbox destroy, confirm absence, then sandbox create");

        if (volumeDefs.Count == 0) return;

        foreach (var vDef in volumeDefs)
        {
            var vStatus = volumes!.Status(vDef.Name);
            if (vStatus.BackendName is null)
                throw Failure(name, "existing container mounts do not match declared volumes; run sandbox destroy, confirm absence, then sandbox create");
            var matched = wipVolumeMounts.Any(m =>
                string.Equals(m.Name, vStatus.BackendName, StringComparison.Ordinal) &&
                string.Equals(m.Destination, vDef.Mount, StringComparison.Ordinal));
            if (!matched)
                throw Failure(name, "existing container mounts do not match declared volumes; run sandbox destroy, confirm absence, then sandbox create");
        }
    }

    private string? RelayDirectory(string name, SandboxDefinition definition)
    {
        if (!definition.ReportRelay) return null;
        var directory = reportRelayDirectory?.Invoke(name)
            ?? throw new ConfigException($"Sandbox {name}: report relay directory is not configured; no container was created");
        if (directory.Contains(',') || directory.Any(char.IsControl))
            throw new ConfigException($"Sandbox {name}: report relay directory cannot contain ',' or control characters");
        return directory;
    }

    /// <summary>
    /// A container created before <c>report_relay</c> was toggled has the wrong mounts, and
    /// mounts cannot change after creation, so the mismatch is reported rather than ignored.
    /// </summary>
    private static void VerifyRelayMount(string name, SandboxStatus existing, SandboxDefinition definition)
    {
        var mounted = existing.Mounts.Any(m =>
            string.Equals(m.Type, "bind", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Destination, ReportRelay.MountPath, StringComparison.Ordinal));
        if (mounted != definition.ReportRelay)
            throw Failure(name, "existing container report relay mount does not match report_relay; run sandbox destroy, confirm absence, then sandbox create");
    }

    private IReadOnlyList<VolumeDefinition> ValidateVolumes(string name, SandboxDefinition definition)
    {
        if (definition.Volumes.Count == 0) return [];
        var volumeDefs = new List<VolumeDefinition>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenMounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vName in definition.Volumes)
        {
            if (!seenNames.Add(vName))
                throw new ConfigException($"Sandbox {name} repeats volume {vName}");
            var vDef = settings.Volumes.SingleOrDefault(v => v.Name == vName)
                ?? throw new ConfigException($"Sandbox {name} references an undefined volume: {vName}");
            if (vDef.Mount == "/")
                throw new ConfigException($"Sandbox {name}: volume {vName} destination cannot be '/'");
            if (vDef.Mount.Contains(','))
                throw new ConfigException($"Sandbox {name}: volume {vName} destination cannot contain ','");
            if (!seenMounts.Add(vDef.Mount))
                throw new ConfigException($"Sandbox {name} has conflicting mount destinations");
            volumeDefs.Add(vDef);
        }
        return volumeDefs;
    }

    /// <param name="environment">
    /// Variables exported to the child with <c>-e</c>; null or empty leaves the backend argv
    /// exactly as it was before the report relay existed.
    /// </param>
    public int Exec(string name, IReadOnlyList<string> argv, TimeSpan timeout, IReadOnlyList<KeyValuePair<string, string>>? environment = null)
    {
        ValidateArgv(argv);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ConfigException("sandbox exec timeout must be positive and at most 2147483 seconds");
        var options = EnvironmentOptions(environment);
        return backend(["exec", .. options, RunningId(name), .. argv], timeout, SandboxConsoleMode.Stream).Code;
    }

    /// <summary>
    /// Runs the same argv as <see cref="Exec"/>, but with wip's own stdin/stdout/stderr
    /// handed to the child, which is what a shell, a REPL or any other interactive CLI needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is deliberately no deadline parameter. <see cref="Exec"/>'s timeout exists so an
    /// automated call cannot hang a script; a session the user is typing into has no such
    /// bound, and a deadline that killed it mid-edit would be a bug rather than a safeguard.
    /// </para>
    /// <para>
    /// WSLC separates the two halves of docker's <c>-it</c>, and so does this: <c>-i</c>
    /// attaches stdin and is always passed, because a session nothing can be typed into is
    /// not interactive, while <paramref name="tty"/> adds <c>-t</c> only when wip's own stdin
    /// and stdout are a terminal -- the same condition <c>wip exec</c> applies. Piping a
    /// script into a sandboxed shell therefore still reaches it; it simply runs without a pty.
    /// </para>
    /// </remarks>
    public int ExecInteractive(string name, IReadOnlyList<string> argv, bool tty, IReadOnlyList<KeyValuePair<string, string>>? environment = null)
    {
        ValidateArgv(argv);
        var options = EnvironmentOptions(environment);
        var id = RunningId(name);
        IReadOnlyList<string> arguments = tty ? ["exec", "-i", "-t", .. options, id, .. argv] : ["exec", "-i", .. options, id, .. argv];
        return backend(arguments, Timeout.InfiniteTimeSpan, SandboxConsoleMode.Interactive).Code;
    }

    /// <summary>
    /// Connects wip's console to the sandbox's existing main process -- the image's own
    /// CMD/ENTRYPOINT, the process whose lifetime is the sandbox's lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not <see cref="ExecInteractive"/> with a default command. Exec starts a new
    /// process beside the main one and ends when that process ends; attach joins the process
    /// that is already there, so what reaches it -- input, Ctrl-C, EOF -- reaches the process
    /// the sandbox exists to run. Ending it ends the sandbox, which is why the two are
    /// separate commands rather than one with a flag.
    /// </para>
    /// <para>
    /// WSLC's <c>attach</c> takes no command and no <c>-i</c>/<c>-t</c> of its own: the
    /// streams it joins are the ones the main process was started with. There is accordingly
    /// no argv to validate and no terminal to request here, and no deadline, for the same
    /// reason <see cref="ExecInteractive"/> has none.
    /// </para>
    /// </remarks>
    public int Attach(string name) =>
        backend(["attach", RunningId(name)], Timeout.InfiniteTimeSpan, SandboxConsoleMode.Interactive).Code;

    /// <summary>
    /// Both exec paths reject argv before any probe, so a usage error never reaches the
    /// backend, and reject it in the same order: argv, then the mode's own options, then
    /// the container's state.
    /// </summary>
    private static void ValidateArgv(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0]) || argv[0].StartsWith('-'))
            throw new ConfigException("sandbox exec requires an executable (use -- before its argv)");
    }

    private static List<string> EnvironmentOptions(IReadOnlyList<KeyValuePair<string, string>>? environment)
    {
        var options = new List<string>();
        foreach (var (key, value) in environment ?? [])
        {
            if (key.Length == 0 || key.Contains('=') || key.StartsWith('-') || key.Any(char.IsControl) || value.Any(char.IsControl))
                throw new ConfigException($"sandbox exec environment variable {key} is invalid");
            options.Add("-e");
            options.Add($"{key}={value}");
        }
        return options;
    }

    /// <summary>The verified ID of the owned container, which must already be running.</summary>
    private string RunningId(string name)
    {
        var existing = Status(name);
        if (existing.Id is null || existing.State != "running") throw Failure(name, "sandbox is not running; run sandbox create first");
        return existing.Id;
    }

    private string Probe(IReadOnlyList<string> argv)
    {
        var result = backend(argv, ProbeTimeout, SandboxConsoleMode.Capture);
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

    private static IReadOnlyList<SandboxMount> ExtractMounts(JsonElement record)
    {
        if (!record.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<SandboxMount>();
        foreach (var mount in mounts.EnumerateArray())
        {
            if (mount.ValueKind != JsonValueKind.Object) continue;
            var type = Text(mount, "Type") ?? "";
            var name = Text(mount, "Name") ?? "";
            var destination = (Text(mount, "Destination") ?? Text(mount, "Target") ?? "").TrimEnd('/');
            if (destination.Length == 0 && (Text(mount, "Destination") == "/" || Text(mount, "Target") == "/"))
                destination = "/";
            result.Add(new(type, name, destination));
        }
        return result;
    }
}
