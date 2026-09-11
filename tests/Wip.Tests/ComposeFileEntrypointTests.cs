using Wip.Compose;
using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;
using Wip.Yaml;

namespace Wip.Tests;

public class ComposeFileEntrypointTests
{
    [Theory]
    [InlineData("entrypoint: /usr/local/bin/start", "/usr/local/bin/start")]
    [InlineData("entrypoint: [\"/usr/local/bin/start\", \"--verbose\"]", "/usr/local/bin/start --verbose")]
    public void EntrypointIsNormalizedAndPassedToWslcRun(string entrypointYaml, string expected)
    {
        using var directory = new TemporaryDirectory();
        var composePath = Path.Combine(directory.Path, "compose.yml");
        File.WriteAllText(composePath, $$"""
            services:
              app:
                image: myapp:dev
                {{entrypointYaml}}
                command: serve
            """);

        var compose = ComposeFile.Load(composePath);
        var app = (OrderedDictionary<string, object?>)compose.ToDependenciesMapping()["app"]!;
        Assert.Equal(expected, app["entrypoint"]);

        var config = new Config(YamlLoader.LoadText($$"""
            mode: compose-native
            compose:
              file: {{composePath}}
              service: app
            """, allowAliases: false), Path.Combine(directory.Path, "wip.yml"));
        var builder = new CommandBuilder("wslc.exe", config, new FakeEnvironment());

        var expectedEntrypoint = Shellwords.Split(expected);
        var expectedCommand = new List<string>
        {
            "wslc.exe", "run", "--name", "app", "--network", directory.Name, "-d", "--entrypoint",
            expectedEntrypoint[0], "myapp:dev",
        };
        expectedCommand.AddRange(expectedEntrypoint.Skip(1));
        expectedCommand.Add("serve");
        Assert.Equal(
            expectedCommand,
            builder.Up(detach: true));
    }

    [Fact]
    public void ExecFormEntrypointWithoutCommandLeavesTheImageCmdIntact()
    {
        using var directory = new TemporaryDirectory();
        var composePath = Path.Combine(directory.Path, "compose.yml");
        File.WriteAllText(composePath, """
            services:
              app:
                image: myapp:dev
                entrypoint: ["/usr/local/bin/start", "--verbose"]
            """);

        var config = new Config(YamlLoader.LoadText($$"""
            mode: compose-native
            compose:
              file: {{composePath}}
              service: app
            """, allowAliases: false), Path.Combine(directory.Path, "wip.yml"));
        var builder = new CommandBuilder("wslc.exe", config, new FakeEnvironment());

        // No command: means no argv, so wslc (like Docker) must fall back to the image's own
        // CMD for the new entrypoint's arguments -- "--verbose" must not be spliced in here,
        // or it would occupy that slot and suppress the image's CMD.
        Assert.Equal(
            [
                "wslc.exe", "run", "--name", "app", "--network", directory.Name, "-d",
                "--entrypoint", "/usr/local/bin/start", "myapp:dev",
            ],
            builder.Up(detach: true));
    }

    [Theory]
    [InlineData("entrypoint: \"\"")]
    [InlineData("entrypoint: []")]
    public void ExplicitEmptyEntrypointClearsTheImageEntrypoint(string entrypointYaml)
    {
        using var directory = new TemporaryDirectory();
        var composePath = Path.Combine(directory.Path, "compose.yml");
        File.WriteAllText(composePath, $$"""
            services:
              app:
                image: myapp:dev
                {{entrypointYaml}}
                command: serve
            """);

        var compose = ComposeFile.Load(composePath);
        var app = (OrderedDictionary<string, object?>)compose.ToDependenciesMapping()["app"]!;
        Assert.Equal("", app["entrypoint"]);

        var config = new Config(YamlLoader.LoadText($$"""
            mode: compose-native
            compose:
              file: {{composePath}}
              service: app
            """, allowAliases: false), Path.Combine(directory.Path, "wip.yml"));
        var builder = new CommandBuilder("wslc.exe", config, new FakeEnvironment());

        Assert.Equal(
            [
                "wslc.exe", "run", "--name", "app", "--network", directory.Name, "-d",
                "--entrypoint", "", "myapp:dev", "serve",
            ],
            builder.Up(detach: true));
    }

    [Fact]
    public void OmittedEntrypointLeavesTheImageEntrypointUntouched()
    {
        using var directory = new TemporaryDirectory();
        var composePath = Path.Combine(directory.Path, "compose.yml");
        File.WriteAllText(composePath, """
            services:
              app:
                image: myapp:dev
                command: serve
            """);

        var compose = ComposeFile.Load(composePath);
        var app = (OrderedDictionary<string, object?>)compose.ToDependenciesMapping()["app"]!;
        Assert.False(app.ContainsKey("entrypoint"));

        var config = new Config(YamlLoader.LoadText($$"""
            mode: compose-native
            compose:
              file: {{composePath}}
              service: app
            """, allowAliases: false), Path.Combine(directory.Path, "wip.yml"));
        var builder = new CommandBuilder("wslc.exe", config, new FakeEnvironment());

        Assert.DoesNotContain("--entrypoint", builder.Up(detach: true));
    }

    [Fact]
    public void EntrypointIsNotPassedToExec()
    {
        using var directory = new TemporaryDirectory();
        var composePath = Path.Combine(directory.Path, "compose.yml");
        File.WriteAllText(composePath, """
            services:
              app:
                image: myapp:dev
                entrypoint: /usr/local/bin/start
            """);
        var config = new Config(YamlLoader.LoadText($$"""
            mode: compose-native
            compose:
              file: {{composePath}}
              service: app
            """, allowAliases: false), Path.Combine(directory.Path, "wip.yml"));
        var builder = new CommandBuilder("wslc.exe", config, new FakeEnvironment());

        Assert.DoesNotContain("--entrypoint", builder.Exec(["sh"]));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-test-").FullName;
        internal string Path { get; }
        internal string Name => new DirectoryInfo(Path).Name;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FakeEnvironment : IEnvironment
    {
        public bool IsInteractive => false;
        public bool IsWsl2 => true;
        public string Architecture => "linux/amd64";
    }
}
