using Wip.Diagnostics;
using Wip.Execution;

namespace Wip.Tests;

/// <summary>
/// Exercises quiet mode (issue #134's point 4) against a real child process rather than a
/// fake: the behaviour under test is exactly the difference between what
/// <see cref="CommandRunner.Run"/> streams live versus holds back, which a mock of the process
/// I/O would define away rather than verify.
/// </summary>
public class CommandRunnerQuietTests
{
    private static readonly ErrorInterpreter Interpreter = new("linux/amd64");

    [Fact]
    public void QuietSuccessPrintsNothing()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CommandRunner(Interpreter, output, error, quiet: true);

        var code = runner.Run(Cmd("echo hello & exit /b 0"));

        Assert.Equal(0, code);
        Assert.Equal("", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void QuietFailureReleasesTheHeldOutput()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CommandRunner(Interpreter, output, error, quiet: true);

        var code = runner.Run(Cmd("echo hello & exit /b 3"));

        Assert.Equal(3, code);
        Assert.Contains("hello", output.ToString());
    }

    [Fact]
    public void NonQuietSuccessStillStreamsLive()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CommandRunner(Interpreter, output, error);

        var code = runner.Run(Cmd("echo hello & exit /b 0"));

        Assert.Equal(0, code);
        Assert.Contains("hello", output.ToString());
    }

    private static IReadOnlyList<string> Cmd(string script) => ["cmd.exe", "/c", script];
}
