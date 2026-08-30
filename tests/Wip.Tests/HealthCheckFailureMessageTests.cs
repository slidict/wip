using Wip.Cli;
using Wip.Execution;

namespace Wip.Tests;

/// <summary>
/// Covers the pure formatting <see cref="CliContext.WaitForHealthy"/> falls back on once a
/// dependency's healthcheck exhausts its retries — the message a user actually sees when
/// <c>wip up</c> gives up, per issue #118's "fail with a clear error" requirement.
/// </summary>
public class HealthCheckFailureMessageTests
{
    [Fact]
    public void ReportsANonZeroExitCode()
    {
        var message = CliContext.HealthCheckFailureMessage("db", failures: 4, code: 1, output: "mysqladmin: connect failed\n");

        Assert.Equal("dependency 'db' did not become healthy after 4 attempt(s) (last check exited 1): mysqladmin: connect failed", message);
    }

    [Fact]
    public void ReportsATimeout()
    {
        var message = CliContext.HealthCheckFailureMessage("db", failures: 3, code: CommandRunner.TimeoutExitCode, output: "");

        Assert.Equal("dependency 'db' did not become healthy after 3 attempt(s) (last check timed out)", message);
    }

    [Fact]
    public void LastLineIgnoresTrailingBlankLines()
    {
        Assert.Equal("connect failed", CliContext.LastLine("connecting...\nconnect failed\n\n"));
    }

    [Fact]
    public void LastLineIsNullForEmptyOutput() =>
        Assert.Null(CliContext.LastLine(""));
}
