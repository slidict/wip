using System.Text.Json;
using Wip.Configuration;
using Wip.Execution;
using Wip.Yaml;

namespace Wip.Tests;

public class SandboxLifecycleTests
{
    private static SandboxSettings Settings(string suffix = "") => new Config(YamlLoader.LoadText("""
        version: 1
        resource_namespace: test
        sandboxes:
          - name: first
            image: fixture:latest
        """ + suffix, allowAliases: false)).SandboxResources;

    private sealed class Fake
    {
        public readonly List<string[]> Calls = [];
        public bool Exists;
        public string State = "running";
        public string Owner = "v1:test:sandbox:first";
        public string Id = "backend-id";
        public string? ListOutput;
        public string? InspectOutput;
        public int ProbeCode;
        public int MutationCode;
        public int ExecCode;
        public bool LeaveResidue;
        public TimeSpan ExecTimeout;
        public readonly string Name = SandboxLifecycle.BackendName("test", "first");

        public SandboxCommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, bool capture)
        {
            Calls.Add(argv.ToArray());
            if (argv[0] == "list") return new(ProbeCode, ListOutput ?? (Exists ? JsonSerializer.Serialize(new[] { new { Id, Name, State } }) : "[]"));
            if (argv[0] == "inspect") return new(ProbeCode, InspectOutput ?? JsonSerializer.Serialize(new[] { new { Id, Name, State, Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = Owner } } }));
            Assert.False(capture);
            if (argv[0] == "exec") { ExecTimeout = timeout; return new(ExecCode, ""); }
            Assert.Equal(TimeSpan.FromMinutes(2), timeout);
            if (argv[0] == "run" && (MutationCode == 0 || LeaveResidue)) { Exists = true; State = "running"; }
            if (argv[0] == "start" && MutationCode == 0) State = "running";
            if (argv[0] == "remove" && MutationCode == 0 && !LeaveResidue) Exists = false;
            return new(MutationCode, "");
        }
    }

    [Fact]
    public void CreateExecDestroyAreIdempotentAndPreserveArgvAndExitCode()
    {
        var fake = new Fake { ExecCode = 7 };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal("not found", service.Status("first").State);
        Assert.Equal(0, service.Create("first"));
        Assert.Equal(0, service.Create("first"));
        Assert.Equal("running", service.Status("first").State);
        string[] argv = ["printf", "%s", "spaces ; $()", "", "--help"];
        Assert.Equal(7, service.Exec("first", argv, TimeSpan.FromSeconds(17)));
        Assert.Equal(new[] { "exec", fake.Id }.Concat(argv), fake.Calls.Single(c => c[0] == "exec"));
        Assert.Equal(TimeSpan.FromSeconds(17), fake.ExecTimeout);
        Assert.Equal(0, service.Destroy("first"));
        Assert.Equal(0, service.Destroy("first"));
        Assert.Single(fake.Calls, c => c[0] == "run");
        Assert.Equal(["remove", "-f", fake.Id], fake.Calls.Single(c => c[0] == "remove"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("volume") || c.Contains("-v"));
    }

    [Fact]
    public void OwnedStoppedContainerIsStartedById()
    {
        var fake = new Fake { Exists = true, State = "exited" };
        Assert.Equal(0, new SandboxLifecycle(Settings(), fake.Run).Create("first"));
        Assert.Equal(["start", fake.Id], fake.Calls.Single(c => c[0] == "start"));
        Assert.DoesNotContain(fake.Calls, c => c[0] == "run");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("destroy")]
    [InlineData("exec")]
    public void ForeignContainerCannotBeAdoptedExecutedOrDeleted(string operation)
    {
        var fake = new Fake { Exists = true, Owner = "someone-else" };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Throws<WipException>(() => operation switch
        {
            "create" => service.Create("first"),
            "destroy" => service.Destroy("first"),
            _ => service.Exec("first", ["true"], TimeSpan.FromSeconds(10)),
        });
        Assert.All(fake.Calls, c => Assert.Contains(c[0], new[] { "list", "inspect" }));
    }

    [Theory]
    [InlineData(124, "[]")]
    [InlineData(1, "[]")]
    [InlineData(0, "null")]
    [InlineData(0, "broken")]
    [InlineData(0, "[{}]")]
    public void FailedOrMalformedProbeNeverBecomesAbsence(int code, string output)
    {
        var fake = new Fake { ProbeCode = code, ListOutput = output };
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Create("first"));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void ChangedIdentityDuringInspectionFailsClosed()
    {
        var fake = new Fake { Exists = true, InspectOutput = "[{\"Id\":\"replacement\",\"Name\":\"other\"}]" };
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Destroy("first"));
        Assert.DoesNotContain(fake.Calls, c => c[0] == "remove");
    }

    [Fact]
    public void FailedCreateResidueCanBeRecoveredByRepeatingCreate()
    {
        var fake = new Fake { MutationCode = 124, LeaveResidue = true };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal(124, service.Create("first"));
        fake.MutationCode = 0;
        Assert.Equal(0, service.Create("first"));
        Assert.Single(fake.Calls, c => c[0] == "run");
        Assert.DoesNotContain(fake.Calls, c => c[0] == "remove");
    }

    [Fact]
    public void FailedDestroyPreservesErrorAndResidualContainerIsReported()
    {
        var fake = new Fake { Exists = true, MutationCode = 23 };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal(23, service.Destroy("first"));
        Assert.Equal("running", service.Status("first").State);
        fake.MutationCode = 0;
        fake.LeaveResidue = true;
        Assert.Throws<WipException>(() => service.Destroy("first"));
    }

    [Fact]
    public void MissingExecUnknownDefinitionAndInvalidArgvFailBeforeMutation()
    {
        var fake = new Fake();
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Throws<WipException>(() => service.Exec("first", ["true"], TimeSpan.FromSeconds(1)));
        Assert.Throws<ConfigException>(() => service.Create("missing"));
        Assert.Throws<ConfigException>(() => service.Exec("first", [], TimeSpan.FromSeconds(1)));
        Assert.Throws<ConfigException>(() => service.Exec("first", ["true"], TimeSpan.Zero));
        Assert.DoesNotContain(fake.Calls, c => c[0] is "run" or "exec");
    }

    [Fact]
    public void BackendNamesAreStableAndNamespaceScoped()
    {
        Assert.Equal(SandboxLifecycle.BackendName("one", "first"), SandboxLifecycle.BackendName("one", "first"));
        Assert.NotEqual(SandboxLifecycle.BackendName("one", "first"), SandboxLifecycle.BackendName("two", "first"));
        Assert.Matches("^wip-s-[a-f0-9]{40}$", SandboxLifecycle.BackendName("one", "first"));
    }

    [Fact]
    public void SuccessfulEmptyListMeansMissingButEmptyInspectDoesNot()
    {
        var fake = new Fake { ListOutput = "" };
        Assert.Equal("not found", new SandboxLifecycle(Settings(), fake.Run).Status("first").State);
        fake.ListOutput = null;
        fake.Exists = true;
        fake.InspectOutput = "";
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Destroy("first"));
    }

    [Fact]
    public void DockerShortIdIsVerifiedAndFullIdIsUsedForExec()
    {
        var fake = new Fake { Exists = true, Id = new string('a', 64) };
        fake.ListOutput = JsonSerializer.Serialize(new { ID = fake.Id[..12], Names = fake.Name });
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal(fake.Id, service.Status("first").Id);
        service.Exec("first", ["true"], TimeSpan.FromSeconds(1));
        Assert.Equal(fake.Id, fake.Calls.Single(c => c[0] == "exec")[1]);
    }

    [Fact]
    public void MountReferencesAreRejectedBeforeAnyBackendCall()
    {
        var settings = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: data
                persistent: true
                mount: /data
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [data]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        Assert.Throws<ConfigException>(() => new SandboxLifecycle(settings, fake.Run).Create("first"));
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    public void UnstartableStatesExplainExplicitRecovery(string state)
    {
        var fake = new Fake { Exists = true, State = state };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        var exception = Assert.Throws<WipException>(() => service.Create("first"));
        Assert.Contains("sandbox destroy", exception.Message);
        Assert.DoesNotContain(fake.Calls, c => c[0] is "start" or "run" or "remove");
        Assert.Equal(0, service.Destroy("first"));
        fake.State = "running";
        Assert.Equal(0, service.Create("first"));
    }

    [Fact]
    public void NullTopLevelLabelsUseConfigLabelsButConflictingObjectIsRejected()
    {
        var fake = new Fake { Exists = true };
        fake.InspectOutput = JsonSerializer.Serialize(new[] { new { fake.Id, fake.Name, State = new { Status = "running" }, Labels = (object?)null,
            Config = new { Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = fake.Owner } } } });
        Assert.Equal("running", new SandboxLifecycle(Settings(), fake.Run).Status("first").State);
        fake.InspectOutput = JsonSerializer.Serialize(new[] { new { fake.Id, fake.Name, fake.State,
            Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = "foreign" },
            Config = new { Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = fake.Owner } } } });
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Destroy("first"));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("\"deleted\"")]
    public void DeletedListTombstonesAreAbsentAndNeverInspectedOrRemoved(string state)
    {
        var fake = new Fake();
        fake.ListOutput = $"[{{\"Name\":\"{fake.Name}\",\"State\":{state}}}]";
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal("not found", service.Status("first").State);
        Assert.Equal(0, service.Destroy("first"));
        Assert.All(fake.Calls, c => Assert.Equal("list", c[0]));
        fake.ListOutput = null;
        Assert.Equal(0, service.Create("first"));
        var tombstone = new Fake { Exists = true, State = "deleted" };
        Assert.Equal(0, new SandboxLifecycle(Settings(), tombstone.Run).Create("first"));
        Assert.Single(tombstone.Calls, c => c[0] == "run");
    }

    [Fact]
    public void NullConfigIsReportedAsUnknownOwnership()
    {
        var fake = new Fake { Exists = true };
        fake.InspectOutput = JsonSerializer.Serialize(new[] { new { fake.Id, fake.Name, fake.State, Config = (object?)null } });
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Destroy("first"));
        Assert.DoesNotContain(fake.Calls, c => c[0] == "remove");
    }
}
