using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// Covers <see cref="CommandBuilder.DependencyExec"/>, added so a readiness check can run its
/// <c>test</c> inside a sidecar directly rather than through <see cref="CommandBuilder.Exec"/>,
/// which always targets the primary container.
/// </summary>
public class CommandBuilderHealthCheckTests
{
    [Fact]
    public void TargetsTheNamedDependencyRatherThanThePrimaryContainer()
    {
        var config = new Config(YamlLoader.LoadText("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
              db:
                image: mysql:8.0
            """, allowAliases: false));

        var builder = new CommandBuilder("wslc.exe", config, new FakeEnvironment());

        Assert.Equal(
            new[] { "wslc.exe", "exec", "db", "mysqladmin", "ping" },
            builder.DependencyExec("db", ["mysqladmin", "ping"]));
    }

    private sealed class FakeEnvironment : IEnvironment
    {
        public bool IsInteractive => false;

        public bool IsWsl2 => true;

        public string Architecture => "linux/amd64";
    }
}
