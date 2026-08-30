using Wip.Configuration;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// Covers <see cref="HealthCheck.Normalize"/> directly — the parsing and validation shared by
/// <c>dependencies.&lt;name&gt;.healthcheck:</c> (mode: container) and a compose.yml service's
/// own <c>healthcheck:</c> (mode: compose-native).
/// </summary>
public class HealthCheckTests
{
    [Fact]
    public void NullIsNoHealthCheck() =>
        Assert.Null(HealthCheck.Normalize("dependencies.db.healthcheck", null));

    [Fact]
    public void StringTestIsShellForm()
    {
        var result = HealthCheck.Normalize("dependencies.db.healthcheck", Load("""
            test: mysqladmin ping -h localhost
            """));

        Assert.Equal(["sh", "-c", "mysqladmin ping -h localhost"], Argv(result));
    }

    [Fact]
    public void CmdArrayIsUsedAsIs()
    {
        var result = HealthCheck.Normalize("dependencies.db.healthcheck", Load("""
            test: ["CMD", "mysqladmin", "ping", "-h", "localhost"]
            """));

        Assert.Equal(["mysqladmin", "ping", "-h", "localhost"], Argv(result));
    }

    [Fact]
    public void CmdShellArrayBecomesAShellInvocation()
    {
        var result = HealthCheck.Normalize("dependencies.db.healthcheck", Load("""
            test: ["CMD-SHELL", "mysqladmin ping || exit 1"]
            """));

        Assert.Equal(["sh", "-c", "mysqladmin ping || exit 1"], Argv(result));
    }

    [Theory]
    [InlineData("test: NONE")]
    [InlineData("""test: ["NONE"]""")]
    public void NoneDisablesTheHealthCheck(string yaml) =>
        Assert.Null(HealthCheck.Normalize("dependencies.db.healthcheck", Load(yaml)));

    [Fact]
    public void DefaultsAreApplied()
    {
        var result = HealthCheck.Normalize("dependencies.db.healthcheck", Load("""
            test: ["CMD", "true"]
            """));

        Assert.Equal(HealthCheck.DefaultInterval, result!["interval"]);
        Assert.Equal(HealthCheck.DefaultTimeout, result["timeout"]);
        Assert.Equal(HealthCheck.DefaultRetries, result["retries"]);
        Assert.Equal(HealthCheck.DefaultStartPeriod, result["start_period"]);
    }

    [Fact]
    public void ExplicitTimingOverridesDefaults()
    {
        var result = HealthCheck.Normalize("dependencies.db.healthcheck", Load("""
            test: ["CMD", "true"]
            interval: 2
            timeout: 5
            retries: 10
            start_period: 30
            """));

        Assert.Equal(2.0, result!["interval"]);
        Assert.Equal(5.0, result["timeout"]);
        Assert.Equal(10L, result["retries"]);
        Assert.Equal(30.0, result["start_period"]);
    }

    [Fact]
    public void MissingTestIsRejected()
    {
        var exception = Assert.Throws<ConfigException>(() =>
            HealthCheck.Normalize("dependencies.db.healthcheck", Load("interval: 2")));

        Assert.Contains("test", exception.Message);
    }

    [Fact]
    public void UnsupportedKeyIsRejected()
    {
        var exception = Assert.Throws<ConfigException>(() => HealthCheck.Normalize("dependencies.db.healthcheck", Load("""
            test: ["CMD", "true"]
            disable: true
            """)));

        Assert.Contains("disable", exception.Message);
    }

    [Fact]
    public void CmdWithNoArgumentsIsRejected()
    {
        var exception = Assert.Throws<ConfigException>(() =>
            HealthCheck.Normalize("dependencies.db.healthcheck", Load("""test: ["CMD"]""")));

        Assert.Contains("CMD needs at least one argument", exception.Message);
    }

    [Fact]
    public void CmdShellWithExtraArgumentsIsRejected()
    {
        var exception = Assert.Throws<ConfigException>(() => HealthCheck.Normalize(
            "dependencies.db.healthcheck", Load("""test: ["CMD-SHELL", "one", "two"]""")));

        Assert.Contains("CMD-SHELL takes exactly one command", exception.Message);
    }

    [Fact]
    public void UnknownArrayLeaderIsRejected()
    {
        var exception = Assert.Throws<ConfigException>(() =>
            HealthCheck.Normalize("dependencies.db.healthcheck", Load("""test: ["EXEC", "true"]""")));

        Assert.Contains("must start with CMD, CMD-SHELL, or NONE", exception.Message);
    }

    [Theory]
    [InlineData("interval: 0")]
    [InlineData("interval: -1")]
    public void NonPositiveIntervalIsRejected(string yaml)
    {
        var exception = Assert.Throws<ConfigException>(() => HealthCheck.Normalize(
            "dependencies.db.healthcheck", Load($"test: [\"CMD\", \"true\"]\n{yaml}")));

        Assert.Contains("interval must be a positive number", exception.Message);
    }

    [Fact]
    public void NegativeStartPeriodIsRejected()
    {
        var exception = Assert.Throws<ConfigException>(() => HealthCheck.Normalize(
            "dependencies.db.healthcheck", Load("""
            test: ["CMD", "true"]
            start_period: -1
            """)));

        Assert.Contains("start_period must not be negative", exception.Message);
    }

    [Theory]
    [InlineData("10s", 10.0)]
    [InlineData("1m30s", 90.0)]
    [InlineData("1h", 3600.0)]
    [InlineData("1h5m30s", 3930.0)]
    [InlineData("500ms", 0.5)]
    public void ComposeDurationStringsAreParsedAsSeconds(string duration, double seconds)
    {
        var result = HealthCheck.Normalize("dependencies.db.healthcheck", Load($"""
            test: ["CMD", "true"]
            interval: "{duration}"
            """));

        Assert.Equal(seconds, result!["interval"]);
    }

    [Fact]
    public void UnparseableIntervalIsRejected()
    {
        var exception = Assert.Throws<ConfigException>(() => HealthCheck.Normalize(
            "dependencies.db.healthcheck", Load("""
            test: ["CMD", "true"]
            interval: "10 seconds"
            """)));

        Assert.Contains("interval must be a number of seconds or a duration string", exception.Message);
    }

    [Fact]
    public void RetriesRejectsADurationString()
    {
        var exception = Assert.Throws<ConfigException>(() => HealthCheck.Normalize(
            "dependencies.db.healthcheck", Load("""
            test: ["CMD", "true"]
            retries: "10s"
            """)));

        Assert.Contains("retries must be a whole number", exception.Message);
    }

    private static object? Load(string yaml) => YamlLoader.LoadText(yaml, allowAliases: false);

    private static List<string> Argv(OrderedDictionary<string, object?>? result) =>
        ((List<object?>)result!["test"]!).Select(item => (string)item!).ToList();
}
