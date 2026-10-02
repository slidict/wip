using Wip.Execution;

namespace Wip.Cli;

internal sealed partial class CliContext
{
    internal int Sandbox(string operation, string name, string[] argv, int timeoutSeconds)
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
            return lifecycle.Exec(name, argv, TimeSpan.FromSeconds(timeoutSeconds));
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
