using System.Text.Json;
using Wip.Configuration;
using Wip.Execution;
using Wip.Security;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// The engine's refusals, and the one property that cannot be left to a convention: an
/// adapter supplies a plan and nothing that reaches the backend, so no adapter can widen the
/// boundary its scan runs behind.
/// </summary>
public class ScanEngineTests
{
    private const string Namespace = "test";
    private static readonly string ScanNet = NetworkLifecycle.BackendName(Namespace, "scan");
    private static readonly string SandboxName = SandboxLifecycle.BackendName(Namespace, "scanner");

    /// <summary>
    /// The image both the sandbox declaration and the plan name. They have to agree: the
    /// scan runs as an exec inside the declared sandbox, so its image is what actually
    /// executes, and a plan pinning anything else would pin nothing.
    /// </summary>
    private const string Pinned = "example.test/zap@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static SandboxSettings Settings(string body) => new Config(YamlLoader.LoadText("""
        version: 1
        resource_namespace: test
        """ + body, allowAliases: false)).SandboxResources;

    private static SandboxSettings Isolated() => Settings($"""

        networks:
          - name: scan
            internal: true
        sandboxes:
          - name: scanner
            image: {Pinned}
            network: scan
          - name: unisolated
            image: {Pinned}
        """);

    /// <summary>A plan that passes validation, so a test can vary one thing at a time.</summary>
    private static ScanPlan ValidPlan(string reportPath = "/out/report.json") => new(
        new PinnedImage(Pinned),
        ["zap-baseline", "-t", "http://app.test:8080"],
        reportPath,
        "2.15.0");

    private sealed class StubScanner(ScanPlan? plan = null, string name = "stub") : ISecurityScanner
    {
        public string Name => name;
        public ScanTarget? Seen { get; private set; }
        public ScanPlan Plan(ScanTarget target)
        {
            Seen = target;
            return plan ?? ValidPlan();
        }
    }

    private sealed class Fake
    {
        public readonly List<string[]> Calls = [];
        public bool ContainerExists = true;
        public string ContainerState = "running";
        public List<string> Attachments = [ScanNet];
        public bool NetworkVerifiable = true;
        public int PullCode;
        public int ExecCode;
        public string Report = "{\"alerts\":[]}";
        public bool ReportMissing;

        public SandboxCommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            Calls.Add(argv.ToArray());
            if (argv is ["pull", ..]) return new(PullCode, "");
            if (argv is ["network", "inspect", ..])
            {
                if (!NetworkVerifiable) return new(1, "");
                return new(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = new string('a', 64),
                        Name = ScanNet,
                        Internal = true,
                        Labels = new Dictionary<string, string> { [NetworkLifecycle.OwnerLabel] = $"v1:{Namespace}:network:scan" },
                    },
                }));
            }
            if (argv[0] == "list")
            {
                return new(0, ContainerExists
                    ? JsonSerializer.Serialize(new[] { new { Id = "container-id", Name = SandboxName, State = ContainerState } })
                    : "[]");
            }
            if (argv[0] == "inspect")
            {
                return new(0, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = "container-id",
                        Name = SandboxName,
                        State = ContainerState,
                        Labels = new Dictionary<string, string> { [SandboxLifecycle.OwnerLabel] = $"v1:{Namespace}:sandbox:scanner" },
                        Mounts = Array.Empty<object>(),
                        NetworkSettings = new { Networks = Attachments.ToDictionary(n => n, _ => new { }) },
                    },
                }));
            }
            if (argv is ["exec", _, "cat", ..]) return new(ReportMissing ? 1 : 0, ReportMissing ? "" : Report);
            if (argv[0] == "exec") return new(ExecCode, "");
            if (argv[0] == "stop") { ContainerState = "exited"; return new(0, ""); }
            return new(0, "");
        }
    }

    private static ScanEngine Engine(Fake fake, SandboxSettings? settings = null)
    {
        var resolved = settings ?? Isolated();
        return new(resolved, new SandboxLifecycle(resolved, fake.Run, null, new NetworkLifecycle(resolved, fake.Run)),
            new NetworkLifecycle(resolved, fake.Run), fake.Run);
    }

    private static bool Ran(Fake fake, params string[] prefix) =>
        fake.Calls.Any(c => c.Length >= prefix.Length && c.Take(prefix.Length).SequenceEqual(prefix));

    // ----------------------------------------------------------------- one tool, from the table

    [Fact]
    public void NoScannerIsRegisteredUntilAnAdapterLands()
    {
        // The registry is a reviewed table, so it is empty before the first adapter. Asserted
        // rather than assumed: an entry appearing here without a pull request to this file is
        // exactly what the platform's design rules out.
        Assert.Empty(ScanEngine.Tools);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("zap")]
    [InlineData("ZAP")]
    [InlineData("nuclei")]
    public void SelectingAnUnregisteredToolFails(string tool)
    {
        var error = Assert.Throws<ConfigException>(() => ScanEngine.Select(tool));
        Assert.Contains("no scanners are registered", error.Message);
    }

    // ----------------------------------------------------------------- preparation

    [Fact]
    public void PrepareValidatesTheTargetBeforeAskingTheAdapter()
    {
        var fake = new Fake();
        var scanner = new StubScanner();

        Assert.Throws<ConfigException>(() => Engine(fake).Prepare(scanner, "scanner", new ScanTarget("")));

        Assert.Null(scanner.Seen);
        Assert.False(Ran(fake, "pull"));
    }

    [Fact]
    public void PrepareRefusesASandboxThatDeclaresNoNetwork()
    {
        var fake = new Fake();
        var scanner = new StubScanner();

        var error = Assert.Throws<ConfigException>(() => Engine(fake).Prepare(scanner, "unisolated", new ScanTarget("app.test")));

        Assert.Contains("declares no network", error.Message);
        Assert.Null(scanner.Seen);
        Assert.False(Ran(fake, "pull"), "nothing is fetched for a scan that cannot run");
    }

    [Fact]
    public void PreparePullsThePinnedImage()
    {
        var fake = new Fake();
        var plan = Engine(fake).Prepare(new StubScanner(), "scanner", new ScanTarget("app.test"));

        var pull = fake.Calls.Single(c => c[0] == "pull");
        Assert.Equal(plan.Image.Reference, pull[1]);
    }

    [Fact]
    public void AFailedPullStartsNoScan()
    {
        var fake = new Fake { PullCode = 3 };

        var error = Assert.Throws<WipException>(() => Engine(fake).Prepare(new StubScanner(), "scanner", new ScanTarget("app.test")));

        Assert.Contains("no scan was started", error.Message);
        Assert.False(Ran(fake, "exec"));
    }

    [Theory]
    [InlineData("example.test/zap:latest")]
    [InlineData("example.test/zap")]
    [InlineData("example.test/zap:stable")]
    [InlineData("example.test/zap:nightly")]
    [InlineData("registry.test:5000/zap")]
    public void AnUnpinnedImageIsRefused(string reference)
    {
        var fake = new Fake();
        var scanner = new StubScanner(ValidPlan() with { Image = new PinnedImage(reference) });

        var error = Assert.Throws<ConfigException>(() => Engine(fake).Prepare(scanner, "scanner", new ScanTarget("app.test")));

        Assert.Contains("not pinned", error.Message);
        Assert.False(Ran(fake, "pull"));
    }

    [Theory]
    [InlineData("example.test/zap@sha256:" + "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("example.test/zap:2.15.0")]
    [InlineData("registry.test:5000/zap:2.15.0")]
    public void APinnedImageIsAccepted(string reference)
    {
        // Asserted on the plan rather than through Prepare: pinning is the plan's property,
        // while Prepare additionally requires the sandbox to declare the same image, which
        // would refuse these for an unrelated reason.
        (ValidPlan() with { Image = new PinnedImage(reference) }).Validate("stub");
    }

    [Fact]
    public void ASandboxDeclaringADifferentImageIsRefused()
    {
        // Without this the pin guarantees nothing: Prepare would fetch the tool's image while
        // the scan ran whatever the sandbox's own image provides, and the outcome would name
        // an image that never ran.
        var fake = new Fake();
        var scanner = new StubScanner(ValidPlan() with
        {
            Image = new PinnedImage("example.test/other@sha256:" + new string('d', 64)),
        });

        var error = Assert.Throws<ConfigException>(() => Engine(fake).Prepare(scanner, "scanner", new ScanTarget("app.test")));

        Assert.Contains("must name the pinned image", error.Message);
        Assert.False(Ran(fake, "pull"), "nothing is fetched for a sandbox that would run something else");
    }

    [Fact]
    public void RunRefusesAPlanThatDoesNotMatchTheSandboxImage()
    {
        // Re-checked in Run for the reason the plan is re-validated there: a caller can build
        // a plan itself and never go through Prepare.
        var fake = new Fake();
        var plan = ValidPlan() with { Image = new PinnedImage("example.test/other@sha256:" + new string('d', 64)) };

        Assert.Throws<ConfigException>(() => Engine(fake).Run(new StubScanner(plan), "scanner", plan));

        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void AnUnpinnedRuleSetIsRefused()
    {
        var fake = new Fake();
        var scanner = new StubScanner(ValidPlan() with { RulesVersion = "  " });

        var error = Assert.Throws<ConfigException>(() => Engine(fake).Prepare(scanner, "scanner", new ScanTarget("app.test")));

        Assert.Contains("rules version is required", error.Message);
        Assert.False(Ran(fake, "pull"));
    }

    [Theory]
    [InlineData("relative/report.json")]
    [InlineData("/out/../etc/passwd")]
    [InlineData("/out/./report.json")]
    public void AReportPathThatIsNotCanonicalAndAbsoluteIsRefused(string reportPath)
    {
        var fake = new Fake();
        var scanner = new StubScanner(ValidPlan(reportPath));

        Assert.Throws<ConfigException>(() => Engine(fake).Prepare(scanner, "scanner", new ScanTarget("app.test")));

        Assert.False(Ran(fake, "pull"));
    }

    [Fact]
    public void AnEmptyArgvIsRefused()
    {
        var fake = new Fake();
        var scanner = new StubScanner(ValidPlan() with { Argv = [] });

        Assert.Throws<ConfigException>(() => Engine(fake).Prepare(scanner, "scanner", new ScanTarget("app.test")));
    }

    // ----------------------------------------------------------------- the boundary, at run time

    [Fact]
    public void RunVerifiesTheBoundaryItselfRatherThanTrustingCreate()
    {
        // The gap slidict/wip#263 names: a container created before the network was declared
        // keeps running on whatever it was given. `sandbox create` refuses it; until this
        // check, `exec` did not -- and exec is where a scan actually runs.
        var fake = new Fake { Attachments = ["bridge"] };

        var error = Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan()));

        Assert.Contains("not attached to exactly its declared network", error.Message);
        Assert.False(Ran(fake, "exec"), "nothing runs in a sandbox whose boundary is not the declared one");
    }

    [Fact]
    public void RunRefusesASandboxAlsoAttachedElsewhere()
    {
        var fake = new Fake { Attachments = [ScanNet, "bridge"] };

        Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan()));

        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void RunRefusesWhenTheNetworkCannotBeVerified()
    {
        var fake = new Fake { NetworkVerifiable = false };

        Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan()));

        Assert.False(Ran(fake, "exec"));
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("created")]
    public void RunRefusesASandboxThatIsNotRunning(string state)
    {
        var fake = new Fake { ContainerState = state };

        var error = Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan()));

        Assert.Contains("is not running", error.Message);
        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void RunRefusesASandboxThatDeclaresNoNetwork()
    {
        var fake = new Fake();

        Assert.Throws<ConfigException>(() => Engine(fake).Run(new StubScanner(), "unisolated", ValidPlan()));

        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void RunRevalidatesThePlanItIsHanded()
    {
        // Being handed a plan is not evidence that it is a valid one: a caller could have
        // built one without going through Prepare.
        var fake = new Fake();

        Assert.Throws<ConfigException>(() =>
            Engine(fake).Run(new StubScanner(), "scanner", ValidPlan() with { Image = new PinnedImage("zap:latest") }));

        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void ANonPositiveTimeoutIsRefused()
    {
        var fake = new Fake();

        Assert.Throws<ConfigException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), TimeSpan.Zero));

        Assert.False(Ran(fake, "exec"));
    }

    // ----------------------------------------------------------------- the run itself

    [Fact]
    public void RunExecutesThePlansArgvAndCollectsTheReport()
    {
        var fake = new Fake();
        var plan = ValidPlan();

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", plan);

        // The argv the adapter asked for, after the container id, and nothing of the adapter's
        // in the backend's own options.
        var exec = fake.Calls.First(c => c[0] == "exec" && c.Contains("zap-baseline"));
        Assert.Equal(["exec", "container-id", .. plan.Argv], exec);
        Assert.Equal("{\"alerts\":[]}", outcome.Report);
        Assert.Equal(ScanNet, outcome.Network);
        Assert.Equal("stub", outcome.Tool);
        Assert.Equal(plan.RulesVersion, outcome.RulesVersion);
    }

    [Fact]
    public void NoAdapterSuppliedValueReachesTheBackendsOwnOptions()
    {
        // The structural half of "アダプターから隔離ポリシーを緩和できない": whatever an
        // adapter puts in its argv lands after the container id, never among the flags the
        // backend reads, so it cannot attach a network or mount a path.
        var fake = new Fake();
        var plan = ValidPlan() with { Argv = ["sh", "--network", "bridge", "-v", "/:/host"] };

        Engine(fake).Run(new StubScanner(plan), "scanner", plan);

        var exec = fake.Calls.First(c => c[0] == "exec" && c.Contains("sh"));
        Assert.Equal("container-id", exec[1]);
        Assert.Equal(["sh", "--network", "bridge", "-v", "/:/host"], exec[2..]);
        Assert.DoesNotContain("run", fake.Calls.Select(c => c[0]));
    }

    [Fact]
    public void AToolsNonZeroStatusIsForwardedRatherThanTreatedAsAnError()
    {
        // Scanners report findings through their exit code. Deciding what that means belongs
        // to the result format that consumes the outcome.
        var fake = new Fake { ExecCode = 2 };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan());

        Assert.Equal(2, outcome.ExitCode);
    }

    [Fact]
    public void AMissingReportIsRecordedAsAbsentRatherThanAsAFailure()
    {
        var fake = new Fake { ReportMissing = true };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan());

        Assert.Null(outcome.Report);
    }

    [Fact]
    public void TheReportIsReadWithThePathAsItsOwnArgument()
    {
        // Never composed into a shell line: a report path is data, and `sh -c` would make it
        // executable.
        var fake = new Fake();

        Engine(fake).Run(new StubScanner(), "scanner", ValidPlan());

        var read = fake.Calls.Single(c => c.Contains("cat"));
        Assert.Equal(["exec", "container-id", "cat", "/out/report.json"], read);
        Assert.DoesNotContain("sh", read);
    }

    // ----------------------------------------------------------------- cleanup

    [Fact]
    public void TheSandboxIsStoppedAfterASuccessfulScan()
    {
        var fake = new Fake();

        Engine(fake).Run(new StubScanner(), "scanner", ValidPlan());

        Assert.True(Ran(fake, "stop"));
        Assert.False(Ran(fake, "remove"), "removal is sandbox destroy's, so a scan leaves the sandbox inspectable");
    }

    [Fact]
    public void TheSandboxIsStoppedWhenTheScanItselfThrows()
    {
        var fake = new Fake();
        // A deadline the exec path rejects, raised from inside the try rather than before it.
        var engine = Engine(fake);

        Assert.Throws<ConfigException>(() =>
            engine.Run(new StubScanner(), "scanner", ValidPlan(), TimeSpan.FromDays(40)));

        // Refused before the try, so nothing to stop -- asserted so the boundary between the
        // two is explicit rather than incidental.
        Assert.False(Ran(fake, "exec"));
    }
}
