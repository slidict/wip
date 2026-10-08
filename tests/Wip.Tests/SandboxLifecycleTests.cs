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
        public readonly Dictionary<string, (string Id, string Owner, string State, List<SandboxMount> Mounts)> Containers = [];
        public List<SandboxMount> Mounts = [];
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
        public SandboxConsoleMode ExecConsole;
        public readonly string Name = SandboxLifecycle.BackendName("test", "first");

        public SandboxCommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            Calls.Add(argv.ToArray());
            if (argv[0] == "list")
            {
                if (ListOutput is not null) return new(ProbeCode, ListOutput);
                var filterIndex = argv.ToList().IndexOf("--filter");
                if (filterIndex >= 0 && filterIndex + 1 < argv.Count && argv[filterIndex + 1].StartsWith("name="))
                {
                    var filterName = argv[filterIndex + 1][5..];
                    if (Containers.TryGetValue(filterName, out var container))
                    {
                        return new(ProbeCode, JsonSerializer.Serialize(new[] { new { container.Id, Name = filterName, container.State } }));
                    }
                    if (Containers.Count > 0)
                    {
                        return new(ProbeCode, "[]");
                    }
                }
                return new(ProbeCode, Exists ? JsonSerializer.Serialize(new[] { new { Id, Name, State } }) : "[]");
            }
            if (argv[0] == "inspect")
            {
                if (InspectOutput is not null) return new(ProbeCode, InspectOutput);
                var inspectId = argv[^1];
                var match = Containers.Values.FirstOrDefault(c => c.Id == inspectId);
                if (match.Id is not null)
                {
                    var cName = Containers.First(kv => kv.Value == match).Key;
                    var mountsObj = match.Mounts.Select(m => new { Type = m.Type, Name = m.Name, Destination = m.Destination, Source = m.Source, RW = !m.ReadOnly }).ToArray();
                    return new(ProbeCode, JsonSerializer.Serialize(new[] { new { Id = inspectId, Name = cName, match.State, Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = match.Owner }, Mounts = mountsObj } }));
                }
                var defaultMounts = Mounts.Select(m => new { Type = m.Type, Name = m.Name, Destination = m.Destination, Source = m.Source, RW = !m.ReadOnly }).ToArray();
                return new(ProbeCode, JsonSerializer.Serialize(new[] { new { Id, Name, State, Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = Owner }, Mounts = defaultMounts } }));
            }
            Assert.NotEqual(SandboxConsoleMode.Capture, console);
            if (argv[0] is "exec" or "attach") { ExecTimeout = timeout; ExecConsole = console; return new(ExecCode, ""); }
            Assert.Equal(TimeSpan.FromMinutes(2), timeout);
            if (argv[0] == "run" && (MutationCode == 0 || LeaveResidue))
            {
                Exists = true;
                State = "running";
                var nameIdx = argv.ToList().IndexOf("--name");
                var labelIdx = argv.ToList().IndexOf("--label");
                var cName = nameIdx >= 0 && nameIdx + 1 < argv.Count ? argv[nameIdx + 1] : Name;
                var cOwner = labelIdx >= 0 && labelIdx + 1 < argv.Count && argv[labelIdx + 1].StartsWith(SandboxLifecycle.OwnerLabel + "=")
                    ? argv[labelIdx + 1][(SandboxLifecycle.OwnerLabel.Length + 1)..] : Owner;
                var cId = cName == Name ? Id : Id + "-" + cName;
                var mounts = new List<SandboxMount>();
                for (int i = 0; i < argv.Count - 1; i++)
                {
                    if (argv[i] == "--mount")
                    {
                        var parts = argv[i + 1].Split(',');
                        var mType = parts.FirstOrDefault(p => p.StartsWith("type="))?[5..] ?? "volume";
                        var mSrc = parts.FirstOrDefault(p => p.StartsWith("source="))?[7..] ?? "";
                        var mTgt = parts.FirstOrDefault(p => p.StartsWith("target="))?[7..] ?? "";
                        mounts.Add(mType == "bind"
                            ? new(mType, "", mTgt, mSrc, parts.Contains("readonly"))
                            : new(mType, mSrc, mTgt));
                    }
                }
                Mounts = mounts;
                Containers[cName] = (cId, cOwner, "running", mounts);
            }
            if (argv[0] == "start" && MutationCode == 0) State = "running";
            if (argv[0] == "stop" && MutationCode == 0 && !LeaveResidue) State = "exited";
            if (argv[0] == "remove" && MutationCode == 0 && !LeaveResidue)
            {
                Exists = false;
                Mounts = [];
                var targetId = argv[^1];
                var key = Containers.FirstOrDefault(kv => kv.Value.Id == targetId).Key;
                if (key is not null) Containers.Remove(key);
            }
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
        Assert.Equal(SandboxConsoleMode.Stream, fake.ExecConsole);
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

    [Fact]
    public void StopUsesVerifiedIdAndCreateResumesTheSameContainer()
    {
        var fake = new Fake { Exists = true };
        fake.Mounts.Add(new("volume", "fixture-data", "/data"));
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal(0, service.Stop("first"));
        Assert.Equal("exited", service.Status("first").State);
        Assert.Equal(0, service.Stop("first"));
        Assert.Equal(0, service.Create("first"));
        Assert.Equal("running", service.Status("first").State);
        Assert.Equal(["stop", fake.Id], fake.Calls.Single(c => c[0] == "stop"));
        Assert.Equal(["start", fake.Id], fake.Calls.Single(c => c[0] == "start"));
        Assert.Single(fake.Mounts);
        Assert.DoesNotContain(fake.Calls, c => c[0] is "remove" or "run" or "volume");
    }

    [Theory]
    [InlineData(false, "running")]
    [InlineData(true, "created")]
    [InlineData(true, "exited")]
    public void MissingOrAlreadyStoppedSandboxIsANoop(bool exists, string state)
    {
        var fake = new Fake { Exists = exists, State = state };
        Assert.Equal(0, new SandboxLifecycle(Settings(), fake.Run).Stop("first"));
        Assert.All(fake.Calls, c => Assert.Contains(c[0], new[] { "list", "inspect" }));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("paused")]
    [InlineData("restarting")]
    public void UnsupportedStopStateDoesNotMutate(string state)
    {
        var fake = new Fake { Exists = true, State = state };
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Stop("first"));
        Assert.DoesNotContain(fake.Calls, c => c[0] == "stop");
    }

    [Theory]
    [InlineData(124, "[]")]
    [InlineData(1, "[]")]
    [InlineData(0, "broken")]
    [InlineData(0, "null")]
    public void StopRejectsFailedOrMalformedProbeWithoutMutation(int code, string output)
    {
        var fake = new Fake { ProbeCode = code, ListOutput = output };
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Stop("first"));
        Assert.Single(fake.Calls);
    }

    [Theory]
    [InlineData(124)]
    [InlineData(7)]
    public void FailedStopPreservesExitCodeWithoutRetry(int code)
    {
        var fake = new Fake { Exists = true, MutationCode = code };
        Assert.Equal(code, new SandboxLifecycle(Settings(), fake.Run).Stop("first"));
        Assert.Single(fake.Calls, c => c[0] == "stop");
        Assert.Equal(3, fake.Calls.Count); // Initial probes and exactly one mutation.
    }

    [Theory]
    [InlineData("running")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("replacement")]
    [InlineData("malformed")]
    [InlineData("probe-timeout")]
    public void UnconfirmedStopOutcomeFailsClosedWithoutRetry(string outcome)
    {
        var fake = new Fake { Exists = true };
        SandboxCommandResult Backend(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            var result = fake.Run(argv, timeout, console);
            if (argv[0] == "stop")
            {
                if (outcome is "running" or "unknown") fake.State = outcome;
                if (outcome == "missing") fake.Exists = false;
                if (outcome == "replacement") fake.Id = "replacement-id";
                if (outcome == "malformed") fake.InspectOutput = "broken";
                if (outcome == "probe-timeout") fake.ProbeCode = 124;
            }
            return result;
        }
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), Backend).Stop("first"));
        Assert.Single(fake.Calls, c => c[0] == "stop");
        Assert.DoesNotContain(fake.Calls, c => c[0] is "remove" or "volume");
    }

    [Fact]
    public void StopRejectsChangedOrUnlabelledOwnershipBeforeMutation()
    {
        foreach (var inspect in new[] { "[{\"Id\":\"replacement\",\"Name\":\"other\"}]",
            JsonSerializer.Serialize(new[] { new { Id = "backend-id", Name = SandboxLifecycle.BackendName("test", "first"), State = "running" } }) })
        {
            var fake = new Fake { Exists = true, InspectOutput = inspect };
            Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), fake.Run).Stop("first"));
            Assert.DoesNotContain(fake.Calls, c => c[0] == "stop");
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("destroy")]
    [InlineData("stop")]
    [InlineData("exec")]
    public void ForeignContainerCannotBeAdoptedExecutedOrDeleted(string operation)
    {
        var fake = new Fake { Exists = true, Owner = "someone-else" };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Throws<WipException>(() => operation switch
        {
            "create" => service.Create("first"),
            "destroy" => service.Destroy("first"),
            "stop" => service.Stop("first"),
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

    /// <summary>
    /// The interactive session is the same verified-ID exec with wip's console handed over:
    /// a terminal is requested, the deadline is gone, and the child's status still comes back.
    /// </summary>
    [Fact]
    public void InteractiveExecRequestsATerminalAndRunsWithoutADeadline()
    {
        var fake = new Fake { Exists = true, ExecCode = 7 };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        string[] argv = ["sh", "-c", "read line; exit 7"];
        Assert.Equal(7, service.ExecInteractive("first", argv, tty: true));
        Assert.Equal(new[] { "exec", "-i", "-t", fake.Id }.Concat(argv), fake.Calls.Single(c => c[0] == "exec"));
        Assert.Equal(SandboxConsoleMode.Interactive, fake.ExecConsole);
        Assert.Equal(Timeout.InfiniteTimeSpan, fake.ExecTimeout);
    }

    /// <summary>
    /// Without a terminal of its own wip must not ask WSLC for one, but it must still
    /// attach stdin: dropping -i as well would silently turn a piped script into a session
    /// the child never receives, which is how this was first found against a real container.
    /// </summary>
    [Fact]
    public void InteractiveExecWithoutATerminalKeepsTheSessionAndDropsTheTtyRequest()
    {
        var fake = new Fake { Exists = true };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal(0, service.ExecInteractive("first", ["sh"], tty: false));
        Assert.Equal(["exec", "-i", fake.Id, "sh"], fake.Calls.Single(c => c[0] == "exec"));
        Assert.Equal(SandboxConsoleMode.Interactive, fake.ExecConsole);
        Assert.Equal(Timeout.InfiniteTimeSpan, fake.ExecTimeout);
    }

    /// <summary>
    /// Interactive execution never creates a sandbox implicitly and rejects the same argv
    /// the non-interactive path rejects, both before the backend is reached.
    /// </summary>
    [Fact]
    public void InteractiveExecRequiresARunningSandboxAndAnExecutable()
    {
        var fake = new Fake();
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Throws<WipException>(() => service.ExecInteractive("first", ["bash"], tty: true));
        Assert.Throws<ConfigException>(() => service.ExecInteractive("first", [], tty: true));
        Assert.Throws<ConfigException>(() => service.ExecInteractive("first", ["--help"], tty: true));
        Assert.Throws<ConfigException>(() => service.ExecInteractive("missing", ["bash"], tty: true));
        Assert.DoesNotContain(fake.Calls, c => c[0] is "run" or "exec");
    }

    /// <summary>
    /// Attach joins the main process by verified ID: no argv, no deadline, and the status it
    /// reports is the one that process ended with.
    /// </summary>
    [Fact]
    public void AttachJoinsTheMainProcessByVerifiedIdWithoutArgvOrADeadline()
    {
        var fake = new Fake { Exists = true, ExecCode = 7 };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal(7, service.Attach("first"));
        Assert.Equal(["attach", fake.Id], fake.Calls.Single(c => c[0] == "attach"));
        Assert.Equal(SandboxConsoleMode.Interactive, fake.ExecConsole);
        Assert.Equal(Timeout.InfiniteTimeSpan, fake.ExecTimeout);
        Assert.DoesNotContain(fake.Calls, c => c[0] == "exec");
    }

    [Fact]
    public void AttachRequiresARunningSandboxAndAKnownDefinition()
    {
        var fake = new Fake();
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Throws<WipException>(() => service.Attach("first"));
        Assert.Throws<ConfigException>(() => service.Attach("missing"));
        Assert.DoesNotContain(fake.Calls, c => c[0] is "run" or "attach");
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
        Assert.Throws<ConfigException>(() => service.Exec("first", ["true"], TimeSpan.FromSeconds(int.MaxValue)));
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

    private static string FakeVolBackend(string name) => VolumeLifecycle.BackendPrefix("test", name) + "00000000000000000000000000000001";

    private sealed class FakeVolumeLifecycle : IVolumeLifecycle
    {
        public readonly List<string> CreateCalls = [];
        public readonly List<string> ReconcileCalls = [];
        public readonly List<string> DestroyCalls = [];
        public readonly Dictionary<string, VolumeStatus> Statuses = [];
        public int CreateCode { get; set; }
        public int ReconcileCode { get; set; }
        public int DestroyCode { get; set; }

        public VolumeStatus Status(string name)
        {
            if (Statuses.TryGetValue(name, out var status)) return status;
            return new VolumeStatus(name, FakeVolBackend(name), true, [], true);
        }

        public int Create(string name)
        {
            CreateCalls.Add(name);
            if (!Statuses.ContainsKey(name))
            {
                Statuses[name] = new VolumeStatus(name, FakeVolBackend(name), true, [], true);
            }
            return CreateCode;
        }

        public int Destroy(string name)
        {
            DestroyCalls.Add(name);
            return DestroyCode;
        }

        public int Reconcile(string name)
        {
            ReconcileCalls.Add(name);
            return ReconcileCode;
        }
    }

    [Fact]
    public void MountDeclaredVolumesWithCorrectMountArgumentsAndReconcilesUsage()
    {
        var settings = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: data
                persistent: true
                mount: /data
              - name: scratch
                persistent: false
                mount: /scratch
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [data, scratch]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        Assert.Equal(0, service.Create("first"));
        Assert.Equal(["data", "scratch"], fakeVolumes.CreateCalls);
        Assert.Equal(["data", "scratch"], fakeVolumes.ReconcileCalls);

        var runCall = fake.Calls.Single(c => c[0] == "run");
        Assert.Equal([
            "run",
            "--name", SandboxLifecycle.BackendName("test", "first"),
            "-d",
            "--label", "io.slidict.wip.owner=v1:test:sandbox:first",
            "--mount", $"type=volume,source={FakeVolBackend("data")},target=/data",
            "--mount", $"type=volume,source={FakeVolBackend("scratch")},target=/scratch",
            "fixture:latest"
        ], runCall);

        Assert.Equal(0, service.Destroy("first"));
        Assert.Equal(["data", "scratch", "data", "scratch", "data", "scratch"], fakeVolumes.ReconcileCalls);
    }

    [Fact]
    public void AncestorDescendantMountOrderIsMaintainedSoShallowerPrecedesDeeper()
    {
        var settings = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: child
                persistent: true
                mount: /workspace/sub
              - name: parent
                persistent: true
                mount: /workspace
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [child, parent]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        Assert.Equal(0, service.Create("first"));
        var runCall = fake.Calls.Single(c => c[0] == "run");
        var parentIdx = Array.IndexOf(runCall, $"type=volume,source={FakeVolBackend("parent")},target=/workspace");
        var childIdx = Array.IndexOf(runCall, $"type=volume,source={FakeVolBackend("child")},target=/workspace/sub");
        Assert.True(parentIdx > 0 && childIdx > 0 && parentIdx < childIdx);
    }

    [Fact]
    public void SharedVolumeMountAcrossMultipleSandboxesUsesIdenticalBackendVolume()
    {
        var settings = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: shared
                persistent: true
                mount: /shared
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [shared]
              - name: second
                image: fixture:latest
                volumes: [shared]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        Assert.Equal(0, service.Create("first"));
        Assert.Equal(0, service.Create("second"));

        var runFirst = fake.Calls.Single(c => c[0] == "run" && c[2] == SandboxLifecycle.BackendName("test", "first"));
        var runSecond = fake.Calls.Single(c => c[0] == "run" && c[2] == SandboxLifecycle.BackendName("test", "second"));
        Assert.Contains($"type=volume,source={FakeVolBackend("shared")},target=/shared", runFirst);
        Assert.Contains($"type=volume,source={FakeVolBackend("shared")},target=/shared", runSecond);

        Assert.Equal(0, service.Destroy("first"));
        Assert.Equal("running", service.Status("second").State);
        Assert.Equal(0, service.Destroy("second"));
        Assert.Equal("not found", service.Status("second").State);
    }

    [Fact]
    public void IsolatedVolumesAreMountedSeparatelyBetweenSandboxes()
    {
        var settings = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: vol1
                persistent: true
                mount: /data
              - name: vol2
                persistent: true
                mount: /data
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [vol1]
              - name: second
                image: fixture:latest
                volumes: [vol2]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        Assert.Equal(0, service.Create("first"));
        Assert.Equal(0, service.Create("second"));

        var runFirst = fake.Calls.Single(c => c[0] == "run" && c[2] == SandboxLifecycle.BackendName("test", "first"));
        var runSecond = fake.Calls.Single(c => c[0] == "run" && c[2] == SandboxLifecycle.BackendName("test", "second"));
        Assert.Contains($"type=volume,source={FakeVolBackend("vol1")},target=/data", runFirst);
        Assert.DoesNotContain($"type=volume,source={FakeVolBackend("vol2")},target=/data", runFirst);
        Assert.Contains($"type=volume,source={FakeVolBackend("vol2")},target=/data", runSecond);
        Assert.DoesNotContain($"type=volume,source={FakeVolBackend("vol1")},target=/data", runSecond);

        Assert.Equal(0, service.Destroy("first"));
        Assert.Equal("running", service.Status("second").State);
        Assert.Equal(0, service.Destroy("second"));
    }

    [Fact]
    public void FailedVolumeCreateAbortsBeforeContainerRunAndRetainsStorage()
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
        var fakeVolumes = new FakeVolumeLifecycle { CreateCode = 124 };
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        Assert.Equal(124, service.Create("first"));
        Assert.DoesNotContain(fake.Calls, c => c[0] == "run");
        Assert.Empty(fakeVolumes.DestroyCalls);
    }

    [Fact]
    public void FailedContainerRunPreservesStorageWithoutDestructiveRollback()
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
        var fake = new Fake { MutationCode = 1 };
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        Assert.Equal(1, service.Create("first"));
        Assert.Single(fake.Calls, c => c[0] == "run");
        Assert.Empty(fakeVolumes.DestroyCalls);
    }

    [Fact]
    public void RootMountDestinationIsSafelyRejectedBeforeBackendCalls()
    {
        var rawSettings = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: rootvol
                persistent: true
                mount: /
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [rootvol]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(rawSettings, fake.Run, fakeVolumes);

        var ex = Assert.Throws<ConfigException>(() => service.Create("first"));
        Assert.Contains("destination cannot be '/'", ex.Message);
        Assert.Empty(fake.Calls);
        Assert.Empty(fakeVolumes.CreateCalls);
    }

    [Fact]
    public void ExistingContainerMountDriftThrowsAndRequiresRecreate()
    {
        var settingsOriginal = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: v1
                persistent: true
                mount: /data1
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [v1]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service1 = new SandboxLifecycle(settingsOriginal, fake.Run, fakeVolumes);
        Assert.Equal(0, service1.Create("first"));

        var settingsDrift = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: v1
                persistent: true
                mount: /data1
              - name: v2
                persistent: true
                mount: /data2
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [v1, v2]
            """, allowAliases: false)).SandboxResources;
        var service2 = new SandboxLifecycle(settingsDrift, fake.Run, fakeVolumes);
        var ex = Assert.Throws<WipException>(() => service2.Create("first"));
        Assert.Contains("existing container mounts do not match declared volumes", ex.Message);
    }

    [Fact]
    public void ExistingStoppedContainerMountDriftThrowsAndRefusesStart()
    {
        var settingsOriginal = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: v1
                persistent: true
                mount: /data1
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [v1]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service1 = new SandboxLifecycle(settingsOriginal, fake.Run, fakeVolumes);
        Assert.Equal(0, service1.Create("first"));
        var key = fake.Containers.Keys.First();
        var c = fake.Containers[key];
        fake.Containers[key] = (c.Id, c.Owner, "exited", c.Mounts);
        fake.State = "exited";

        var settingsDrift = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: v2
                persistent: true
                mount: /data2
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [v2]
            """, allowAliases: false)).SandboxResources;
        var service2 = new SandboxLifecycle(settingsDrift, fake.Run, fakeVolumes);
        var ex = Assert.Throws<WipException>(() => service2.Create("first"));
        Assert.Contains("existing container mounts do not match declared volumes", ex.Message);
        Assert.DoesNotContain(fake.Calls, c => c[0] == "start");
    }

    [Fact]
    public void ReconciliationFailureInCreatePropagatesNonZeroCode()
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
        var fakeVolumes = new FakeVolumeLifecycle { ReconcileCode = 37 };
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        // Initial create failure
        Assert.Equal(37, service.Create("first"));

        // Idempotent create failure on running container
        fakeVolumes.ReconcileCode = 0;
        fake.Exists = true;
        fake.State = "running";
        fake.Mounts = [new SandboxMount("volume", FakeVolBackend("data"), "/data")];
        Assert.Equal(0, service.Create("first"));

        fakeVolumes.ReconcileCode = 42;
        Assert.Equal(42, service.Create("first"));
    }

    [Fact]
    public void DestroyReconcilesUsageBeforeAndAfterContainerRemoval()
    {
        var settings = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: temp
                persistent: false
                mount: /scratch
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [temp]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        // Simulate residual container created but run failed before reconcile
        fake.MutationCode = 1;
        fake.LeaveResidue = true;
        Assert.Equal(1, service.Create("first"));
        Assert.Empty(fakeVolumes.ReconcileCalls);

        // Now destroy the residual container
        fake.MutationCode = 0;
        fake.LeaveResidue = false;
        Assert.Equal(0, service.Destroy("first"));
        // Reconcile was called before removal AND after removal
        Assert.Equal(["temp", "temp"], fakeVolumes.ReconcileCalls);
    }

    [Fact]
    public void IdempotentCreateSucceedsWithImageDeclaredAnonymousVolume()
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
        var fakeVolumes = new FakeVolumeLifecycle();
        var service = new SandboxLifecycle(settings, fake.Run, fakeVolumes);

        // Container is running with declared wip volume AND an anonymous image volume
        fake.Exists = true;
        fake.State = "running";
        fake.Mounts = [
            new SandboxMount("volume", FakeVolBackend("data"), "/data"),
            new SandboxMount("volume", "a1b2c3d4e5f67890a1b2c3d4e5f67890", "/var/log"),
        ];

        Assert.Equal(0, service.Create("first"));
        Assert.Equal(["data"], fakeVolumes.ReconcileCalls);
    }

    [Fact]
    public void DestroyReconcilesMountedVolumesEvenIfRemovedFromSandboxDeclaration()
    {
        var settingsOriginal = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: oldvol
                persistent: false
                mount: /old
              - name: newvol
                persistent: false
                mount: /new
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [oldvol]
            """, allowAliases: false)).SandboxResources;
        var fake = new Fake();
        var fakeVolumes = new FakeVolumeLifecycle();
        var service1 = new SandboxLifecycle(settingsOriginal, fake.Run, fakeVolumes);
        Assert.Equal(0, service1.Create("first"));
        fakeVolumes.ReconcileCalls.Clear();

        // Now sandbox declaration changes to use newvol, but container still has oldvol mounted
        var settingsModified = new Config(YamlLoader.LoadText("""
            version: 1
            resource_namespace: test
            volumes:
              - name: oldvol
                persistent: false
                mount: /old
              - name: newvol
                persistent: false
                mount: /new
            sandboxes:
              - name: first
                image: fixture:latest
                volumes: [newvol]
            """, allowAliases: false)).SandboxResources;
        var service2 = new SandboxLifecycle(settingsModified, fake.Run, fakeVolumes);
        Assert.Equal(0, service2.Destroy("first"));

        // Both oldvol (actually mounted) and newvol (declared) should be reconciled
        Assert.Contains("oldvol", fakeVolumes.ReconcileCalls);
        Assert.Contains("newvol", fakeVolumes.ReconcileCalls);
    }

    private const string RelaySuffix = "\n    report_relay: true";
    private const string RelayDirectory = "/host/project/.wip/report-relay/first";

    private static readonly KeyValuePair<string, string>[] RelayEnvironment =
    [
        new("WIP_REPORT_SOCKET", "/run/wip/herdr/report.sock"),
        new("HERDR_PANE_ID", "1-2"),
    ];

    /// <summary>
    /// Without the relay, create keeps its exact argv and needs no relay directory, so
    /// existing wip.yml files see no change.
    /// </summary>
    [Fact]
    public void CreateWithoutRelayAddsNoBindMount()
    {
        var fake = new Fake();
        Assert.Equal(0, new SandboxLifecycle(Settings(), fake.Run).Create("first"));
        Assert.Equal(["run", "--name", fake.Name, "-d", "--label", $"{SandboxLifecycle.OwnerLabel}=v1:test:sandbox:first", "fixture:latest"],
            fake.Calls.Single(c => c[0] == "run"));
    }

    [Fact]
    public void CreateWithRelayBindsTheHostDirectoryReadOnlyAtTheFixedPath()
    {
        var fake = new Fake();
        var service = new SandboxLifecycle(Settings(RelaySuffix), fake.Run, reportRelayDirectory: _ => RelayDirectory);
        Assert.Equal(0, service.Create("first"));
        var run = fake.Calls.Single(c => c[0] == "run");
        var mount = run[Array.IndexOf(run, "--mount") + 1];
        Assert.Equal($"type=bind,source={RelayDirectory},target=/run/wip/herdr,readonly", mount);
        Assert.Equal("fixture:latest", run[^1]);
        // Restarting the same container accepts its relay mount.
        Assert.Equal(0, service.Create("first"));
        Assert.Single(fake.Calls, c => c[0] == "run");
    }

    [Fact]
    public void CreateWithRelayRefusesWithoutADirectoryOrWithAnUnsafeOne()
    {
        var fake = new Fake();
        Assert.Throws<ConfigException>(() => new SandboxLifecycle(Settings(RelaySuffix), fake.Run).Create("first"));
        Assert.Throws<ConfigException>(() =>
            new SandboxLifecycle(Settings(RelaySuffix), fake.Run, reportRelayDirectory: _ => "/a,readonly=false").Create("first"));
        Assert.Empty(fake.Calls);
    }

    /// <summary>Mounts are fixed at creation, so toggling report_relay needs a recreate.</summary>
    [Fact]
    public void ExistingContainerWhoseRelayMountDisagreesWithConfigIsReported()
    {
        var withoutMount = new Fake { Exists = true };
        Assert.Throws<WipException>(() =>
            new SandboxLifecycle(Settings(RelaySuffix), withoutMount.Run, reportRelayDirectory: _ => RelayDirectory).Create("first"));

        var withMount = new Fake { Exists = true, Mounts = [new("bind", "", "/run/wip/herdr", RelayDirectory, ReadOnly: true)] };
        Assert.Throws<WipException>(() => new SandboxLifecycle(Settings(), withMount.Run).Create("first"));
        Assert.DoesNotContain(withoutMount.Calls.Concat(withMount.Calls), c => c[0] is "run" or "start" or "remove");
    }

    /// <summary>
    /// Only the read-only bind of the configured directory is reused: a writable bind lets
    /// the sandbox write into the host directory, and another source is another socket.
    /// </summary>
    [Theory]
    [InlineData(RelayDirectory, false)]
    [InlineData("/host/elsewhere", true)]
    [InlineData("/host/project/.wip/report-relay/second", true)]
    [InlineData("", true)]
    public void ExistingRelayBindMustBeReadOnlyAndFromTheConfiguredDirectory(string source, bool readOnly)
    {
        var fake = new Fake { Exists = true, Mounts = [new("bind", "", "/run/wip/herdr", source, readOnly)] };
        var service = new SandboxLifecycle(Settings(RelaySuffix), fake.Run, reportRelayDirectory: _ => RelayDirectory);

        Assert.Throws<WipException>(() => service.Create("first"));
        Assert.DoesNotContain(fake.Calls, c => c[0] is "run" or "start" or "remove");
    }

    [Fact]
    public void ExistingRelayBindIsReusedWhenItMatchesAndAcceptsTrailingSeparator()
    {
        var fake = new Fake { Exists = true, Mounts = [new("bind", "", "/run/wip/herdr", RelayDirectory + "/", ReadOnly: true)] };
        Assert.Equal(0, new SandboxLifecycle(Settings(RelaySuffix), fake.Run, reportRelayDirectory: _ => RelayDirectory).Create("first"));
        Assert.DoesNotContain(fake.Calls, c => c[0] is "run" or "start" or "remove");
    }

    [Fact]
    public void DuplicateRelayBindsAreRefused()
    {
        var fake = new Fake
        {
            Exists = true,
            Mounts = [new("bind", "", "/run/wip/herdr", RelayDirectory, true), new("bind", "", "/run/wip/herdr", "/host/elsewhere", true)],
        };
        Assert.Throws<WipException>(() =>
            new SandboxLifecycle(Settings(RelaySuffix), fake.Run, reportRelayDirectory: _ => RelayDirectory).Create("first"));
    }

    /// <summary>Docker's inspect reports RW; the read-only state is what the relay check relies on.</summary>
    [Fact]
    public void StatusKeepsBindSourceAndReadOnlyState()
    {
        var fake = new Fake
        {
            Exists = true,
            InspectOutput = $$"""
                [{"Id":"backend-id","Name":"{{SandboxLifecycle.BackendName("test", "first")}}","State":"running",
                  "Labels":{"{{SandboxLifecycle.OwnerLabel}}":"v1:test:sandbox:first"},
                  "Mounts":[{"Type":"bind","Source":"/host/a","Destination":"/run/wip/herdr/","RW":false},
                            {"Type":"bind","Source":"/host/b","Destination":"/b","RW":true},
                            {"Type":"bind","Source":"/host/c","Destination":"/c","ReadOnly":true},
                            {"Type":"bind","Source":"/host/d","Destination":"/d"}]}]
                """,
        };

        var mounts = new SandboxLifecycle(Settings(), fake.Run).Status("first").Mounts;

        Assert.Equal(
            [
                new SandboxMount("bind", "", "/run/wip/herdr", "/host/a", true),
                new SandboxMount("bind", "", "/b", "/host/b", false),
                new SandboxMount("bind", "", "/c", "/host/c", true),
                new SandboxMount("bind", "", "/d", "/host/d", false),
            ],
            mounts);
    }

    [Fact]
    public void ExecExportsEnvironmentBeforeTheContainerId()
    {
        var fake = new Fake { Exists = true };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        Assert.Equal(0, service.Exec("first", ["true"], TimeSpan.FromSeconds(5), RelayEnvironment));
        Assert.Equal(["exec", "-e", "WIP_REPORT_SOCKET=/run/wip/herdr/report.sock", "-e", "HERDR_PANE_ID=1-2", fake.Id, "true"],
            fake.Calls.Single(c => c[0] == "exec"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InteractiveExecExportsEnvironmentAndEmptyEnvironmentChangesNothing(bool tty)
    {
        var fake = new Fake { Exists = true };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        string[] prefix = tty ? ["exec", "-i", "-t"] : ["exec", "-i"];

        Assert.Equal(0, service.ExecInteractive("first", ["sh"], tty, RelayEnvironment));
        Assert.Equal(prefix.Concat(["-e", "WIP_REPORT_SOCKET=/run/wip/herdr/report.sock", "-e", "HERDR_PANE_ID=1-2", fake.Id, "sh"]),
            fake.Calls.Single(c => c[0] == "exec"));

        fake.Calls.Clear();
        Assert.Equal(0, service.ExecInteractive("first", ["sh"], tty, []));
        Assert.Equal(prefix.Concat([fake.Id, "sh"]), fake.Calls.Single(c => c[0] == "exec"));
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("A=B", "x")]
    [InlineData("-e", "x")]
    [InlineData("HERDR_PANE_ID", "1-2\n")]
    public void InvalidEnvironmentIsRejectedBeforeAnyBackendCall(string key, string value)
    {
        var fake = new Fake { Exists = true };
        var service = new SandboxLifecycle(Settings(), fake.Run);
        KeyValuePair<string, string>[] environment = [new(key, value)];
        Assert.Throws<ConfigException>(() => service.ExecInteractive("first", ["sh"], tty: true, environment));
        Assert.Throws<ConfigException>(() => service.Exec("first", ["sh"], TimeSpan.FromSeconds(5), environment));
        Assert.Empty(fake.Calls);
    }
}
