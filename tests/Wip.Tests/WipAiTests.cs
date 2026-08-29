using Wip.Ai;
using Wip.Configuration;

namespace Wip.Tests;

public class WipAiTests
{
    [Fact]
    public void AnalyzerCollectsOnlyAllowListedFilesAndBoundsInput()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "README.md"), new string('r', ProjectAnalyzer.MaxFileCharacters + 100));
        File.WriteAllText(Path.Combine(directory.Path, ".env"), "SECRET=do-not-send");
        File.WriteAllText(Path.Combine(directory.Path, "random.txt"), "not relevant");

        var snapshot = new ProjectAnalyzer(directory.Path).Analyze();

        var file = Assert.Single(snapshot.Files);
        Assert.Equal("README.md", file.RelativePath);
        Assert.Contains("[truncated by wip]", file.Content);
        Assert.DoesNotContain("SECRET", snapshot.ToPromptText());
    }

    [Fact]
    public void GeneratorStripsFenceAndValidatesWithExistingParser()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "wip.yml");
        var provider = new StubProvider("""
            ```yaml
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: ruby:3.4
            ```
            """);

        var result = new WipAiGenerator(provider).Generate(
            "Run Rails", new ProjectSnapshot(directory.Path, []), null, path);

        Assert.Equal("container", new ConfigLoader(path: Write(path, result)).Load().Mode);
        Assert.Contains("User request:\nRun Rails", provider.Prompt);
    }

    [Fact]
    public void GeneratorRejectsInvalidCandidateBeforeItCanBeSaved()
    {
        using var directory = new TemporaryDirectory();
        var generator = new WipAiGenerator(new StubProvider("version: 999"));

        Assert.Throws<ConfigException>(() => generator.Generate(
            "anything", new ProjectSnapshot(directory.Path, []), null, Path.Combine(directory.Path, "wip.yml")));
    }

    private static string Write(string path, string content)
    {
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class StubProvider(string response) : IWipAiProvider
    {
        internal string? Prompt { get; private set; }
        public string Generate(string prompt, CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            return response;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-ai-test-").FullName;
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
