using Wip.Compose;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// Covers <c>healthcheck:</c> parsing and <c>depends_on: condition: service_healthy</c> on a
/// real compose.yml, both new to <see cref="ComposeFile"/> alongside the readiness-check
/// feature. <see cref="HealthCheckTests"/> already covers the shared normalizer in isolation.
/// </summary>
public class ComposeFileHealthCheckTests
{
    [Fact]
    public void HealthCheckFlowsIntoTheDependenciesMapping()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteCompose(directory.Path, """
            services:
              db:
                image: mysql:8.0
                healthcheck:
                  test: ["CMD", "mysqladmin", "ping"]
                  interval: 2
                  retries: 5
            """);

        var compose = ComposeFile.Load(path);
        var db = (OrderedDictionary<string, object?>)compose.ToDependenciesMapping()["db"]!;
        var healthcheck = (OrderedDictionary<string, object?>)db["healthcheck"]!;

        Assert.Equal(["mysqladmin", "ping"], ((List<object?>)healthcheck["test"]!).Cast<string>());
        Assert.Equal(2.0, healthcheck["interval"]);
        Assert.Equal(5L, healthcheck["retries"]);
    }

    [Fact]
    public void ServiceWithNoHealthCheckHasANullEntry()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteCompose(directory.Path, """
            services:
              app:
                image: myapp:dev
            """);

        var compose = ComposeFile.Load(path);
        var app = (OrderedDictionary<string, object?>)compose.ToDependenciesMapping()["app"]!;

        Assert.Null(app["healthcheck"]);
    }

    [Fact]
    public void ServiceHealthyConditionIsAcceptedWhenTheDependencyHasAHealthCheck()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteCompose(directory.Path, """
            services:
              db:
                image: mysql:8.0
                healthcheck:
                  test: ["CMD", "mysqladmin", "ping"]
              web:
                image: myapp:dev
                depends_on:
                  db:
                    condition: service_healthy
            """);

        var compose = ComposeFile.Load(path);

        Assert.Equal(["db", "web"], compose.ServiceNamesInDependencyOrder);
    }

    [Fact]
    public void ServiceHealthyConditionIsRejectedWithoutAHealthCheck()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteCompose(directory.Path, """
            services:
              db:
                image: mysql:8.0
              web:
                image: myapp:dev
                depends_on:
                  db:
                    condition: service_healthy
            """);

        var exception = Assert.Throws<ConfigException>(() => ComposeFile.Load(path));

        Assert.Contains("service_healthy", exception.Message);
        Assert.Contains("no healthcheck", exception.Message);
    }

    [Fact]
    public void UnsupportedConditionIsStillRejected()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteCompose(directory.Path, """
            services:
              db:
                image: mysql:8.0
              web:
                image: myapp:dev
                depends_on:
                  db:
                    condition: service_completed_successfully
            """);

        var exception = Assert.Throws<ConfigException>(() => ComposeFile.Load(path));

        Assert.Contains("condition 'service_completed_successfully' is not supported", exception.Message);
    }

    [Fact]
    public void ArrayFormDependsOnDefaultsToServiceStarted()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteCompose(directory.Path, """
            services:
              db:
                image: mysql:8.0
              web:
                image: myapp:dev
                depends_on:
                  - db
            """);

        var compose = ComposeFile.Load(path);

        Assert.Equal(["db", "web"], compose.ServiceNamesInDependencyOrder);
    }

    private static string WriteCompose(string directory, string contents)
    {
        var path = Path.Combine(directory, "compose.yml");
        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-test-").FullName;

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
