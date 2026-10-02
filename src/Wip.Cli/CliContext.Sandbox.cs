using Wip.Execution;

namespace Wip.Cli;

internal sealed partial class CliContext
{
    internal int Sandbox(string operation, string name, string[] argv, int timeoutSeconds)
    {
        var executable = Resolver.Resolve(Config.WslcCommand);
        var lifecycle = new SandboxLifecycle(Config.SandboxResources, (arguments, timeout, capture) =>
        {
            if (capture)
            {
                var result = Probe([executable, .. arguments], timeout);
                return new(result.Code, result.Output);
            }
            var runner = new CommandRunner(Interpreter, debug: Debug, quiet: Quiet);
            return new(runner.Run([executable, .. arguments], timeout: timeout), "");
        });
        if (operation == "status")
        {
            var status = lifecycle.Status(name);
            Console.WriteLine($"{status.Name}\t{status.State}\t{status.BackendName}\t{status.Id ?? "-"}");
            return 0;
        }
        return operation switch
        {
            "create" => lifecycle.Create(name),
            "destroy" => lifecycle.Destroy(name),
            "exec" => lifecycle.Exec(name, argv, TimeSpan.FromSeconds(timeoutSeconds)),
            _ => throw new ArgumentException("Unknown sandbox operation", nameof(operation)),
        };
    }
}
