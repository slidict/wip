using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// Covers <see cref="CommandBuilder.SystemSessionTerminate"/>, the one command here that takes
/// no argument at all: unlike every other builder method, it isn't scoped to a container or
/// network this <c>wip.yml</c> names.
/// </summary>
public class CommandBuilderSystemSessionTests
{
    [Fact]
    public void TerminatesTheSessionWithNoProjectSpecificArgument()
    {
        var config = new Config(YamlLoader.LoadText("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
            """, allowAliases: false));

        var builder = new CommandBuilder("wslc.exe", config, new FakeEnvironment());

        Assert.Equal(
            new[] { "wslc.exe", "system", "session", "terminate" },
            builder.SystemSessionTerminate());
    }

    private sealed class FakeEnvironment : IEnvironment
    {
        public bool IsInteractive => false;

        public bool IsWsl2 => true;

        public string Architecture => "linux/amd64";
    }
}
