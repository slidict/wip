using System.Text;
using System.Diagnostics;
using Wip.Cli;
using Wip.Diagnostics;
using Wip.Execution;
using Wip.Security;

namespace Wip.Tests;

[CollectionDefinition("Console encoding", DisableParallelization = true)]
public sealed class ConsoleEncodingCollection;

[Collection("Console encoding")]
public sealed class SecurityOutputTests
{
    [Fact]
    public void CapturedStreamsExplicitlyUseUtf8()
    {
        var startInfo = new ProcessStartInfo();
        CommandRunner.ConfigureCapturedStreams(startInfo);
        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardOutputEncoding?.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardErrorEncoding?.CodePage);
    }

    [Fact]
    public void CapturedUtf8IgnoresTheConsoleCodePage()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The byte-producing fixture uses /bin/sh");
        var original = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.ASCII;
            var output = new StringWriter();
            var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), output, new StringWriter());
            Assert.Equal(0, runner.Run(["/bin/sh", "-c", "printf '\\343\\201\\202\\303\\251'"]));
            Assert.Equal("あé", output.ToString());
        }
        finally
        {
            Console.OutputEncoding = original;
        }
    }

    [Fact]
    public void TerminalReportsEscapeControlsAndBidiOverrides()
    {
        var output = new StringWriter();
        CliContext.WriteSecurityReport(output, "Title\n\u001b[2J\u001b]8;;fake\a\r\u0085\u009b\u009d\u202e", isOutputRedirected: false);
        Assert.Equal("Title\n\\u001b[2J\\u001b]8;;fake\\u0007\\u000d\\u0085\\u009b\\u009d\\u202e", output.ToString());
    }

    [Fact]
    public void RedirectedReportsPreserveTheOriginalText()
    {
        const string report = "あé\n\u001b[2J\r";
        var output = new StringWriter();
        CliContext.WriteSecurityReport(output, report, isOutputRedirected: true);
        Assert.Equal(report, output.ToString());
    }

    [Fact]
    public void TerminalReportsUseOneWriteInsteadOfOneWritePerCharacter()
    {
        var output = new CountingWriter();
        CliContext.WriteSecurityReport(output, new string('x', 1024 * 1024) + "\u001b", isOutputRedirected: false);
        Assert.Equal(1, output.Writes);
        Assert.EndsWith("\\u001b", output.ToString());
    }

    [Fact]
    public void ExcessiveTimeoutIsRejectedBeforePreparingAnImage()
    {
        var context = new CliContext(new CliOptions("missing-security-test-config.yml", null, false, null, false));
        var failure = Assert.Throws<ConfigException>(() => context.SecurityScan("unused", "unused", "unused", 99_999_999));
        Assert.Contains("--timeout", failure.Message);
        CliContext.ValidateSecurityTimeout(2_147_483);
    }

    [Fact]
    public void ToolExit124IsNotAnEnforcedTimeout()
    {
        var command = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", "exit /b 124" }
            : new[] { "/bin/sh", "-c", "exit 124" };
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), TextWriter.Null, TextWriter.Null);
        var result = runner.RunWithOutcome(command, timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(124, result.Code);
        Assert.False(result.TimedOut);
        Assert.Equal(124, runner.Run(command, timeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ExistingHealthProbeAcceptsLargeStderrAndPreservesTheLastLine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The large-output fixture uses /bin/sh");
        var context = new CliContext(new CliOptions(null, null, false, null, false));
        var result = context.Probe(
            ["/bin/sh", "-c", "head -c 1048577 /dev/zero | tr '\\000' x >&2; printf '\\nhealth-tail' >&2; exit 17"],
            timeout: TimeSpan.FromSeconds(5), captureStderr: true);
        Assert.Equal(17, result.Code);
        Assert.Equal(1_048_589, result.Output.Length);
        Assert.EndsWith("health-tail", CliContext.HealthCheckFailureMessage("db", 1, result.Code, result.Output));
    }

    [Fact]
    public void ReportReadFailurePreservesProvenanceAndTheScannerExitCode()
    {
        var outcome = new ScanOutcome("scanner", "scan", "image", "rules-v1", "isolated", 7, null,
            ReportError: "report exceeded the output limit");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(7, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, isOutputRedirected: true));
        Assert.Contains("scanner\tscan\tisolated\timage\trules=rules-v1\texit=7\tstatus=exited", error.ToString());
        Assert.Contains("report exceeded the output limit", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EnforcedDeadlineIsReportedOutOfBand()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The blocking fixture uses /bin/sh");
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), TextWriter.Null, TextWriter.Null);
        var command = new[] { "/bin/sh", "-c", "exec sleep 5" };
        var result = runner.RunWithOutcome(command, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Equal(CommandRunner.TimeoutExitCode, result.Code);
        Assert.True(result.TimedOut);
        Assert.Equal(CommandRunner.TimeoutExitCode, runner.Run(command, timeout: TimeSpan.FromMilliseconds(100)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedReportsPreserveStatusAndDiscardOverflowWithoutThrowing(bool timedOut)
    {
        var result = CliContext.CaptureProbeWithOutcome((output, _) =>
        {
            output.Write(new string('x', ScanEngine.MaxReportCharacters + 1));
            return (71, timedOut);
        }, boundedReport: true);
        Assert.Equal(71, result.Code);
        Assert.Equal(timedOut, result.TimedOut);
        Assert.True(result.OutputLimitExceeded);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void EmptyAlertsDoNotHideRawUnperformedChecks()
    {
        const string diagnostics = "Checks not performed: authentication\nNative warning: spider limit reached\n";
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 2, "{\"alerts\":[]}",
            UnperformedChecks: diagnostics);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        Assert.Equal(outcome.Report, output.ToString());
        Assert.Contains("Unperformed checks (raw):", error.ToString());
        Assert.Contains(diagnostics, error.ToString());
    }

    [Fact]
    public void MissingUnperformedCheckDiagnosticsAreExplicit()
    {
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 0, "{\"alerts\":[]}",
            UnperformedChecksError: "collection timed out");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        Assert.Contains("unperformed-check diagnostics unavailable: collection timed out", error.ToString());
    }

    /// <summary>
    /// An empty sidecar used to print its header with nothing under it, which reads as "no
    /// checks were skipped" -- the one reading this collection exists to prevent. The scanner
    /// asked for the file, so an empty one means the record was lost.
    /// </summary>
    [Fact]
    public void AnEmptyUnperformedCheckRecordIsReportedRatherThanShownAsNone()
    {
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 0, "{\"alerts\":[]}",
            UnperformedChecks: "");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        var written = error.ToString();
        Assert.DoesNotContain("Unperformed checks (raw):", written);
        Assert.Contains("came back empty", written);
    }

    private sealed class CountingWriter : StringWriter
    {
        internal int Writes { get; private set; }
        public override void Write(char value) { Writes++; base.Write(value); }
        public override void Write(string? value) { Writes++; base.Write(value); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneralProbePreservesLargeOutputAndTheExitCode(bool captureStderr)
    {
        var text = new string('x', 1_048_577);
        var result = CliContext.CaptureProbe((output, error) =>
        {
            (captureStderr ? error : output).Write(text);
            return 17;
        }, captureStderr);
        Assert.Equal(17, result.Code);
        Assert.Equal(text, result.Output);
    }
}
