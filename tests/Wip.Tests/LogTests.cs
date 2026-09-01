using Wip.Diagnostics;

namespace Wip.Tests;

public class LogTests
{
    [Fact]
    public void FormatsPlainWhenColorDisabled()
    {
        var actual = Log.Format("container 'app' not found, creating it", colorize: false);

        Assert.Equal("wip: container 'app' not found, creating it", actual);
    }

    [Fact]
    public void TintsOnlyTheTagWhenColorEnabled()
    {
        var actual = Log.Format("container 'app' not found, creating it", colorize: true);

        Assert.Equal("\x1b[36mwip:\x1b[0m container 'app' not found, creating it", actual);
    }

    [Fact]
    public void WarnAddsAPlainWarningLabelWhenColorDisabled()
    {
        var actual = Log.FormatWarn("this project is on the WSL filesystem", colorize: false);

        Assert.Equal("wip: warning: this project is on the WSL filesystem", actual);
    }

    [Fact]
    public void WarnTintsTheTagAndTheLabelSeparatelyWhenColorEnabled()
    {
        var actual = Log.FormatWarn("this project is on the WSL filesystem", colorize: true);

        Assert.Equal(
            "\x1b[36mwip:\x1b[0m \x1b[33mwarning:\x1b[0m this project is on the WSL filesystem",
            actual);
    }

    [Fact]
    public void ErrorAddsAPlainErrorLabelWhenColorDisabled()
    {
        var actual = Log.FormatError("up failed (exit code 1)", colorize: false);

        Assert.Equal("wip: error: up failed (exit code 1)", actual);
    }

    [Fact]
    public void ErrorTintsTheTagAndTheLabelSeparatelyWhenColorEnabled()
    {
        var actual = Log.FormatError("up failed (exit code 1)", colorize: true);

        Assert.Equal("\x1b[36mwip:\x1b[0m \x1b[31merror:\x1b[0m up failed (exit code 1)", actual);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void NoColorEnvironmentVariableAlwaysWins(bool noColorRequested, bool isRealConsole, bool expected)
    {
        var actual = Log.ShouldColorize(noColorRequested, isRealConsole);

        Assert.Equal(expected, actual);
    }
}
