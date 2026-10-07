using Wip.Execution;

namespace Wip.Cli;

internal sealed partial class CliContext
{
    internal int Sandbox(string operation, string name, string[] argv, int timeoutSeconds, bool interactive = false)
    {
        if (operation == "status")
        {
            var lifecycle = new SandboxLifecycle(Config.SandboxResources, ResourceBackend());
            var status = lifecycle.Status(name);
            Console.WriteLine($"{status.Name}\t{status.State}\t{status.BackendName}\t{status.Id ?? "-"}");
            return 0;
        }
        if (operation == "exec")
        {
            var lifecycle = new SandboxLifecycle(Config.SandboxResources, ResourceBackend());
            // Tty() keeps the -t request honest: a terminal wip itself does not have cannot
            // be handed on, so a piped invocation gets stdin attached without a pty rather
            // than asking wslc for one that does not exist.
            return interactive
                ? lifecycle.ExecInteractive(name, argv, Tty(true))
                : lifecycle.Exec(name, argv, TimeSpan.FromSeconds(timeoutSeconds));
        }
        if (operation == "stop")
        {
            // Stopping keeps both the container and its attached volumes; no storage mutation.
            return new SandboxLifecycle(Config.SandboxResources, ResourceBackend()).Stop(name);
        }

        var definition = Config.SandboxResources.Sandboxes.SingleOrDefault(s => s.Name == name)
            ?? throw new ConfigException($"Unknown sandbox: {name}");

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
                var probeLifecycle = new SandboxLifecycle(Config.SandboxResources, ResourceBackend());
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
            var lifecycle = new SandboxLifecycle(Config.SandboxResources, ResourceBackend());
            return operation switch
            {
                "create" => lifecycle.Create(name),
                "destroy" => lifecycle.Destroy(name),
                _ => throw new ArgumentException("Unknown sandbox operation", nameof(operation)),
            };
        }

        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Config.Path ?? "wip.yml"))!, ".wip", "volume-usage");
        using var usage = new FileVolumeUsageStore(directory);
        var volumeLifecycle = new VolumeLifecycle(Config.SandboxResources, ResourceBackend(), usage);
        var lifecycleWithVolumes = new SandboxLifecycle(Config.SandboxResources, ResourceBackend(), volumeLifecycle);
        return operation switch
        {
            "create" => lifecycleWithVolumes.Create(name),
            "destroy" => lifecycleWithVolumes.Destroy(name),
            _ => throw new ArgumentException("Unknown sandbox operation", nameof(operation)),
        };
    }
}
