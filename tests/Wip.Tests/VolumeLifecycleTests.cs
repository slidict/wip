using System.Text.Json;
using Wip.Configuration;
using Wip.Execution;
using Wip.Yaml;

namespace Wip.Tests;

public class VolumeLifecycleTests
{
    private static SandboxSettings Settings() => new Config(YamlLoader.LoadText("""
        version: 1
        resource_namespace: test
        volumes:
          - name: data
            persistent: true
            mount: /data
          - name: scratch
            persistent: false
            mount: /scratch
        """, allowAliases: false)).SandboxResources;

    private sealed class Usage : IVolumeUsageStore
    {
        public readonly HashSet<string> Used = [];
        public bool Fail;
        public bool FailForget;
        public bool WasUsed(string name) => Used.Contains(name);
        public void MarkUsed(string name)
        {
            if (Fail) throw new WipException("journal failed");
            Used.Add(name);
        }
        public void Forget(string name)
        {
            if (FailForget) throw new WipException("marker removal failed");
            Used.Remove(name);
        }
    }

    private sealed record Stored(string Name, Dictionary<string, string> Labels);
    private sealed class Fake
    {
        public readonly List<string[]> Calls = [];
        public readonly Dictionary<string, Stored> Volumes = [];
        public readonly Dictionary<string, string[]> Containers = [];
        public string ContainerState = "running";
        public int MutationCode;
        public int ProbeCode;
        public bool LeaveResidue;
        public string? ListOutput;
        public string? ContainerInspectOutput;
        public Action<int>? OnVolumeList;
        private int listCount;

        public string Add(string logicalName, bool persistent, string? instance = null)
        {
            instance ??= Guid.NewGuid().ToString("N");
            var name = VolumeLifecycle.BackendPrefix("test", logicalName) + instance;
            Volumes[name] = new(name, new()
            {
                [VolumeLifecycle.OwnerLabel] = $"v1:test:volume:{logicalName}",
                [VolumeLifecycle.PolicyLabel] = persistent ? "true" : "false",
                [VolumeLifecycle.InstanceLabel] = instance,
            });
            return name;
        }

        public SandboxCommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            Calls.Add(argv.ToArray());
            if (console == SandboxConsoleMode.Capture)
            {
                Assert.InRange(timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
                if (ProbeCode != 0) return new(ProbeCode, "");
                if (argv[0] == "volume" && argv[1] == "list")
                {
                    OnVolumeList?.Invoke(++listCount);
                    return new(0, ListOutput ?? JsonSerializer.Serialize(Volumes.Values.Select(v => new { v.Name })));
                }
                if (argv[0] == "volume") return new(0, JsonSerializer.Serialize(new[] { Volumes[argv[^1]] }));
                if (argv[0] == "list") return new(0, JsonSerializer.Serialize(Containers.Keys.Select(id => new { ID = id, State = ContainerState })));
                return new(0, ContainerInspectOutput ?? JsonSerializer.Serialize(argv.Skip(5).Select(containerId => new { Id = containerId, State = new { Status = ContainerState },
                    Mounts = Containers[containerId].Select(name => new { Type = "volume", Name = name }) })));
            }
            Assert.Equal(TimeSpan.FromMinutes(2), timeout);
            Assert.DoesNotContain("--force", argv);
            Assert.DoesNotContain("prune", argv);
            if (argv[1] == "create" && (MutationCode == 0 || LeaveResidue))
            {
                var labels = new Dictionary<string, string>();
                for (var index = 2; index < argv.Count - 1; index += 2)
                {
                    Assert.Equal("--label", argv[index]);
                    var pair = argv[index + 1].Split('=', 2);
                    labels[pair[0]] = pair[1];
                }
                Volumes[argv[^1]] = new(argv[^1], labels);
            }
            if (argv[1] == "remove" && MutationCode == 0 && !LeaveResidue)
            {
                if (Containers.Values.Any(names => names.Contains(argv[^1]))) return new(9, "");
                Volumes.Remove(argv[^1]);
            }
            return new(MutationCode, "");
        }
    }

    [Fact]
    public void CreateReuseAndExplicitDestroyAreIdempotentAndNeverPrune()
    {
        var fake = new Fake();
        var service = new VolumeLifecycle(Settings(), fake.Run, new Usage());
        Assert.Null(service.Status("data").BackendName);
        Assert.Equal(0, service.Create("data"));
        var name = service.Status("data").BackendName!;
        Assert.Equal(0, service.Create("data"));
        Assert.Single(fake.Calls, c => c[0] == "volume" && c[1] == "create");
        Assert.Equal(0, service.Destroy("data"));
        Assert.Equal(0, service.Destroy("data"));
        Assert.Equal(["volume", "remove", name], fake.Calls.Single(c => c[1] == "remove"));
        Assert.Equal(0, service.Create("data"));
        Assert.NotEqual(name, service.Status("data").BackendName);
    }

    [Fact]
    public void EphemeralCycleRetainsUnusedAndAllReferencesThenCleansAfterLastDetach()
    {
        var fake = new Fake(); var usage = new Usage();
        var service = new VolumeLifecycle(Settings(), fake.Run, usage);
        service.Create("scratch");
        var name = service.Status("scratch").BackendName!;
        service.Reconcile("scratch");
        Assert.True(fake.Volumes.ContainsKey(name));
        fake.Containers["one"] = [name]; fake.Containers["two"] = [name];
        Assert.Equal(2, service.Status("scratch").References.Count);
        Assert.Throws<WipException>(() => service.Destroy("scratch"));
        fake.Containers.Remove("one");
        service.Reconcile("scratch");
        Assert.True(fake.Volumes.ContainsKey(name));
        fake.Containers.Clear();
        // New service instance models process restart; usage stays generation-scoped.
        Assert.Equal(0, new VolumeLifecycle(Settings(), fake.Run, usage).Reconcile("scratch"));
        Assert.Null(service.Status("scratch").BackendName);
        Assert.Empty(usage.Used);
        service.Create("scratch"); service.Reconcile("scratch");
        Assert.NotNull(service.Status("scratch").BackendName);
    }

    [Fact]
    public void PersistentReconciliationNeverDeletesAfterDetach()
    {
        var fake = new Fake(); var usage = new Usage();
        var name = fake.Add("data", true);
        fake.Containers["one"] = [name];
        var service = new VolumeLifecycle(Settings(), fake.Run, usage);
        service.Reconcile("data"); fake.Containers.Clear(); service.Reconcile("data");
        Assert.Equal(name, service.Status("data").BackendName);
        Assert.DoesNotContain(fake.Calls, c => c[1] == "remove");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("policy")]
    [InlineData("generation")]
    public void ForeignOrChangedPolicyStorageIsNeverAdoptedOrDeleted(string mismatch)
    {
        var fake = new Fake(); var name = fake.Add("data", true);
        fake.Volumes[name].Labels[mismatch switch { "owner" => VolumeLifecycle.OwnerLabel, "policy" => VolumeLifecycle.PolicyLabel, _ => VolumeLifecycle.InstanceLabel }] = "invalid";
        var service = new VolumeLifecycle(Settings(), fake.Run, new Usage());
        Assert.Throws<WipException>(() => service.Create("data"));
        Assert.Throws<WipException>(() => service.Destroy("data"));
        Assert.DoesNotContain(fake.Calls, c => c[1] is "create" or "remove");
    }

    [Theory]
    [InlineData(124, "[]")]
    [InlineData(1, "[]")]
    [InlineData(0, "broken")]
    [InlineData(0, "[{}]")]
    public void FailedOrMalformedListCannotBecomeMissingStorage(int code, string output)
    {
        var fake = new Fake { ProbeCode = code, ListOutput = output };
        Assert.Throws<WipException>(() => new VolumeLifecycle(Settings(), fake.Run, new Usage()).Create("data"));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void FailedCreateResidueIsReusedWithoutAnotherMutation()
    {
        var fake = new Fake { MutationCode = 124, LeaveResidue = true };
        var service = new VolumeLifecycle(Settings(), fake.Run, new Usage());
        Assert.Equal(124, service.Create("data")); fake.MutationCode = 0;
        Assert.Equal(0, service.Create("data"));
        Assert.Single(fake.Calls, c => c[1] == "create");
        Assert.DoesNotContain(fake.Calls, c => c[1] == "remove");
    }

    [Fact]
    public void FailedDeletionRetainsStorageAndPreservesExitCode()
    {
        var fake = new Fake { MutationCode = 23 }; fake.Add("data", true);
        var service = new VolumeLifecycle(Settings(), fake.Run, new Usage());
        Assert.Equal(23, service.Destroy("data")); Assert.NotNull(service.Status("data").BackendName);
        fake.MutationCode = 0; fake.LeaveResidue = true;
        Assert.Throws<WipException>(() => service.Destroy("data"));
    }

    [Fact]
    public void UnknownReferencesOrJournalFailurePreventCleanup()
    {
        var fake = new Fake(); var usage = new Usage { Fail = true };
        var name = fake.Add("scratch", false); fake.Containers["one"] = [name];
        var service = new VolumeLifecycle(Settings(), fake.Run, usage);
        Assert.Throws<WipException>(() => service.Reconcile("scratch"));
        usage.Fail = false; fake.ContainerState = "unknown";
        Assert.Throws<WipException>(() => service.Reconcile("scratch"));
        fake.ContainerState = "running"; fake.ContainerInspectOutput = "[{\"Id\":\"one\",\"State\":\"running\"}]";
        Assert.Throws<WipException>(() => service.Reconcile("scratch"));
        Assert.Empty(usage.Used);
        Assert.DoesNotContain(fake.Calls, c => c[1] == "remove");
    }

    [Fact]
    public void ReconciliationCannotDeleteARecreatedUnusedGeneration()
    {
        var fake = new Fake(); var usage = new Usage();
        var old = fake.Add("scratch", false); usage.Used.Add(old);
        string? replacement = null;
        fake.OnVolumeList = count => { if (count == 2) { fake.Volumes.Remove(old); replacement = fake.Add("scratch", false); } };
        Assert.Throws<WipException>(() => new VolumeLifecycle(Settings(), fake.Run, usage).Reconcile("scratch"));
        Assert.True(fake.Volumes.ContainsKey(replacement!));
        Assert.DoesNotContain(fake.Calls, c => c[1] == "remove");
    }

    [Fact]
    public void DuplicateGenerationsAreAmbiguousAndNeverPruned()
    {
        var fake = new Fake(); fake.Add("data", true); fake.Add("data", true);
        Assert.Throws<WipException>(() => new VolumeLifecycle(Settings(), fake.Run, new Usage()).Destroy("data"));
        Assert.Equal(2, fake.Volumes.Count);
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void ReferenceInspectionIsBatchedAndSharesAnOverallDeadline()
    {
        var fake = new Fake(); var name = fake.Add("scratch", false);
        for (var index = 0; index < 230; index++) fake.Containers[$"container-{index}"] = [name];
        var usage = new Usage();
        Assert.Equal(230, new VolumeLifecycle(Settings(), fake.Run, usage).Status("scratch").References.Count);
        Assert.Equal(3, fake.Calls.Count(c => c[0] == "inspect"));
        Assert.All(fake.Calls.Where(c => c[0] == "inspect"), c => Assert.InRange(c.Length - 5, 1, 100));
        var clock = new Clock(); usage.Used.Clear(); fake.Calls.Clear();
        SandboxCommandResult Slow(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            var result = fake.Run(argv, timeout, console);
            if (argv[0] == "inspect") clock.Ticks += TimeSpan.FromSeconds(6).Ticks;
            return result;
        }
        Assert.Throws<WipException>(() => new VolumeLifecycle(Settings(), Slow, usage, clock).Reconcile("scratch"));
        Assert.Empty(usage.Used);
        Assert.DoesNotContain(fake.Calls, c => c[1] == "remove");
    }

    [Fact]
    public void MarkerRemovalFailureDoesNotDeleteAnotherGeneration()
    {
        var fake = new Fake(); var usage = new Usage { FailForget = true };
        var name = fake.Add("data", true); usage.Used.Add(name);
        var unrelated = fake.Add("scratch", false);
        Assert.Throws<WipException>(() => new VolumeLifecycle(Settings(), fake.Run, usage).Destroy("data"));
        Assert.False(fake.Volumes.ContainsKey(name));
        Assert.True(fake.Volumes.ContainsKey(unrelated));
        Assert.Contains(name, usage.Used);
    }

    [Theory]
    [InlineData("[{\"Id\":\"one\",\"State\":\"running\",\"Mounts\":[]}]")]
    [InlineData("[{\"Id\":\"one\",\"State\":\"running\",\"Mounts\":[]},{\"Id\":\"one\",\"State\":\"running\",\"Mounts\":[]}]")]
    [InlineData("[{\"Id\":\"one\",\"State\":\"running\",\"Mounts\":[]},{\"Id\":\"replacement\",\"State\":\"running\",\"Mounts\":[]}]")]
    public void MissingDuplicateOrUnexpectedBatchRecordsCannotAuthorizeCleanup(string inspection)
    {
        var fake = new Fake { ContainerInspectOutput = inspection };
        var usage = new Usage();
        var name = fake.Add("scratch", false); usage.Used.Add(name);
        fake.Containers["one"] = [name]; fake.Containers["two"] = [name];
        Assert.Throws<WipException>(() => new VolumeLifecycle(Settings(), fake.Run, usage).Reconcile("scratch"));
        Assert.True(fake.Volumes.ContainsKey(name));
        Assert.Contains(name, usage.Used);
        Assert.DoesNotContain(fake.Calls, c => c[1] == "remove");
    }
}
