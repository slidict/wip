using Wip.Configuration;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// <c>wip init</c> writes the file <c>wip doctor</c> is about to read, so a template that does
/// not load is the worst possible first impression. These pin the whole loop: template →
/// wip.yml → Config → YAML → Config again.
/// </summary>
public class InitializerRoundTripTests
{
    public static TheoryData<string?> Templates()
    {
        var data = new TheoryData<string?> { (string?)null };
        foreach (var name in Initializer.TemplateLabels.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void ContainerTemplateLoads(string? template)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "wip.yml");
        File.WriteAllText(path, new Initializer(directory.Path, template).Call());

        var config = new ConfigLoader(path: path).Load();

        Assert.Equal("container", config.Mode);
        Assert.Equal("app", config.Container);
        Assert.NotNull(config.Sync);
        Assert.Equal("/host-src", config.Sync!.Mount);

        // restart: "no" has to survive as the string, not resolve to boolean false.
        Assert.Equal("no", RubyValue.ToStringValue(config.Dependency("app")!["restart"]));
    }

    [Fact]
    public void ComposeTemplateIsChosenWhenAComposeFileExists()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "compose.yml"), """
            services:
              app:
                image: busybox:latest
            """);

        var initializer = new Initializer(directory.Path);
        Assert.True(initializer.IsCompose);

        var path = Path.Combine(directory.Path, "wip.yml");
        File.WriteAllText(path, initializer.Call());

        var config = new ConfigLoader(path: path).Load();
        Assert.Equal("compose-native", config.Mode);
        Assert.Equal("app", config.Container);
    }

    /// <summary>
    /// The output of <c>wip config</c> has to read back as the same document, which means
    /// nulls cannot come out as '' and "no" cannot come out unquoted.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures))]
    public void ConfigOutputReloadsUnchanged(string fixture)
    {
        var path = Path.Combine(GoldenCorpus.CasesDirectory, fixture, "wip.yml");
        var original = new ConfigLoader(path: path).Load().ToMapping();

        using var directory = new TemporaryDirectory();
        var dumped = Path.Combine(directory.Path, "wip.yml");
        File.WriteAllText(dumped, YamlWriter.Dump(original));

        var reloaded = YamlLoader.LoadFile(dumped, allowAliases: false);

        Assert.Equal(
            RubyJson.Canonical(RubyJson.ToJson(original)),
            RubyJson.Canonical(RubyJson.ToJson(reloaded)));
    }

    public static TheoryData<string> GoldenFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in GoldenCorpus.FixtureNames())
        {
            data.Add(fixture);
        }

        return data;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-test-").FullName;

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
