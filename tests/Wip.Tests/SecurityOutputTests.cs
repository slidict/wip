using System.Text;
using System.Diagnostics;
using Wip.Cli;
using Wip.Diagnostics;
using Wip.Execution;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedProbeOutputIsRejectedInsteadOfReturningAPartialReport(bool captureStderr)
    {
        var consumed = false;
        Assert.Throws<WipException>(() => CliContext.CaptureProbe((output, error) =>
        {
            var destination = captureStderr ? error : output;
            for (var index = 0; index < 257; index++) destination.Write(new string('x', 4096));
            consumed = true;
            return 0;
        }, captureStderr));
        Assert.True(consumed);
    }
}
