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
        return (arguments, timeout, console) =>
        {
            if (console == SandboxConsoleMode.Capture)
            {
                var result = Probe([executable, .. arguments], timeout);
                return new(result.Code, result.Output);
            }
            var runner = new CommandRunner(Interpreter, debug: Debug, quiet: Quiet);
            if (console == SandboxConsoleMode.Interactive)
            {
                // The child owns the console, so there is no stream for CommandRunner to
                // read and no deadline to enforce: the session ends when the child exits.
                // Its exit status is still the one wip returns.
                return new(runner.Run([executable, .. arguments], interactive: true), "");
            }
            return new(runner.Run([executable, .. arguments], timeout: timeout), "");
        };
    }
}
