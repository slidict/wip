using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;

namespace Wip.Cli;

internal sealed partial class CliContext
{
    internal int Volume(string operation, string name)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Config.Path ?? "wip.yml"))!, ".wip", "volume-usage");
        using var usage = new FileVolumeUsageStore(directory);
        var lifecycle = new VolumeLifecycle(Config.SandboxResources, ResourceBackend(), usage);
        if (operation == "sync")
        {
            return lifecycle.Sync(name, SyncSource(name));
        }
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

    /// <summary>
    /// Expanded against the wip.yml directory, like <c>sync.source</c>, so the mirror covers
    /// the same tree no matter which subdirectory wip was invoked from.
    /// </summary>
    private string SyncSource(string name)
    {
        var definition = Config.SandboxResources.Volumes.SingleOrDefault(v => v.Name == name)
            ?? throw new ConfigException($"Unknown volume: {name}");
        var sync = definition.Sync
            ?? throw new ConfigException($"Volume {name}: no sync is declared; add volumes.{name}.sync to mirror a host directory");
        var directory = Path.GetDirectoryName(Path.GetFullPath(Config.Path ?? "wip.yml"))!;
        return ResolveSyncSource(directory, name, sync.Source, Directory.Exists);
    }

    /// <summary>
    /// A source that is not there is a typo or a tree that was moved, not an empty mirror: the
    /// backend would bind an absent path, or rsync would run against whatever the backend
    /// created in its place and <c>--delete</c> would then empty the volume. Checked before the
    /// path is converted for the backend, so the message names the host path as written.
    /// </summary>
    internal static string ResolveSyncSource(string configDirectory, string name, string rawSource, Func<string, bool> directoryExists)
    {
        var source = Path.GetFullPath(Path.Combine(configDirectory, rawSource));
        if (!directoryExists(source))
            throw new ConfigException($"Volume {name}: sync source directory does not exist: {source}");
        return WslPath.ForWslc(source, $"volumes.{name}.sync.source");
    }

    /// <param name="streamOutput">
    /// Where a streamed child's stdout goes. Defaults to the console's, which is what every
    /// sandbox, volume and network command wants. A caller that writes its own result to
    /// stdout -- a scan handing on a tool's report -- passes stderr instead, so the child's
    /// progress cannot be mixed into it. Passed per call rather than changed in place,
    /// because this backend is shared.
    /// </param>
    private SandboxBackend ResourceBackend(TextWriter? streamOutput = null, TextWriter? streamError = null)
    {
        var executable = Resolver.Resolve(Config.WslcCommand);
        return (arguments, timeout, console) =>
        {
            if (console == SandboxConsoleMode.Capture)
            {
                var result = Probe([executable, .. arguments], timeout);
                return new(result.Code, result.Output);
            }
            var runner = new CommandRunner(Interpreter, output: streamOutput, error: streamError, debug: Debug, quiet: Quiet);
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
