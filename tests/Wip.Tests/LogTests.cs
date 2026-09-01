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
