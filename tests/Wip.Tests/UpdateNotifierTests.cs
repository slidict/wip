using Wip.Diagnostics;

namespace Wip.Tests;

public class UpdateNotifierTests
{
    [Fact]
    public void FindsHighestVersionInWinGetDirectoryListing()
    {
        const string response = """
            [
              { "name": "2.4.9", "type": "dir" },
              { "name": "not-a-version", "type": "file" },
              { "name": "2.10.0", "type": "dir" },
              { "name": "2.5.2", "type": "dir" }
            ]
            """;

        Assert.Equal("2.10.0", UpdateNotifier.FindLatestVersion(response));
    }

    [Theory]
    [InlineData("2.5.3", "2.5.2", true)]
    [InlineData("2.5.2", "2.5.2", false)]
    [InlineData("2.5.1", "2.5.2", false)]
    [InlineData("v3.0.0", "2.5.2+build", true)]
    [InlineData("invalid", "2.5.2", false)]
    public void ComparesVersions(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateNotifier.IsNewer(candidate, current));
    }

    [Fact]
    public void FormatsProminentPlainUpdateNotice()
    {
        var actual = Log.FormatUpdateAvailable("2.5.2", "2.6.0", colorize: false);

        Assert.Contains("*** WIP UPDATE AVAILABLE ***", actual);
        Assert.Contains("2.5.2 -> 2.6.0", actual);
        Assert.Contains("winget upgrade --id Slidict.Wip --exact", actual);
        Assert.DoesNotContain('\x1b', actual);
    }

    [Fact]
    public void HighlightsUpdateHeadingAndCommandWhenColorEnabled()
    {
        var actual = Log.FormatUpdateAvailable("2.5.2", "2.6.0", colorize: true);

        Assert.Contains("\x1b[1;35m*** WIP UPDATE AVAILABLE ***\x1b[0m", actual);
        Assert.Contains("\x1b[1;35mwinget upgrade --id Slidict.Wip --exact\x1b[0m", actual);
    }
}
