using System.Text.Json;
using Wip.Configuration;
using Wip.Execution;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// The declared mirror (<c>volumes[].sync</c>) and the one-shot container that runs it.
/// Legacy <c>sync:</c> behaviour is covered by the CommandBuilder tests; what matters here is
/// that the declaration is validated, that the mirror targets the volume's own generation,
/// and that the rsync invocation is the shared one rather than a second copy of the options.
/// </summary>
public class VolumeSyncTests
{
    private const string Yaml = """
        version: 1
        resource_namespace: test
        volumes:
          - name: src
            persistent: true
            mount: /workspace/src
            sync:
              source: .
              image: mirror:1
              exclude: [.git, node_modules]
          - name: data
            persistent: true
            mount: /data
        sandboxes:
          - name: box
            image: tool
            volumes: [src, data]
        """;

    private static SandboxSettings Settings(string yaml = Yaml) =>
        new Config(YamlLoader.LoadText(yaml, allowAliases: false)).SandboxResources;

    private sealed class Usage : IVolumeUsageStore
    {
        private readonly HashSet<string> used = [];
        public bool WasUsed(string name) => used.Contains(name);
        public void MarkUsed(string name) => used.Add(name);
        public void Forget(string name) => used.Remove(name);
    }

    /// <summary>Reports one existing generation per created volume and records every call.</summary>
    private sealed class Fake
    {
        public readonly List<string[]> Calls = [];
        public readonly Dictionary<string, string> Backends = [];
        public int MirrorCode;
        public string[] Referencing = [];

        public string Add(string logicalName)
        {
            var backend = VolumeLifecycle.BackendPrefix("test", logicalName) + new string('a', 32);
            Backends[logicalName] = backend;
            return backend;
        }

        public SandboxCommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            Calls.Add(argv.ToArray());
            if (console != SandboxConsoleMode.Capture)
            {
                // A whole-tree copy gets a mirror deadline, not a backend call's.
                Assert.Equal(TimeSpan.FromMinutes(30), timeout);
                return new(MirrorCode, "");
            }
            if (argv[0] == "volume" && argv[1] == "list")
                return new(0, JsonSerializer.Serialize(Backends.Values.Select(name => new { Name = name })));
            if (argv[0] == "volume")
            {
                var backend = argv[^1];
                var logical = Backends.Single(pair => pair.Value == backend).Key;
                return new(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Name = backend,
                        Labels = new Dictionary<string, string>
                        {
                            [VolumeLifecycle.OwnerLabel] = $"v1:test:volume:{logical}",
                            [VolumeLifecycle.PolicyLabel] = "true",
                            [VolumeLifecycle.InstanceLabel] = new string('a', 32),
                        },
                    },
                }));
            }
            if (argv[0] == "list")
                return new(0, JsonSerializer.Serialize(Referencing.Select(id => new { ID = id, State = "running" })));
            return new(0, JsonSerializer.Serialize(argv.Skip(5).Select(id => new
            {
                Id = id,
                State = new { Status = "running" },
                Mounts = Backends.Values.Select(name => new { Type = "volume", Name = name }),
            })));
        }
    }

    [Fact]
    public void MirrorRunsAThrowawayContainerOverTheVolumesOwnGeneration()
    {
        var fake = new Fake();
        var backend = fake.Add("src");
        var service = new VolumeLifecycle(Settings(), fake.Run, new Usage());

        Assert.Equal(0, service.Sync("src", "C:/host/workspace"));

        var mirror = fake.Calls.Single(call => call[0] == "run");
        Assert.Equal(
        [
            "run", "--rm",
            "-v", "C:/host/workspace:/host-src:ro",
            "--mount", $"type=volume,source={backend},target=/workspace/src",
            "mirror:1",
            "rsync", "-r", "-l", "-t", "--whole-file", "--delete",
            "--exclude=.git", "--exclude=node_modules",
            "/host-src/", "/workspace/src/",
        ], mirror);
    }

    /// <summary>The option set lives in one place, so the two mirrors cannot drift apart.</summary>
    [Fact]
    public void MirrorArgumentsAreTheSharedSyncInvocation()
    {
        var fake = new Fake();
        fake.Add("src");
        new VolumeLifecycle(Settings(), fake.Run, new Usage()).Sync("src", "/host/workspace");

        var mirror = fake.Calls.Single(call => call[0] == "run");
        var expected = SyncSettings.MirrorCommand(
            SyncSettings.DefaultBinary, "/host-src", "/workspace/src", delete: true, [".git", "node_modules"]);
        Assert.Equal(expected, mirror[^expected.Count..]);
    }

    [Fact]
    public void RunningSandboxesThatReferenceTheVolumeDoNotBlockTheMirror()
    {
        var fake = new Fake { Referencing = ["c0ffee0c0ffee0c0"] };
        fake.Add("src");
        Assert.Equal(0, new VolumeLifecycle(Settings(), fake.Run, new Usage()).Sync("src", "/host/workspace"));
        Assert.Single(fake.Calls, call => call[0] == "run");
    }

    [Fact]
    public void MirrorExitCodeIsReturnedUnchanged()
    {
        var fake = new Fake { MirrorCode = 23 };
        fake.Add("src");
        Assert.Equal(23, new VolumeLifecycle(Settings(), fake.Run, new Usage()).Sync("src", "/host/workspace"));
    }

    [Fact]
    public void DeclaredDeleteFalseKeepsFilesTheHostNoLongerHas()
    {
        var yaml = Yaml.Replace("exclude: [.git, node_modules]", "exclude: [.git]\n      delete: false");
        var fake = new Fake();
        fake.Add("src");
        new VolumeLifecycle(Settings(yaml), fake.Run, new Usage()).Sync("src", "/host/workspace");
        Assert.DoesNotContain("--delete", fake.Calls.Single(call => call[0] == "run"));
    }

    [Fact]
    public void AbsentStorageIsReportedInsteadOfCreatedOrMirrored()
    {
        var fake = new Fake();
        var service = new VolumeLifecycle(Settings(), fake.Run, new Usage());
        Assert.Throws<WipException>(() => service.Sync("src", "/host/workspace"));
        Assert.DoesNotContain(fake.Calls, call => call[0] == "run" || (call[0] == "volume" && call[1] == "create"));
    }

    [Fact]
    public void VolumeWithoutADeclaredSyncIsRefused()
    {
        var fake = new Fake();
        fake.Add("data");
        Assert.Throws<ConfigException>(() => new VolumeLifecycle(Settings(), fake.Run, new Usage()).Sync("data", "/host/workspace"));
        Assert.DoesNotContain(fake.Calls, call => call[0] == "run");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--delete")]
    [InlineData("/host/work\u0007space")]
    public void UnusableResolvedSourcesNeverReachTheBackend(string source)
    {
        var fake = new Fake();
        fake.Add("src");
        Assert.Throws<WipException>(() => new VolumeLifecycle(Settings(), fake.Run, new Usage()).Sync("src", source));
        Assert.DoesNotContain(fake.Calls, call => call[0] == "run");
    }

    [Fact]
    public void ResolvedSourceIsExpandedAgainstTheConfigDirectory()
    {
        var resolved = Wip.Cli.CliContext.ResolveSyncSource(
            Path.Combine(Path.GetTempPath(), "project"), "src", "../tree", _ => true);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tree")), resolved);
    }

    /// <summary>
    /// An absent source would have the backend bind a path it creates instead, and
    /// <c>--delete</c> would then empty the volume against it.
    /// </summary>
    [Fact]
    public void MissingSourceDirectoryIsRefusedBeforeTheMirrorRuns()
    {
        var probed = new List<string>();
        var exception = Assert.Throws<ConfigException>(() => Wip.Cli.CliContext.ResolveSyncSource(
            Path.Combine(Path.GetTempPath(), "project"), "src", "gone", path => { probed.Add(path); return false; }));
        Assert.Contains("sync source directory does not exist", exception.Message);
        Assert.Equal([Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project", "gone"))], probed);
    }
}
