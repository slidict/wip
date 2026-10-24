using Wip.Configuration;
using Wip.Execution;
using Wip.Security;

namespace Wip.Cli;

internal sealed partial class CliContext
{
    /// <summary>
    /// Runs one registered scanner inside one declared sandbox, behind the boundary that
    /// sandbox declares.
    /// </summary>
    /// <remarks>
    /// The two phases stay two calls rather than one, because only the first may reach the
    /// network: preparation pulls the pinned image, and the run happens where it cannot. A
    /// failure in preparation therefore stops before anything is executed in the sandbox.
    /// </remarks>
    internal int SecurityScan(string sandbox, string tool, string target, int timeoutSeconds)
    {
        if (timeoutSeconds <= 0) throw new ConfigException("--timeout must be a positive number of seconds");
        // Both sinks to stderr: this command writes the report to stdout, and a scanner's
        // own progress arriving there would be mixed into it.
        var backend = ResourceBackend(streamOutput: Console.Error, streamError: Console.Error);
        var networks = new NetworkLifecycle(Config.SandboxResources, backend);
        var lifecycle = new SandboxLifecycle(Config.SandboxResources, backend, null, networks);
        var engine = new ScanEngine(Config.SandboxResources, lifecycle, networks, backend);

        var scanner = ScanEngine.Select(tool);
        var scanTarget = new ScanTarget(target);
        var plan = engine.Prepare(scanner, sandbox, scanTarget);
        var outcome = engine.Run(scanner, sandbox, plan, TimeSpan.FromSeconds(timeoutSeconds));

        // One line of provenance, then the report as the tool wrote it. The report is not
        // parsed or reformatted here: it is a tool's output, which the result format in
        // slidict/workspace#690 treats as untrusted input, and this command's job is to hand
        // it on unchanged.
        Console.Error.WriteLine(
            $"{outcome.Tool}\t{outcome.Sandbox}\t{outcome.Network}\t{outcome.Image}\trules={outcome.RulesVersion}\texit={outcome.ExitCode}");
        if (outcome.Report is { } report) Console.Out.Write(report);
        else Console.Error.WriteLine($"{outcome.Tool}: wrote no report at {plan.ReportPath}");

        // The tool's own status, forwarded: a scanner uses it to report findings, so
        // flattening it to 0 would hide what the scan found.
        return outcome.ExitCode;
    }

    /// <summary>The registered tool names, one per line, for a caller choosing one.</summary>
    internal int SecurityTools()
    {
        foreach (var name in ScanEngine.Tools) Console.WriteLine(name);
        if (ScanEngine.Tools.Count == 0)
            Console.Error.WriteLine("No scanners are registered in this build; a tool arrives by a reviewed pull request to wip.");
        return 0;
    }
}
