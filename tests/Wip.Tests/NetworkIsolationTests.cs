using System.Text.Json;
using Wip.Configuration;
using Wip.Execution;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// The isolation boundary, asserted without WSLC. What matters here is not that the happy
/// path emits <c>--network</c>, but that every way of failing to confirm the boundary stops
/// before a container exists: the fallback a bug would produce is the backend's default
/// bridge, which has unrestricted egress.
/// </summary>
public class NetworkIsolationTests
{
    private const string Namespace = "test";
    private static readonly string SandboxName = SandboxLifecycle.BackendName(Namespace, "scanner");
    private static readonly string ScanNet = NetworkLifecycle.BackendName(Namespace, "scan");
    private static readonly string ScanIdentity = $"v1:{Namespace}:network:scan";

    private static SandboxSettings Settings(string body) => new Config(YamlLoader.LoadText("""
        version: 1
        resource_namespace: test
        """ + body, allowAliases: false)).SandboxResources;

    private static SandboxSettings Isolated() => Settings("""

        networks:
          - name: scan
            internal: true
        sandboxes:
          - name: scanner
            image: fixture:latest
            network: scan
        """);

    /// <summary>
    /// Models the two shapes WSLC actually returns, measured 2026-10-24: <c>network list</c>
    /// renders <c>Internal</c> as a string and <c>Labels</c> as one flat field, while
    /// <c>network inspect</c> types them as a bool and an object. A fake that typed both the
    /// same way would hide a parsing mistake in whichever one it got wrong.
    /// </summary>
    private sealed class Fake
    {
        public readonly List<string[]> Calls = [];
        public bool NetworkExists;
        public bool NetworkInternal = true;
        public string NetworkOwner = ScanIdentity;
        public bool ContainerExists;
        public string ContainerState = "running";
        public List<string> Attachments = [ScanNet];
        public int NetworkCreateCode;
        public int NetworkInspectCode;
        public bool CreateLeavesNetworkAbsent;

        /// <summary>
        /// Containers on the network besides this sandbox's own, and whether they are
        /// running. A stopped one still holds the network: WSLC removes it anyway, and the
        /// container is then unstartable, so the count must include it.
        /// </summary>
        public int OtherAttachments;
        public string OtherAttachmentState = "exited";

        public SandboxCommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            Calls.Add(argv.ToArray());
            if (argv is ["network", "list", ..])
            {
                return new(0, NetworkExists
                    ? JsonSerializer.Serialize(new[]
                    {
                        new { Name = ScanNet, ID = "net-id", Internal = NetworkInternal ? "true" : "false", Labels = $"{NetworkLifecycle.OwnerLabel}={NetworkOwner}" },
                    })
                    : "[]");
            }
            if (argv is ["network", "inspect", ..])
            {
                if (NetworkInspectCode != 0) return new(NetworkInspectCode, "");
                if (!NetworkExists) return new(1, "");
                return new(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = new string('a', 64),
                        Name = ScanNet,
                        Internal = NetworkInternal,
                        Labels = new Dictionary<string, string> { [NetworkLifecycle.OwnerLabel] = NetworkOwner },
                        // Only running containers, which is what WSLC reports here and the
                        // reason attachments are counted from the container listing instead.
                        Containers = Enumerable.Range(0, ContainerExists && ContainerState == "running" ? 1 : 0)
                            .ToDictionary(i => $"running-{i}", _ => new { }),
                    },
                }));
            }
            if (argv is ["network", "create", ..])
            {
                if (NetworkCreateCode == 0 && !CreateLeavesNetworkAbsent) NetworkExists = true;
                return new(NetworkCreateCode, "");
            }
            if (argv is ["network", "remove", ..])
            {
                NetworkExists = false;
                return new(0, "");
            }
            if (argv[0] == "list")
            {
                var rows = new List<object>();
                if (ContainerExists) rows.Add(new { Id = "container-id", Name = SandboxName, State = ContainerState });
                for (var i = 0; i < OtherAttachments; i++)
                    rows.Add(new { Id = $"other-{i}", Name = $"wip-s-other-{i}", State = OtherAttachmentState });
                return new(0, JsonSerializer.Serialize(rows));
            }
            if (argv[0] == "inspect")
            {
                var requested = argv.Skip(argv.ToList().IndexOf("json") + 1).ToArray();
                var inspected = new List<object>();
                foreach (var id in requested)
                {
                    inspected.Add(id == "container-id"
                        ? new
                        {
                            Id = "container-id",
                            Name = SandboxName,
                            State = ContainerState,
                            Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = $"v1:{Namespace}:sandbox:scanner" },
                            Mounts = Array.Empty<object>(),
                            NetworkSettings = new { Networks = Attachments.ToDictionary(n => n, _ => new { }) },
                        }
                        : new
                        {
                            Id = id,
                            Name = $"wip-s-{id}",
                            State = OtherAttachmentState,
                            Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = $"v1:{Namespace}:sandbox:{id}" },
                            Mounts = Array.Empty<object>(),
                            NetworkSettings = new { Networks = new Dictionary<string, object> { [ScanNet] = new { } } },
                        });
                }
                return new(0, JsonSerializer.Serialize(inspected));
            }
            if (argv[0] is "run" or "start")
            {
                ContainerExists = true;
                ContainerState = "running";
                return new(0, "");
            }
            if (argv[0] == "remove")
            {
                ContainerExists = false;
                return new(0, "");
            }
            return new(0, "");
        }
    }

    private static SandboxLifecycle Lifecycle(Fake fake, SandboxSettings? settings = null)
    {
        var resolved = settings ?? Isolated();
        return new(resolved, fake.Run, null, new NetworkLifecycle(resolved, fake.Run));
    }

    private static bool Ran(Fake fake, params string[] prefix) =>
        fake.Calls.Any(c => c.Length >= prefix.Length && c.Take(prefix.Length).SequenceEqual(prefix));

    [Fact]
    public void CreateAttachesTheContainerToTheDeclaredInternalNetwork()
    {
        var fake = new Fake { ContainerExists = false, NetworkExists = false };

        Assert.Equal(0, Lifecycle(fake).Create("scanner"));

        var run = fake.Calls.Single(c => c[0] == "run");
        var networkIndex = Array.IndexOf(run, "--network");
        Assert.True(networkIndex >= 0, "run must attach a network");
        Assert.Equal(ScanNet, run[networkIndex + 1]);
        // Before the image, or the backend reads it as an argument to the container instead.
        Assert.True(networkIndex + 1 < Array.IndexOf(run, "fixture:latest"));
        Assert.True(Ran(fake, "network", "create", "--internal"), "the network must be created with --internal");
    }

    [Fact]
    public void ASandboxWithNoDeclaredNetworkIsUnchanged()
    {
        var settings = Settings("""

            sandboxes:
              - name: scanner
                image: fixture:latest
            """);
        var fake = new Fake { ContainerExists = false, Attachments = ["bridge"] };

        Assert.Equal(0, Lifecycle(fake, settings).Create("scanner"));

        Assert.DoesNotContain("--network", fake.Calls.Single(c => c[0] == "run"));
        Assert.False(Ran(fake, "network", "create"), "no network is created for a sandbox that declares none");
    }

    [Fact]
    public void AFailedNetworkCreateStartsNoContainer()
    {
        var fake = new Fake { ContainerExists = false, NetworkExists = false, NetworkCreateCode = 3 };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "run"), "a container must not be started without a verified boundary");
    }

    [Fact]
    public void ACreateThatLeavesNoNetworkStartsNoContainer()
    {
        var fake = new Fake { ContainerExists = false, NetworkExists = false, CreateLeavesNetworkAbsent = true };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "run"));
    }

    [Fact]
    public void ANonInternalNetworkOfTheSameNameIsNeitherAdoptedNorRecreated()
    {
        var fake = new Fake { ContainerExists = false, NetworkExists = true, NetworkInternal = false };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "run"));
        Assert.False(Ran(fake, "network", "remove"), "a network wip may not own must not be deleted");
        Assert.False(Ran(fake, "network", "create"), "an existing network must not be recreated over");
    }

    [Fact]
    public void ANetworkOwnedBySomethingElseStartsNoContainer()
    {
        var fake = new Fake { ContainerExists = false, NetworkExists = true, NetworkOwner = "v1:other:network:scan" };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "run"));
        Assert.False(Ran(fake, "network", "remove"));
    }

    [Fact]
    public void AnUnreadableNetworkInspectionStartsNoContainer()
    {
        var fake = new Fake { ContainerExists = false, NetworkExists = true, NetworkInspectCode = 7 };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "run"));
    }

    [Fact]
    public void ADeclaredNetworkWithoutANetworkLifecycleIsAConfigurationError()
    {
        var fake = new Fake { ContainerExists = false };

        Assert.Throws<ConfigException>(() => new SandboxLifecycle(Isolated(), fake.Run).Create("scanner"));

        Assert.False(Ran(fake, "run"));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("exited")]
    [InlineData("running")]
    public void ResumeRefusesAContainerAttachedToAnythingElse(string state)
    {
        // The abnormal-exit case: the container came back, but on whatever network it was
        // last given. Attachments are fixed at creation, so this is reported, not re-attached.
        var fake = new Fake { ContainerExists = true, ContainerState = state, NetworkExists = true, Attachments = ["bridge"] };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "start"), "a container on the wrong network must not be started");
        Assert.False(Ran(fake, "network", "connect"), "isolation is never repaired by attaching after the fact");
    }

    [Fact]
    public void ResumeRefusesAContainerAlsoAttachedToAnotherNetwork()
    {
        var fake = new Fake { ContainerExists = true, ContainerState = "exited", NetworkExists = true, Attachments = [ScanNet, "bridge"] };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "start"));
    }

    [Fact]
    public void ResumeStartsAContainerStillOnTheDeclaredNetwork()
    {
        var fake = new Fake { ContainerExists = true, ContainerState = "exited", NetworkExists = true };

        Assert.Equal(0, Lifecycle(fake).Create("scanner"));

        Assert.True(Ran(fake, "start"));
    }

    [Fact]
    public void ResumeWithAnUnverifiableNetworkStartsNothing()
    {
        var fake = new Fake { ContainerExists = true, ContainerState = "exited", NetworkExists = true, NetworkInspectCode = 7 };

        Assert.Throws<WipException>(() => Lifecycle(fake).Create("scanner"));

        Assert.False(Ran(fake, "start"));
    }

    [Fact]
    public void DestroyRemovesTheNetworkOnlyAfterTheContainer()
    {
        var fake = new Fake { ContainerExists = true, NetworkExists = true };

        Assert.Equal(0, Lifecycle(fake).Destroy("scanner"));

        var containerRemoval = fake.Calls.FindIndex(c => c[0] == "remove");
        var networkRemoval = fake.Calls.FindIndex(c => c is ["network", "remove", ..]);
        Assert.True(containerRemoval >= 0 && networkRemoval > containerRemoval,
            "the network must outlive the container it isolated");
    }

    [Fact]
    public void DestroyKeepsANetworkAnotherSandboxIsStillOn()
    {
        // A scanner and its target share one declared network, so destroying the first of
        // them must leave the boundary standing for the second rather than failing on a
        // network the backend will not remove.
        var fake = new Fake { ContainerExists = true, NetworkExists = true, OtherAttachments = 1, OtherAttachmentState = "running" };

        Assert.Equal(0, Lifecycle(fake).Destroy("scanner"));

        Assert.True(Ran(fake, "remove"), "the container is still removed");
        Assert.False(Ran(fake, "network", "remove"), "a network another container is on must be kept");
    }

    [Fact]
    public void DestroyKeepsANetworkAStoppedSandboxIsStillOn()
    {
        // The case `network inspect` cannot answer: it lists only running containers, and
        // WSLC removes the network anyway. The stopped container is then unstartable for
        // good, because its attachment is pinned to the removed network's id and recreating
        // the same name does not restore it.
        var fake = new Fake { ContainerExists = true, NetworkExists = true, OtherAttachments = 1, OtherAttachmentState = "exited" };

        Assert.Equal(0, Lifecycle(fake).Destroy("scanner"));

        Assert.True(Ran(fake, "remove"), "the container is still removed");
        Assert.False(Ran(fake, "network", "remove"), "a network a stopped container is on must be kept");
    }

    [Fact]
    public void DestroyDeletesNothingWhenTheNetworkCannotBeInspected()
    {
        var fake = new Fake { ContainerExists = false, NetworkExists = true, NetworkInspectCode = 7 };

        Assert.Throws<WipException>(() => Lifecycle(fake).Destroy("scanner"));

        Assert.False(Ran(fake, "network", "remove"));
    }

    [Fact]
    public void DestroyLeavesANetworkItMayNotOwn()
    {
        var fake = new Fake { ContainerExists = true, NetworkExists = true, NetworkOwner = "v1:other:network:scan" };

        Assert.Throws<WipException>(() => Lifecycle(fake).Destroy("scanner"));

        Assert.False(Ran(fake, "network", "remove"));
    }

    [Fact]
    public void ParallelSandboxesGetDistinctNetworkNames()
    {
        // Two scans must not share a boundary: reaching the other sandbox is exactly one of
        // the paths #687 requires to be blocked.
        Assert.NotEqual(NetworkLifecycle.BackendName(Namespace, "scan-a"), NetworkLifecycle.BackendName(Namespace, "scan-b"));
        // And the same declared name in a different project is a different network.
        Assert.NotEqual(NetworkLifecycle.BackendName("other", "scan"), NetworkLifecycle.BackendName(Namespace, "scan"));
        Assert.StartsWith("wip-n-", NetworkLifecycle.BackendName(Namespace, "scan"));
    }

    [Fact]
    public void NetworkNamesAreStableAcrossRuns()
    {
        // Status has to be a pure lookup after an abnormal exit, which a per-run name would
        // break: the orphaned network would no longer be findable by its declared name.
        Assert.Equal(NetworkLifecycle.BackendName(Namespace, "scan"), NetworkLifecycle.BackendName(Namespace, "scan"));
    }

    [Fact]
    public void AnUndeclaredNetworkReferenceIsRejectedAtLoad()
    {
        var error = Assert.Throws<ConfigException>(() => Settings("""

            sandboxes:
              - name: scanner
                image: fixture:latest
                network: scan
            """));
        Assert.Contains("undefined network", error.Message);
    }

    [Fact]
    public void ANonInternalNetworkCannotBeReferencedByASandbox()
    {
        var error = Assert.Throws<ConfigException>(() => Settings("""

            networks:
              - name: open
                internal: false
            sandboxes:
              - name: scanner
                image: fixture:latest
                network: open
            """));
        Assert.Contains("not internal", error.Message);
    }

    [Fact]
    public void NetworkInternalMustBeDeclaredExplicitly()
    {
        Assert.Throws<ConfigException>(() => Settings("""

            networks:
              - name: scan
            """));
        Assert.Throws<ConfigException>(() => Settings("""

            networks:
              - name: scan
                internal: "true"
            """));
    }

    [Fact]
    public void DuplicateNetworkNamesAreRejected()
    {
        Assert.Throws<ConfigException>(() => Settings("""

            networks:
              - name: scan
                internal: true
              - name: scan
                internal: true
            """));
    }

    [Fact]
    public void TheDeclarationRoundTripsThroughNormalization()
    {
        var config = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            networks:
              - name: scan
                internal: true
            sandboxes:
              - name: scanner
                image: fixture:latest
                network: scan
            """, allowAliases: false));
        var mapping = config.ToMapping();

        var networks = Assert.IsType<List<object?>>(mapping["networks"]);
        var network = Assert.IsType<OrderedDictionary<string, object?>>(networks.Single());
        Assert.Equal("scan", network["name"]);
        Assert.Equal(true, network["internal"]);
        var sandboxes = Assert.IsType<List<object?>>(mapping["sandboxes"]);
        var sandbox = Assert.IsType<OrderedDictionary<string, object?>>(sandboxes.Single());
        Assert.Equal("scan", sandbox["network"]);
    }

    [Fact]
    public void ConfigsWithoutNetworksNormalizeExactlyAsBefore()
    {
        var mapping = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            sandboxes:
              - name: scanner
                image: fixture:latest
            """, allowAliases: false)).ToMapping();

        Assert.False(mapping.ContainsKey("networks"));
        var sandboxes = Assert.IsType<List<object?>>(mapping["sandboxes"]);
        Assert.False(Assert.IsType<OrderedDictionary<string, object?>>(sandboxes.Single()).ContainsKey("network"));
    }
}
