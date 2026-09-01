using Wip.Compose;
using Wip.Yaml;

namespace Wip.Tests;

public class YamlLoaderSecurityTests
{
    [Fact]
    public void RejectsExcessiveNesting()
    {
        var yaml = $"{new string('[', 102)}null{new string(']', 102)}";

        var exception = Assert.Throws<ConfigException>(
            () => YamlLoader.LoadText(yaml, allowAliases: false, path: "nested.yml"));

        Assert.Equal(
            "Could not parse nested.yml: YAML nesting exceeds the limit of 100",
            exception.Message);
    }

    [Fact]
    public void AcceptsNestingAtTheLimit()
    {
        var yaml = $"{new string('[', 100)}null{new string(']', 100)}";

        Assert.NotNull(YamlLoader.LoadText(yaml, allowAliases: false));
    }

    [Fact]
    public void RejectsScalarOverTheLimit()
    {
        var yaml = new string('a', YamlLoader.MaxScalarLength + 1);

        var exception = Assert.Throws<ConfigException>(
            () => YamlLoader.LoadText(yaml, allowAliases: false, path: "scalar.yml"));

        Assert.Contains("scalar.yml", exception.Message);
        Assert.Contains($"scalar length exceeds the limit of {YamlLoader.MaxScalarLength}", exception.Message);
    }

    [Fact]
    public void RejectsTooManyShallowSequenceElements()
    {
        var yaml = $"[{string.Join(',', Enumerable.Repeat("0", YamlLoader.MaxCollectionElements + 1))}]";

        var exception = Assert.Throws<ConfigException>(
            () => YamlLoader.LoadText(yaml, allowAliases: false, path: "sequence.yml"));

        Assert.Contains($"sequence element count exceeds the limit of {YamlLoader.MaxCollectionElements}", exception.Message);
    }

    [Fact]
    public void RejectsTooManyMappingElements()
    {
        var yaml = string.Concat(Enumerable.Range(0, YamlLoader.MaxCollectionElements + 1)
            .Select(index => $"k{index}: 0\n"));

        var exception = Assert.Throws<ConfigException>(
            () => YamlLoader.LoadText(yaml, allowAliases: false, path: "mapping.yml"));

        Assert.Contains($"mapping element count exceeds the limit of {YamlLoader.MaxCollectionElements}", exception.Message);
    }

    [Fact]
    public void RejectsTooManyNodesAcrossSmallCollections()
    {
        var group = $"[{string.Join(',', Enumerable.Repeat("[]", 40_000))}]";
        var yaml = $"[{group},{group},{group},{group}]";

        var exception = Assert.Throws<ConfigException>(
            () => YamlLoader.LoadText(yaml, allowAliases: false, path: "nodes.yml"));

        Assert.Contains($"total node count exceeds the limit of {YamlLoader.MaxTotalNodes}", exception.Message);
    }

    [Fact]
    public void AcceptsScalarAtTheLimit()
    {
        var yaml = new string('a', YamlLoader.MaxScalarLength);

        Assert.Equal(yaml, YamlLoader.LoadText(yaml, allowAliases: false));
    }

    [Fact]
    public void RejectsOversizedTextAndFileInputs()
    {
        var oversized = new string('#', YamlLoader.MaxInputSize + 1);
        var textException = Assert.Throws<ConfigException>(
            () => YamlLoader.LoadText(oversized, allowAliases: false, path: "text.yml"));

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "large.yml");
        File.WriteAllText(path, oversized);
        var fileException = Assert.Throws<ConfigException>(() => YamlLoader.LoadFile(path, allowAliases: false));

        Assert.Contains($"input length exceeds the limit of {YamlLoader.MaxInputSize}", textException.Message);
        Assert.Contains($"file size exceeds the limit of {YamlLoader.MaxInputSize}", fileException.Message);
    }

    [Fact]
    public void AcceptsNormalWipFileAtTheInputBoundary()
    {
        const string document = "container: app\n";
        var yaml = document + new string('#', YamlLoader.MaxInputSize - document.Length);

        var value = Assert.IsType<OrderedDictionary<string, object?>>(
            YamlLoader.LoadText(yaml, allowAliases: false, path: "wip.yml"));

        Assert.Equal("app", value["container"]);
    }

    [Fact]
    public void AcceptsComposeFileWithAliasesAtTheFileBoundary()
    {
        const string document = "services:\n  app: &app\n    image: example:latest\n  worker:\n    <<: *app\n";
        var yaml = document + new string('#', YamlLoader.MaxInputSize - document.Length);
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "compose.yml");
        File.WriteAllText(path, yaml);

        var compose = ComposeFile.Load(path);

        Assert.Equal(["app", "worker"], compose.ServiceNamesInDependencyOrder);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-yaml-test-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
