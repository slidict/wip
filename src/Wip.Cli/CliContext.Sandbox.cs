using Wip.Configuration;
using Wip.Execution;

namespace Wip.Cli;

internal sealed partial class CliContext
{
    internal int Sandbox(string operation, string name, string[] argv, int timeoutSeconds, bool interactive = false)
    {
        if (operation == "status")
        {
            var lifecycle = Lifecycle();
            var status = lifecycle.Status(name);
            Console.WriteLine($"{status.Name}\t{status.State}\t{status.BackendName}\t{status.Id ?? "-"}");
            return 0;
        }
        if (operation == "exec")
        {
            var lifecycle = Lifecycle();
            var environment = ExecEnvironment(name);
            // Tty() keeps the -t request honest: a terminal wip itself does not have cannot
            // be handed on, so a piped invocation gets stdin attached without a pty rather
            // than asking wslc for one that does not exist.
            return interactive
                ? lifecycle.ExecInteractive(name, argv, Tty(true), environment)
                : lifecycle.Exec(name, argv, TimeSpan.FromSeconds(timeoutSeconds), environment);
        }
        if (operation == "attach")
        {
            var lifecycle = Lifecycle();
            return lifecycle.Attach(name);
        }
        if (operation == "stop")
        {
            // Stopping keeps both the container and its attached volumes; no storage mutation.
            return Lifecycle().Stop(name);
        }

        var definition = Definition(name);

        bool needsVolumes;
        if (operation == "create")
        {
            needsVolumes = definition.Volumes.Count > 0;
        }
        else if (operation == "destroy")
        {
            if (definition.Volumes.Count > 0)
            {
                needsVolumes = true;
            }
            else
            {
                var probeLifecycle = Lifecycle();
                var status = probeLifecycle.Status(name);
                var ns = Config.SandboxResources.ResourceNamespace;
                needsVolumes = status.Id is not null && ns is not null && status.Mounts.Any(m =>
                    string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase) &&
                    Config.SandboxResources.Volumes.Any(v =>
                        m.Name.StartsWith(VolumeLifecycle.BackendPrefix(ns, v.Name), StringComparison.Ordinal)));
            }
        }
        else
        {
            throw new ArgumentException("Unknown sandbox operation", nameof(operation));
        }

        if (!needsVolumes)
        {
            var lifecycle = Lifecycle();
            return operation switch
            {
                "create" => lifecycle.Create(name),
                "destroy" => lifecycle.Destroy(name),
                _ => throw new ArgumentException("Unknown sandbox operation", nameof(operation)),
            };
        }

        var directory = Path.Combine(ConfigDirectory, ".wip", "volume-usage");
        using var usage = new FileVolumeUsageStore(directory);
        var volumeLifecycle = new VolumeLifecycle(Config.SandboxResources, ResourceBackend(), usage);
        var lifecycleWithVolumes = Lifecycle(volumeLifecycle);
        return operation switch
        {
            "create" => lifecycleWithVolumes.Create(name),
            "destroy" => lifecycleWithVolumes.Destroy(name),
            _ => throw new ArgumentException("Unknown sandbox operation", nameof(operation)),
        };
    }

    /// <summary>
    /// Runs the host side of a sandbox's report relay until interrupted: creates the report
    /// FIFO inside the sandbox, reads it through <c>sandbox exec</c>, and forwards what the
    /// filter allows to Herdr. Only the host runs this; the sandbox never sees Herdr's pipe.
    /// </summary>
    internal int SandboxRelay(string name, string? upstream)
    {
        var definition = Definition(name);
        if (!definition.ReportRelay)
            throw new ConfigException($"Sandbox {name} does not declare report_relay: true");
        var herdr = upstream ?? System.Environment.GetEnvironmentVariable("HERDR_SOCKET_PATH");
        if (string.IsNullOrWhiteSpace(herdr))
            throw new ConfigException("sandbox relay needs Herdr's socket: pass --upstream or set HERDR_SOCKET_PATH");
        // The relay, not the sandbox, says which pane a report is about.
        var pane = System.Environment.GetEnvironmentVariable(ReportRelay.PaneVariable);
        if (string.IsNullOrWhiteSpace(pane) || pane.Any(char.IsControl))
            throw new ConfigException($"sandbox relay needs {ReportRelay.PaneVariable}: run it from the Herdr pane the sandbox's CLI reports for");

        // Held until the relay exits: one relay per sandbox, so no report goes to a second reader.
        using var relayLock = ReportRelay.AcquireRelayLock(ReportRelay.LockPath(ConfigDirectory, name), name);
        var lifecycle = Lifecycle();
        var executable = Resolver.Resolve(Config.WslcCommand);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var server = new ReportRelayServer(name, pane, ReportRelayServer.ForHost(herdr), Console.Error.WriteLine);
        server.RunAsync(
            (onLine, token) => ReadFifoAsync(executable, lifecycle.ExecArguments(name, ReportRelay.ReadFifoCommand), onLine, token),
            _ =>
            {
                var code = lifecycle.Exec(name, ["sh", "-c", ReportRelay.CreateFifoScript], TimeSpan.FromSeconds(30));
                if (code != 0) throw new WipException($"Sandbox {name}: could not create the report FIFO (exit {code})");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            cancellation.Token).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>One <c>cat</c> of the FIFO, streamed line by line until every writer has closed it.</summary>
    private static async Task<int> ReadFifoAsync(string executable, IReadOnlyList<string> arguments, Func<string, Task> onLine, CancellationToken cancellation)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new WipException("could not start the report FIFO reader");
        process.StandardInput.Close();
        using var stop = cancellation.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        await ReportRelay.PumpLinesAsync(process.StandardOutput, onLine, cancellation);
        await process.WaitForExitAsync(cancellation);
        return process.ExitCode;
    }

    private SandboxLifecycle Lifecycle(IVolumeLifecycle? volumes = null) =>
        new(Config.SandboxResources, ResourceBackend(), volumes);

    private SandboxDefinition Definition(string name) =>
        Config.SandboxResources.Sandboxes.SingleOrDefault(s => s.Name == name)
            ?? throw new ConfigException($"Unknown sandbox: {name}");

    private string ConfigDirectory => Path.GetDirectoryName(Path.GetFullPath(Config.Path ?? "wip.yml"))!;

    /// <summary>Null unless the sandbox opted into the relay, so other execs are unchanged.</summary>
    private IReadOnlyList<KeyValuePair<string, string>>? ExecEnvironment(string name)
    {
        var definition = Config.SandboxResources.Sandboxes.SingleOrDefault(s => s.Name == name);
        return definition is { ReportRelay: true }
            ? ReportRelay.ExecEnvironment(name, System.Environment.GetEnvironmentVariable)
            : null;
    }
}
