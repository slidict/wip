using System.Text;
using System.Text.Json;
using Wip.Configuration;
using Wip.Diagnostics;
using Wip.Execution;
using Wip.Security;
using Wip.Yaml;

// This test executable drives the real engine without adding a fixture to the product registry.
if (args.Length != 5)
{
    Console.Error.WriteLine("usage: scan-fixture <config> <image-digest> <target-url> <fixture-script> <outcome-file>");
    return 64;
}
try
{
    var config = new Config(YamlLoader.LoadText(File.ReadAllText(args[0]), allowAliases: false), args[0]);
    var executable = new CommandResolver().Resolve(config.WslcCommand);
    SandboxCommandResult Backend(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode mode)
    {
        var capture = mode is SandboxConsoleMode.Capture or SandboxConsoleMode.CaptureReport;
        using var buffer = new CaptureWriter(mode == SandboxConsoleMode.CaptureReport);
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"),
            capture ? buffer : Console.Error, Console.Error);
        var result = runner.RunWithOutcome([executable, .. argv], timeout: timeout);
        return new(result.Code, capture && !buffer.Exceeded ? buffer.ToString() : "")
        {
            TimedOut = result.TimedOut,
            OutputLimitExceeded = buffer.Exceeded
        };
    }
    var networks = new NetworkLifecycle(config.SandboxResources, Backend);
    var lifecycle = new SandboxLifecycle(config.SandboxResources, Backend, null, networks);
    var engine = new ScanEngine(config.SandboxResources, lifecycle, networks, Backend);
    var script = File.ReadAllText(args[3]).Replace("\r\n", "\n");
    // Plan argv rejects literal control characters. printf %b restores the reviewed fixture
    // script from a separate escaped argument; target data is never interpolated into code.
    var transported = script.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t");
    var scanner = new FixtureScanner(args[1], transported);
    var plan = engine.Prepare(scanner, "engine-scan-scanner", new ScanTarget(args[2]));
    var outcome = engine.Run(scanner, "engine-scan-scanner", plan, new ScanTarget(args[2]), TimeSpan.FromSeconds(90));
    using (var stream = File.Create(args[4]))
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        writer.WriteNumber("ExitCode", outcome.ExitCode);
        writer.WriteBoolean("TimedOut", outcome.TimedOut);
        writer.WriteString("Report", outcome.Report);
        writer.WriteString("ReportError", outcome.ReportError);
        writer.WriteString("UnperformedChecks", outcome.UnperformedChecks);
        writer.WriteString("UnperformedChecksError", outcome.UnperformedChecksError);
        writer.WriteEndObject();
        writer.Flush();
    }
    return outcome.ProcessExitCode;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

sealed class FixtureScanner(string image, string script) : ISecurityScanner
{
    public string Name => "e2e-fixture";
    public IReadOnlyList<ScanFinding> ConvertReport(string report)
    {
        using var document = JsonDocument.Parse(report);
        if (!document.RootElement.TryGetProperty("target_http_completed", out var completed) ||
            completed.ValueKind != JsonValueKind.True)
            throw new FormatException("The HTTP fixture did not complete");
        return [];
    }
    public ScanPlan Plan(ScanTarget target) => new(new PinnedImage(image),
        ["sh", "-c", "printf '%b' \"$1\" > /tmp/wip-scan-fixture.sh; shift; exec sh /tmp/wip-scan-fixture.sh \"$@\"",
         "wip-scan-fixture", script, target.Address, "/tmp/wip-scan-report.json", "/tmp/wip-scan-unperformed.txt"],
        "/tmp/wip-scan-report.json", "fixture-v1", "/tmp/wip-scan-unperformed.txt");
}

sealed class CaptureWriter(bool bounded) : TextWriter
{
    private readonly StringBuilder buffer = new();
    public bool Exceeded { get; private set; }
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) => Write(value.ToString());
    public override void Write(string? value)
    {
        if (value is null) return;
        var retained = bounded ? Math.Min(value.Length, 1024 * 1024 - buffer.Length) : value.Length;
        buffer.Append(value.AsSpan(0, retained));
        Exceeded |= retained != value.Length;
    }
    public override string ToString() => buffer.ToString();
}
