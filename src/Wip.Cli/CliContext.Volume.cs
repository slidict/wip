using Wip.Execution;

namespace Wip.Cli;

internal sealed partial class CliContext
{
    internal int Volume(string operation, string name)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Config.Path ?? "wip.yml"))!, ".wip", "volume-usage");
        using var usage = new FileVolumeUsageStore(directory);
        var lifecycle = new VolumeLifecycle(Config.SandboxResources, ResourceBackend(), usage);
        if (operation == "status")
        {
            var status = lifecycle.Status(name);
            Console.WriteLine($"{status.Name}\t{(status.BackendName is null ? "not found" : "exists")}\t{(status.Persistent ? "persistent" : "ephemeral")}\t{status.BackendName ?? "-"}\treferences={status.References.Count}\tused={status.WasUsed}");
            return 0;
        }
        return operation switch
        {
            "create" => lifecycle.Create(name),
            "destroy" => lifecycle.Destroy(name),
            "reconcile" => lifecycle.Reconcile(name),
            _ => throw new ArgumentException("Unknown volume operation", nameof(operation)),
        };
    }

    private SandboxBackend ResourceBackend()
    {
        var executable = Resolver.Resolve(Config.WslcCommand);
        return (arguments, timeout, capture) =>
        {
            if (capture)
            {
                var result = Probe([executable, .. arguments], timeout);
                return new(result.Code, result.Output);
            }
            var runner = new CommandRunner(Interpreter, debug: Debug, quiet: Quiet);
            return new(runner.Run([executable, .. arguments], timeout: timeout), "");
        };
    }
}
