using Wip.Configuration;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// Covers <c>dependencies.&lt;name&gt;.healthcheck:</c> under <c>mode: container</c>, where
/// <see cref="Config"/> normalizes it eagerly at load time via <see cref="HealthCheck"/> —
/// the same normalizer compose-native's own healthcheck: goes through (see
/// <see cref="ComposeFileHealthCheckTests"/>).
/// </summary>
public class ConfigHealthCheckTests
{
    [Fact]
    public void NormalizedHealthCheckIsReachableFromDependency()
    {
        var config = LoadConfig("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
              db:
                image: mysql:8.0
                healthcheck:
                  test: ["CMD", "mysqladmin", "ping"]
                  interval: 2
                  retries: 5
            """);

        var healthcheck = (OrderedDictionary<string, object?>)config.Dependency("db")!["healthcheck"]!;

        Assert.Equal(["mysqladmin", "ping"], ((List<object?>)healthcheck["test"]!).Cast<string>());
        Assert.Equal(2.0, healthcheck["interval"]);
        Assert.Equal(5L, healthcheck["retries"]);
    }

    [Fact]
    public void DependencyWithNoHealthCheckDefaultsToNull()
    {
        var config = LoadConfig("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
            """);

        Assert.Null(config.Dependency("app")!["healthcheck"]);
    }

    [Fact]
    public void InvalidHealthCheckFailsAtConfigLoadTime()
    {
        var exception = Assert.Throws<ConfigException>(() => LoadConfig("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
              db:
                image: mysql:8.0
                healthcheck:
                  interval: 2
            """));

        Assert.Contains("dependencies.db.healthcheck.test", exception.Message);
    }

    [Fact]
    public void NoneTestDisablesTheHealthCheck()
    {
        var config = LoadConfig("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
              db:
                image: mysql:8.0
                healthcheck:
                  test: NONE
            """);

        Assert.Null(config.Dependency("db")!["healthcheck"]);
    }

    private static Config LoadConfig(string yaml) =>
        new(YamlLoader.LoadText(yaml, allowAliases: false));
}
