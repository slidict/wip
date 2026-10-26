using System.Globalization;
using System.Text;
using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;
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
    internal int SecurityScan(string sandbox, string tool, string target, int timeoutSeconds,
        string? outputDirectory = null, string? commit = null)
    {
        ValidateSecurityTimeout(timeoutSeconds);
        // Before Prepare, which requests a pinned image of about a gigabyte. The engine
        // checks this again for callers that reach Run directly.
        if (outputDirectory is not null) ScanResultStore.RequireUsable(outputDirectory);
        // Both sinks to stderr: this command writes the report to stdout, and a scanner's
        // own progress arriving there would be mixed into it.
        var backend = ResourceBackend(streamOutput: Console.Error, streamError: Console.Error);
        var networks = new NetworkLifecycle(Config.SandboxResources, backend);
        var lifecycle = new SandboxLifecycle(Config.SandboxResources, backend, null, networks);
        // Constructing the fetcher is lazy: adapters with image-bundled rules neither touch
        // this cache nor require git. Apply the common host-path guard only when copying rules.
        var rulesFetcher = new RulesFetcher(Path.Combine(ConfigDirectory, ".wip", "scanner-rules"));
        var engine = new ScanEngine(Config.SandboxResources, lifecycle, networks, backend,
            rulesFetcher: rulesFetcher,
            mapRulesSource: source => WslPath.ForWslc(Path.GetFullPath(source), "the pinned scanner rules source"));

        var scanner = ScanEngine.Select(tool);
        var scanTarget = new ScanTarget(target);
        var plan = engine.Prepare(scanner, sandbox, scanTarget);
        var outcome = engine.Run(scanner, sandbox, plan, scanTarget, TimeSpan.FromSeconds(timeoutSeconds),
            outputDirectory, commit);

        return WriteScanOutcome(outcome, plan.ReportPath, Console.Out, Console.Error, Console.IsOutputRedirected, Console.IsErrorRedirected);
    }

    internal static int WriteScanOutcome(ScanOutcome outcome, string reportPath, TextWriter output, TextWriter error, bool isOutputRedirected, bool isErrorRedirected = false)
    {
        // Reports are untrusted tool output. Preserve redirected artifacts, but render
        // terminal controls visibly when displaying a report to a person.
        error.WriteLine(
            $"{outcome.Tool}\t{outcome.Sandbox}\t{outcome.Network}\t{outcome.Image}\trules={outcome.RulesVersion}\texit={outcome.ExitCode}\tstatus={(outcome.TimedOut ? "timeout" : "exited")}");
        if (outcome.Report is { } report) WriteSecurityReport(output, report, isOutputRedirected);
        else if (outcome.TimedOut) error.WriteLine($"{outcome.Tool}: timed out; requested sandbox stop without collecting a partial report");
        else if (outcome.ReportError is { } reason) error.WriteLine($"{outcome.Tool}: {reason}");
        else error.WriteLine($"{outcome.Tool}: wrote no report at {reportPath}");
        // An empty sidecar is reported, not printed as a bare header. A header with nothing
        // under it reads as "nothing was skipped", which is the one reading this collection
        // exists to prevent: the scanner asked for the file, so an empty one means the record
        // was lost rather than that every check ran. Whitespace counts as empty, because a
        // header followed by a blank line reads the same way.
        if (!string.IsNullOrWhiteSpace(outcome.UnperformedChecks))
        {
            error.WriteLine("Unperformed checks (raw):");
            WriteSecurityReport(error, outcome.UnperformedChecks, isErrorRedirected);
            if (!outcome.UnperformedChecks.EndsWith('\n')) error.WriteLine();
        }
        else if (outcome.UnperformedChecksError is { } diagnosticsError)
            error.WriteLine($"{outcome.Tool}: unperformed-check diagnostics unavailable: {diagnosticsError}");
        else if (outcome.UnperformedChecks is not null)
            error.WriteLine($"{outcome.Tool}: the unperformed-check record came back empty; which checks were skipped is unknown");
        if (outcome.Result is { Status: "incomplete" } result)
            error.WriteLine($"{outcome.Tool}: scan result incomplete: {result.IncompleteReason}");
        // A retried run leaves several run directories under --output; this names the one
        // that belongs to this invocation without reading result.json to find its run id.
        if (outcome.ResultDirectory is { } resultDirectory)
        {
            // The path is the caller's --output echoed back, so it goes through the same
            // escaping as every other variable text here rather than reaching a terminal raw.
            error.Write($"{outcome.Tool}: result saved to ");
            WriteSecurityReport(error, resultDirectory, isErrorRedirected);
            error.WriteLine();
        }
        if (outcome.PersistenceError is { } persistenceError)
        {
            error.Write($"{outcome.Tool}: result persistence failed: ");
            WriteSecurityReport(error, persistenceError, isErrorRedirected);
            error.WriteLine();
        }
        return outcome.ProcessExitCode;
    }

    internal static void ValidateSecurityTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds <= 0 || (long)timeoutSeconds * 1000 > int.MaxValue)
            throw new ConfigException("--timeout must be positive and at most 2147483 seconds");
    }

    internal static void WriteSecurityReport(TextWriter output, string report, bool isOutputRedirected)
    {
        if (isOutputRedirected)
        {
            output.Write(report);
            return;
        }

        var rendered = new StringBuilder(report.Length);
        foreach (var character in report)
        {
            if ((char.IsControl(character) && character is not ('\n' or '\t')) ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format)
                rendered.Append($"\\u{(int)character:x4}");
            else rendered.Append(character);
        }
        output.Write(rendered.ToString());
    }

    internal static (int Code, string Output) CaptureProbe(Func<TextWriter, TextWriter, int> run, bool captureStderr = false)
    {
        var result = CaptureProbeWithOutcome((output, error) => (run(output, error), false), captureStderr);
        return (result.Code, result.Output);
    }

    // Only reports have a cap. Ordinary metadata and health probes retain their existing
    // full output. Keep draining after overflow, preserving the exit code and timeout flag.
    internal static SandboxCommandResult CaptureProbeWithOutcome(
        Func<TextWriter, TextWriter, (int Code, bool TimedOut)> run,
        bool captureStderr = false,
        bool boundedReport = false)
    {
        using TextWriter captured = boundedReport ? new BoundedCaptureWriter() : new StringWriter();
        var output = TextWriter.Synchronized(captured);
        var error = captureStderr ? output : TextWriter.Null;
        var result = run(output, error);
        var exceeded = captured is BoundedCaptureWriter { Exceeded: true };
        return new(result.Code, exceeded ? "" : captured.ToString()!)
        {
            TimedOut = result.TimedOut,
            OutputLimitExceeded = exceeded
        };
    }

    private sealed class BoundedCaptureWriter : TextWriter
    {
        private const int Limit = ScanEngine.MaxReportCharacters;
        private readonly StringBuilder buffer = new();
        internal bool Exceeded { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (buffer.Length < Limit) buffer.Append(value);
            else Exceeded = true;
        }
        public override void Write(string? value)
        {
            if (value is null) return;
            var retained = Math.Min(value.Length, Limit - buffer.Length);
            buffer.Append(value.AsSpan(0, retained));
            Exceeded |= retained < value.Length;
        }
        public override string ToString() => buffer.ToString();
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
