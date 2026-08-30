using Wip.Compose;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// Covers <c>dns:</c> — accepted and ignored like <c>cap_add</c>/<c>networks</c>/<c>tty</c>,
/// since <c>wslc run</c>/<c>exec</c> has no flag to set per-container DNS servers.
/// </summary>
public class ComposeFileIgnoredKeysTests
{
    [Fact]
    public void DnsIsAcceptedAndIgnored()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "compose.yml");
        File.WriteAllText(path, """
            services:
              app:
                image: myapp:dev
                dns:
                  - 8.8.8.8
                  - 1.1.1.1
            """);

        var compose = ComposeFile.Load(path);
        var app = (OrderedDictionary<string, object?>)compose.ToDependenciesMapping()["app"]!;

        Assert.False(app.ContainsKey("dns"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-test-").FullName;

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
