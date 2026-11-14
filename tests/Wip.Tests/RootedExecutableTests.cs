using Wip.Diagnostics;
using Wip.Execution;
using Wip.Platform;

namespace Wip.Tests;

/// <summary>
/// A bare executable name lets CreateProcess search the current directory ahead of System32,
/// so the process starters refuse one instead of reporting it as a failed run.
/// </summary>
public class RootedExecutableTests
{
    private static readonly string Missing =
        Path.Combine(Path.GetTempPath(), "wip-missing-" + Guid.NewGuid().ToString("N"), "wsl.exe");

    [Fact]
    public void ProbeRefusesABareName()
    {
        Assert.Throws<ArgumentException>(() => ProcessProbe.Succeeds("wsl.exe", ["--status"]));
    }

    [Fact]
    public void ProbeReportsAMissingRootedPathAsFalse()
    {
        Assert.False(ProcessProbe.Succeeds(Missing, ["--status"]));
    }

    [Fact]
    public void RunnerRefusesABareName()
    {
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), TextWriter.Null, TextWriter.Null);
        Assert.Throws<ArgumentException>(() => runner.Run(["git", "--version"]));
    }

    [Fact]
    public void RunnerRefusesAnEmptyCommand()
    {
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), TextWriter.Null, TextWriter.Null);
        Assert.Throws<ArgumentException>(() => runner.Run([]));
    }
}
