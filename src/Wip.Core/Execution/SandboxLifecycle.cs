using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wip.Configuration;

namespace Wip.Execution;

public sealed record SandboxCommandResult(int Code, string Output)
{
    public bool TimedOut { get; init; }
    public bool OutputLimitExceeded { get; init; }
}
public sealed record SandboxMount(string Type, string Name, string Destination);
public sealed record SandboxStatus(
    string Name,
    string BackendName,
    string? Id,
    string State,
    IReadOnlyList<SandboxMount>? Mounts = null,
    IReadOnlyList<string>? Networks = null,
    string? Image = null)
{
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = Mounts ?? [];

    /// <summary>Backend names of every network the container is attached to.</summary>
    public IReadOnlyList<string> Networks { get; init; } = Networks ?? [];

    /// <summary>
    /// The image reference the container was created from, as the backend recorded it, or null
    /// when it cannot be read. A declaration can be edited after a container exists, so this
    /// is the only way to know what is actually running.
    /// </summary>
    public string? Image { get; init; } = Image;

    /// <summary>The source argv in Config.Cmd, or null when it cannot be read.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>The source argv in Config.Entrypoint, or null when it cannot be read.</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }
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

    /// <summary>Capture a scan report with an explicit storage limit.</summary>
    CaptureReport,
}

/// <summary>Backend argv are passed directly, never through a shell. Capture is only needed for probes.</summary>
public delegate SandboxCommandResult SandboxBackend(IReadOnlyList<string> arguments, TimeSpan timeout, SandboxConsoleMode console);

public sealed class SandboxLifecycle(
    SandboxSettings settings,
    SandboxBackend backend,
    IVolumeLifecycle? volumes = null,
    INetworkLifecycle? networks = null)
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
        return new(name, backendName, inspectedId, state, mounts, ExtractNetworks(record), ExtractImage(record))
        {
            Command = ExtractArgv(record, "Cmd"),
            Entrypoint = ExtractArgv(record, "Entrypoint")
        };
    }

    public int Create(string name)
    {
        var definition = Definition(name);
        if (definition.Image.StartsWith('-')) throw new ConfigException("Sandbox image must not start with '-'");
        // --entrypoint also clears the image's CMD, so the executable would run with no operand
        // and exit at once. Refused here rather than at load, so other commands, destroy
        // included, still work on a config that already declares it.
        if (definition.Entrypoint is not null && definition.Command is null)
            throw new ConfigException(
                $"Sandbox {name}: entrypoint requires command; the override clears the image's CMD, so give the executable's arguments in command, as in [infinity]; nothing was started");
        var volumeDefs = ValidateVolumes(name, definition);
        if (volumeDefs.Count != 0 && volumes is null)
            throw new ConfigException($"Sandbox {name}: volume lifecycle is not configured; no container was created");
        // Before the container, and before any volume is touched: the declared boundary has to
        // exist and be verifiable, or nothing starts. There is no path from here that falls
        // back to the backend's default bridge.
        var isolatedNetwork = PrepareNetwork(name, definition);
        var existing = Status(name);
        if (existing.Id is not null)
        {
            VerifyExistingMounts(name, existing, volumeDefs);
            VerifyExistingNetworks(name, existing, isolatedNetwork);
            VerifyExistingCommand(name, definition, existing);
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
            if (isolatedNetwork is not null)
            {
                runArgs.Add("--network");
                runArgs.Add(isolatedNetwork);
            }
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
            if (definition.Entrypoint is { } entrypoint) runArgs.AddRange(["--entrypoint", entrypoint]);
            runArgs.Add(definition.Image);
            if (definition.Command is { } command) runArgs.AddRange(command);
            var created = backend(runArgs, MutationTimeout, SandboxConsoleMode.Stream);
            // Even a failed/timed-out run can leave a container. Keep it for ownership-checked recovery.
            if (created.Code != 0) return created.Code;
        }
        var current = Status(name);
        if (current.State != "running") throw Failure(name, "creation/start did not produce a running sandbox; inspect status and image CMD, then retry or destroy");
        VerifyExistingCommand(name, definition, current, freshlyCreated: true);
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
        // Hoisted to join the volume guard, for the reason that one is here: refusing further
        // down would mean refusing after the container and its volumes were already gone,
        // leaving the declared network with nothing left to remove it.
        if (definition.Network is not null && networks is null)
            throw new ConfigException($"Sandbox {name}: network lifecycle is not configured; container removal cannot remove its network");

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
        // Last, and for two reasons. After the container, so the network is never removed
        // from under a live attachment -- and after the reconcile above, because that is the
        // one that frees volumes now the container is gone. Removing the network first meant
        // a squatted name or a failing `network remove` skipped it, and the names could not
        // be recovered on a retry: the container is already gone by then, so
        // VolumesToReconcileOnDestroy no longer learns them from its mounts.
        if (definition.Network is { } networkName)
        {
            if (networks is null)
                throw new ConfigException($"Sandbox {name}: network lifecycle is not configured; container removal cannot remove its network");
            var code = networks.Destroy(networkName);
            if (code != 0) return code;
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

    /// <summary>
    /// Creates and verifies the declared network, returning the backend name to attach to, or
    /// null when the sandbox declares none. Throws on anything it cannot confirm: an
    /// unverified boundary must stop the operation, because the alternative -- carrying on
    /// without <c>--network</c> -- is the backend's default bridge, which is unrestricted.
    /// </summary>
    private string? PrepareNetwork(string name, SandboxDefinition definition)
    {
        if (definition.Network is not { } networkName) return null;
        if (networks is null)
            throw new ConfigException($"Sandbox {name}: network lifecycle is not configured; no container was created");
        var code = networks.Create(networkName);
        if (code != 0)
            throw Failure(name, $"network {networkName} could not be created (exit {code}); isolation is unverified so no container was started");
        return networks.VerifiedBackendName(networkName);
    }

    /// <summary>
    /// An existing container's attachments are fixed at creation, so a declaration that has
    /// since changed -- or a container that came back from an abnormal exit on a different
    /// network -- is refused rather than started or re-attached.
    /// </summary>
    private void VerifyExistingNetworks(string name, SandboxStatus existing, string? isolatedNetwork)
    {
        if (isolatedNetwork is null)
        {
            // Nothing is declared, so nothing is asserted: a sandbox without a network block
            // keeps whatever the backend attached, exactly as before this existed.
            return;
        }
        if (existing.Networks.Count != 1 || existing.Networks[0] != isolatedNetwork)
            throw Failure(name, "existing container is not attached to exactly the declared network; run sandbox destroy, confirm absence, then sandbox create");
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
    public int Exec(string name, IReadOnlyList<string> argv, TimeSpan timeout, IReadOnlyList<KeyValuePair<string, string>>? environment = null) =>
        ExecWithResult(name, argv, timeout, environment).Code;

    /// <summary>Execute while retaining timeout information separately from the exit code.</summary>
    internal SandboxCommandResult ExecWithResult(string name, IReadOnlyList<string> argv, TimeSpan timeout, IReadOnlyList<KeyValuePair<string, string>>? environment = null)
    {
        ValidateArgv(argv);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ConfigException("sandbox exec timeout must be positive and at most 2147483 seconds");
        var options = EnvironmentOptions(environment);
        return backend(["exec", .. options, RunningId(name), .. argv], timeout, SandboxConsoleMode.Stream);
    }

    /// <summary>Collect a scan report without limiting ordinary probe output.</summary>
    internal SandboxCommandResult CaptureReport(string name, IReadOnlyList<string> argv, TimeSpan timeout)
    {
        ValidateArgv(argv);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ConfigException("sandbox capture timeout must be positive and at most 2147483 seconds");
        return backend(["exec", RunningId(name), .. argv], timeout, SandboxConsoleMode.CaptureReport);
    }

    /// <summary>
    /// The backend arguments for running <paramref name="argv"/> in the verified, running
    /// container, for a caller that must read the child's output as it streams -- the report
    /// relay reading the sandbox FIFO -- rather than through <see cref="SandboxBackend"/>.
    /// </summary>
    public IReadOnlyList<string> ExecArguments(string name, IReadOnlyList<string> argv)
    {
        ValidateArgv(argv);
        return ["exec", RunningId(name), .. argv];
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

    /// <summary>
    /// The verified, running container's ID -- verified including its declared boundary.
    /// </summary>
    /// <remarks>
    /// The network check lives here because this is the guard <see cref="Exec"/>,
    /// <see cref="CaptureReport"/>, <see cref="ExecArguments"/>, <see cref="ExecInteractive"/> and
    /// <see cref="Attach"/> all pass through. Create refuses a container that is not attached
    /// to exactly its declared network, but until this, nothing else did: a container made
    /// before `network:` was added to wip.yml kept running on whatever it was given, and an
    /// exec into it ran with unrestricted egress. A sandbox that declares
    /// no network is unaffected, so this costs nothing where there is no boundary to check.
    /// </remarks>
    private string RunningId(string name)
    {
        var existing = Status(name);
        if (existing.Id is null || existing.State != "running") throw Failure(name, "sandbox is not running; run sandbox create first");
        var definition = Definition(name);
        VerifyExistingCommand(name, definition, existing);
        if (definition.Network is { } networkName)
        {
            if (networks is null)
                throw new ConfigException($"Sandbox {name}: network lifecycle is not configured; its declared network cannot be verified");
            var expected = networks.VerifiedBackendName(networkName);
            if (existing.Networks.Count != 1 || existing.Networks[0] != expected)
                throw Failure(name, "is not attached to exactly its declared network; run sandbox destroy, confirm absence, then sandbox create");
        }
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

    /// <summary>
    /// Refuses a container whose command or entrypoint differs from its declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as the image check, for the same reason: a declaration can be edited
    /// after the container was made, and the container keeps whatever argv it was created with.
    /// Without this, editing <c>command:</c> left the old process as PID 1 while
    /// <c>sandbox create</c> reported success.
    /// </para>
    /// <para>
    /// <paramref name="freshlyCreated"/> separates two failures that need different advice. For
    /// a container that already existed, a mismatch is drift and destroy-then-create fixes it.
    /// For one wip just created, a mismatch means the backend did not record the argv wip
    /// passed, and recreating reproduces it exactly -- so that advice would loop forever.
    /// </para>
    /// <para>
    /// A declaration that was removed is not checked. "No command declared" would have to mean
    /// "the image's own CMD", which needs the image inspected rather than the container, so
    /// deleting <c>command:</c> from a live sandbox leaves the old process running and nothing
    /// says so. Unlike <c>network:</c>, where undeclared genuinely asserts nothing, this one is
    /// checkable and simply is not checked yet.
    /// </para>
    /// </remarks>
    private static void VerifyExistingCommand(
        string name, SandboxDefinition definition, SandboxStatus status, bool freshlyCreated = false)
    {
        if (definition.Entrypoint is { } entrypoint)
        {
            // A container created before the declaration existed has no entrypoint recorded,
            // and destroy-then-create does fix that. Only a freshly created one proves the
            // backend will not report it.
            if (status.Entrypoint is null)
                throw Failure(name, freshlyCreated
                    ? "the declared entrypoint cannot be verified because the backend does not report Config.Entrypoint as an argv list; recreating will not change that"
                    : "the declared entrypoint is not recorded in Config.Entrypoint; run sandbox destroy, confirm absence, then sandbox create");
            if (!status.Entrypoint.SequenceEqual(new[] { entrypoint }, StringComparer.Ordinal))
                throw Failure(name, freshlyCreated
                    ? "the backend recorded a different entrypoint from the executable wip passed; recreating will not change that"
                    : "the declared entrypoint differs from Config.Entrypoint; run sandbox destroy, confirm absence, then sandbox create");
        }
        // As with command, removing a declaration does not assert an image default.
        if (definition.Command is not { } expected) return;

        // Unreadable is not drift, wherever it is found. The backend is not reporting argv the
        // way wip reads it, and no container this backend creates will, so destroy-and-create
        // cannot help -- which is what the advice used to say here, and on the exec path it
        // said it even after Create had already explained that recreating changes nothing.
        if (status.Command is null)
            throw Failure(
                name,
                "the declared command cannot be verified because the backend does not report Config.Cmd as an argv list; "
                    + "recreating the sandbox will not change that. Remove command: to run the image's own CMD, "
                    + "or use an image whose own CMD stays running.");

        if (!expected.SequenceEqual(status.Command, StringComparer.Ordinal))
        {
            // Drift is fixable by recreating; a disagreement with argv wip itself just passed
            // is not, and the container is already running, so say what does end it.
            throw Failure(name, freshlyCreated
                ? "the backend recorded a different command from the argv wip passed, so recreating the sandbox will not change it. "
                    + "Remove command: to run the image's own CMD, or use an image whose own CMD stays running."
                : "the declared command differs from Config.Cmd; run sandbox destroy, confirm absence, then sandbox create");
        }
    }

    private static IReadOnlyList<string>? ExtractArgv(JsonElement record, string property)
    {
        if (!record.TryGetProperty("Config", out var config) || config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty(property, out var command) || command.ValueKind != JsonValueKind.Array)
            return null;
        var argv = new List<string>();
        foreach (var item in command.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            argv.Add(item.GetString()!);
        }
        return argv.AsReadOnly();
    }

    /// <summary>
    /// The source reference in <c>Config.Image</c>, or null when unavailable. The top-level
    /// <c>Image</c> is a config image ID, not a repository reference, and cannot substitute
    /// for it when verifying the image declared by a scan plan.
    /// </summary>
    private static string? ExtractImage(JsonElement record)
    {
        if (record.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object &&
            Text(config, "Image") is { Length: > 0 } declared)
        {
            return declared;
        }
        return null;
    }

    /// <summary>
    /// Attachments as <c>inspect</c> reports them: <c>NetworkSettings.Networks</c> keyed by
    /// network name. An absent or malformed block yields an empty list, which fails the
    /// declared-network check rather than passing it.
    /// </summary>
    private static IReadOnlyList<string> ExtractNetworks(JsonElement record)
    {
        if (!record.TryGetProperty("NetworkSettings", out var settings) || settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("Networks", out var attached) || attached.ValueKind != JsonValueKind.Object)
            return [];
        return attached.EnumerateObject().Select(n => n.Name).ToArray();
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
