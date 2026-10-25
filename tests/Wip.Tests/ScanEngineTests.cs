using System.Text.Json;
using Wip.Cli;
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

    private sealed class StubScanner(ScanPlan? plan = null, string name = "stub", IReadOnlyList<ScanFinding>? findings = null) : ISecurityScanner
    {
        public string Name => name;
        public IReadOnlyList<ScanFinding> ConvertReport(string report)
        {
            using var document = JsonDocument.Parse(report);
            if (!document.RootElement.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array)
                throw new FormatException("Fixture report must contain an alerts array");
            return findings ?? [];
        }
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
        public bool ExecTimedOut;
        public bool ExecThrows;
        public readonly List<TimeSpan> ReportTimeouts = [];
        public string Report = "{\"alerts\":[]}";
        public bool ReportMissing;
        public string Diagnostics = "Not performed: authentication and active scanning\nZAP warning: passive scan incomplete";
        public bool DiagnosticsMissing;
        public bool DiagnosticsTimedOut;
        public int ReportCode;
        public bool ReportTimedOut;
        public bool ReportLimitExceeded;
        public bool ReportThrows;
        public bool StopThrows;
        public Action? BeforeStop;

        /// <summary>
        /// What the running container was created from. Defaults to the declared image; a test
        /// sets it to something else to model a declaration edited after creation.
        /// </summary>
        public string? ContainerImage = Pinned;

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
                        Config = ContainerImage is null ? null : new { Image = ContainerImage },
                        NetworkSettings = new { Networks = Attachments.ToDictionary(n => n, _ => new { }) },
                    },
                }));
            }
            if (argv is ["exec", _, "cat", ..])
            {
                if (argv[^1] == "/out/unperformed.txt")
                    return new(DiagnosticsMissing ? 1 : 0, Diagnostics) { TimedOut = DiagnosticsTimedOut };
                ReportTimeouts.Add(timeout);
                if (ReportThrows) throw new WipException("report capture exceeded its limit");
                return new(ReportMissing ? 1 : ReportCode, ReportMissing ? "" : Report)
                {
                    TimedOut = ReportTimedOut,
                    OutputLimitExceeded = ReportLimitExceeded,
                };
            }
            if (argv[0] == "exec")
            {
                if (ExecThrows) throw new WipException("scanner failed to start");
                return new(ExecCode, "") { TimedOut = ExecTimedOut };
            }
            if (argv[0] == "stop")
            {
                BeforeStop?.Invoke();
                if (StopThrows) throw new InvalidOperationException("stop failed unexpectedly");
                ContainerState = "exited";
                return new(0, "");
            }
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
    public void ZapIsRegisteredInTheReviewedBuiltInTable()
    {
        Assert.Equal(["zap"], ScanEngine.Tools);
        Assert.IsType<ZapScanner>(ScanEngine.Select("zap"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Zap")]
    [InlineData("ZAP")]
    [InlineData("nuclei")]
    public void SelectingAnUnregisteredToolFails(string tool)
    {
        var error = Assert.Throws<ConfigException>(() => ScanEngine.Select(tool));
        Assert.Contains("registered tools: zap", error.Message);
    }

    [Fact]
    public void ScannerContractsStayInternalToWip()
    {
        Assert.False(typeof(PinnedImage).IsPublic);
        Assert.False(typeof(ScanPlan).IsPublic);
        Assert.False(typeof(ScanTarget).IsPublic);
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
    [InlineData("registry.test:5000/zap@sha256:" + "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    public void APinnedImageIsAccepted(string reference)
    {
        // Asserted on the plan rather than through Prepare: pinning is the plan's property,
        // while Prepare additionally requires the sandbox to declare the same image, which
        // would refuse these for an unrelated reason.
        (ValidPlan() with { Image = new PinnedImage(reference) }).Validate("stub");
    }

    [Theory]
    [InlineData("example.test/zap:2.15.0")]
    [InlineData("example.test/zap:release")]
    [InlineData("example.test/zap@sha256:")]
    [InlineData("example.test/zap@sha256:short")]
    [InlineData("example.test/zap@sha256:" + "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void OnlyAnImmutableFullSha256DigestIsPinned(string reference)
    {
        Assert.Throws<ConfigException>(() =>
            (ValidPlan() with { Image = new PinnedImage(reference) }).Validate("stub"));
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
    public void RunRefusesWhenTheContainerRunsADifferentImageThanThePlan()
    {
        // The declaration is not enough on its own: a container is created from whatever the
        // declaration said then, so editing it to another pinned image afterwards leaves the
        // old one running while the plan and the outcome name the new one.
        var fake = new Fake { ContainerImage = "example.test/old@sha256:" + new string('e', 64) };

        var error = Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.Contains("is running image", error.Message);
        Assert.False(Ran(fake, "exec"), "nothing runs in a container that is not the pinned tool");
    }

    [Fact]
    public void RunRefusesWhenTheContainersImageCannotBeRead()
    {
        // Unknown is not a match. Treating an unreadable image as acceptable would make the
        // check pass exactly when it cannot tell.
        var fake = new Fake { ContainerImage = null };

        var error = Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.Contains("could not be read", error.Message);
        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void RunRefusesAPlanThatDoesNotMatchTheSandboxImage()
    {
        // Re-checked in Run for the reason the plan is re-validated there: a caller can build
        // a plan itself and never go through Prepare.
        var fake = new Fake();
        var plan = ValidPlan() with { Image = new PinnedImage("example.test/other@sha256:" + new string('d', 64)) };

        Assert.Throws<ConfigException>(() => Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test:8080")));

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

        var error = Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.Contains("not attached to exactly its declared network", error.Message);
        Assert.False(Ran(fake, "exec"), "nothing runs in a sandbox whose boundary is not the declared one");
    }

    [Fact]
    public void RunRefusesASandboxAlsoAttachedElsewhere()
    {
        var fake = new Fake { Attachments = [ScanNet, "bridge"] };

        Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void RunRefusesWhenTheNetworkCannotBeVerified()
    {
        var fake = new Fake { NetworkVerifiable = false };

        Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.False(Ran(fake, "exec"));
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("created")]
    public void RunRefusesASandboxThatIsNotRunning(string state)
    {
        var fake = new Fake { ContainerState = state };

        var error = Assert.Throws<WipException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.Contains("is not running", error.Message);
        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void RunRefusesASandboxThatDeclaresNoNetwork()
    {
        var fake = new Fake();

        Assert.Throws<ConfigException>(() => Engine(fake).Run(new StubScanner(), "unisolated", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void RunRevalidatesThePlanItIsHanded()
    {
        // Being handed a plan is not evidence that it is a valid one: a caller could have
        // built one without going through Prepare.
        var fake = new Fake();

        Assert.Throws<ConfigException>(() =>
            Engine(fake).Run(new StubScanner(), "scanner", ValidPlan() with { Image = new PinnedImage("zap:latest") }, new ScanTarget("http://app.test:8080")));

        Assert.False(Ran(fake, "exec"));
    }

    [Fact]
    public void ANonPositiveTimeoutIsRefused()
    {
        var fake = new Fake();

        Assert.Throws<ConfigException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"), TimeSpan.Zero));

        Assert.False(Ran(fake, "exec"));
    }

    // ----------------------------------------------------------------- the run itself

    [Fact]
    public void RunExecutesThePlansArgvAndCollectsTheReport()
    {
        var fake = new Fake();
        var plan = ValidPlan();

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", plan, new ScanTarget("http://app.test:8080"));

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
        // Adapters cannot relax isolation: whatever an adapter puts in its argv lands
        // after the container id, never among the flags the
        // backend reads, so it cannot attach a network or mount a path.
        var fake = new Fake();
        var plan = ValidPlan() with { Argv = ["sh", "--network", "bridge", "-v", "/:/host"] };

        Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test:8080"));

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

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Equal(2, outcome.ExitCode);
    }

    [Fact]
    public void AMissingReportIsRecordedAsAbsentRatherThanAsAFailure()
    {
        var fake = new Fake { ReportMissing = true };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Null(outcome.Report);
    }

    [Fact]
    public void TheReportIsReadWithThePathAsItsOwnArgument()
    {
        // Never composed into a shell line: a report path is data, and `sh -c` would make it
        // executable.
        var fake = new Fake();

        Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        var read = fake.Calls.Single(c => c.Contains("cat"));
        Assert.Equal(["exec", "container-id", "cat", "/out/report.json"], read);
        Assert.DoesNotContain("sh", read);
    }

    [Fact]
    public void ATimedOutScannerIsStoppedWithoutReadingAnUnfinishedReport()
    {
        var fake = new Fake { ExecCode = CommandRunner.TimeoutExitCode, ExecTimedOut = true, Report = "unfinished" };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Null(outcome.Report);
        Assert.True(outcome.TimedOut);
        Assert.True(Ran(fake, "stop"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("cat"));
    }

    [Fact]
    public void CollectionUsesAShortDeadlineInsteadOfTheScanTimeout()
    {
        var fake = new Fake();

        Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"), TimeSpan.FromSeconds(60));

        Assert.Single(fake.ReportTimeouts);
        Assert.InRange(fake.ReportTimeouts[0], TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AnOversizedReportPreservesTheScannerStatus()
    {
        var fake = new Fake { ExecCode = 2, Report = new string('x', 1024 * 1024 + 1) };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("limit", outcome.ReportError);
        Assert.True(Ran(fake, "stop"));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliContext.WriteScanOutcome(outcome, "/out/report.json", output, error, isOutputRedirected: true);

        Assert.Equal(ScanEngine.IncompleteExitCode, code);
        Assert.Equal("", output.ToString());
        Assert.Contains($"stub\tscanner\t{ScanNet}\t{Pinned}\trules=2.15.0\texit=2\tstatus=exited", error.ToString());
        Assert.Contains("retention limit", error.ToString());
    }

    [Fact]
    public void AReportReadExceptionPreservesTheScannerStatus()
    {
        var fake = new Fake { ExecCode = 2, ReportThrows = true };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("report capture exceeded", outcome.ReportError);
        Assert.True(Ran(fake, "stop"));
    }

    [Fact]
    public void AReportReadFailureIsNotReportedAsAnAbsentFile()
    {
        var fake = new Fake { ExecCode = 2, ReportCode = 7 };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("exit 7", outcome.ReportError);
    }

    [Fact]
    public void AReportCollectionTimeoutHasItsOwnReason()
    {
        var fake = new Fake { ExecCode = 2, ReportCode = 124, ReportTimedOut = true };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
        Assert.Null(outcome.Report);
        Assert.Contains("timed out", outcome.ReportError);
    }

    [Fact]
    public void ABackendReportLimitPreservesTheScannerStatus()
    {
        var fake = new Fake { ExecCode = 2, ReportLimitExceeded = true };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("limit", outcome.ReportError);
    }

    [Fact]
    public void AScannerThatExitsWith124KeepsItsReport()
    {
        var fake = new Fake { ExecCode = 124 };

        var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.Equal(124, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
        Assert.Equal(fake.Report, outcome.Report);
    }

    [Fact]
    public void AnUnexpectedStopExceptionDoesNotAttemptASecondStop()
    {
        var fake = new Fake { ExecCode = CommandRunner.TimeoutExitCode, ExecTimedOut = true, StopThrows = true };

        Assert.Throws<InvalidOperationException>(() => Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.Single(fake.Calls, c => c[0] == "stop");
    }

    [Fact]
    public void RawUnperformedChecksRemainDistinctFromAnEmptyAlertsReport()
    {
        var fake = new Fake();
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };

        var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test:8080"));

        Assert.Equal("{\"alerts\":[]}", outcome.Report);
        Assert.Equal(fake.Diagnostics, outcome.UnperformedChecks);
        Assert.Null(outcome.UnperformedChecksError);
    }

    [Fact]
    public void MissingUnperformedChecksAreNotReportedAsClean()
    {
        var fake = new Fake { DiagnosticsMissing = true };
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };

        var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test:8080"));

        Assert.Null(outcome.UnperformedChecks);
        Assert.NotNull(outcome.UnperformedChecksError);
    }

    [Fact]
    public void UnperformedChecksAreCollectedEvenWhenTheReportReadFails()
    {
        var fake = new Fake { ReportMissing = true };
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };

        var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test:8080"));

        Assert.Null(outcome.Report);
        Assert.Equal(fake.Diagnostics, outcome.UnperformedChecks);
    }

    [Fact]
    public void AScanTimeoutMarksUnperformedChecksAsUnavailable()
    {
        var fake = new Fake { ExecCode = 124, ExecTimedOut = true };
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };

        var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test:8080"));

        Assert.True(outcome.TimedOut);
        Assert.Null(outcome.UnperformedChecks);
        Assert.NotNull(outcome.UnperformedChecksError);
    }

    [Fact]
    public void UnperformedChecksCollectionTimeoutHasASeparateReason()
    {
        var fake = new Fake { DiagnosticsTimedOut = true };
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };

        var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test:8080"));

        Assert.Null(outcome.UnperformedChecks);
        Assert.Contains("timed out", outcome.UnperformedChecksError);
    }

    [Fact]
    public void ACompletedResultCarriesRunMetadataAndAnExplicitEmptyFindingList()
    {
        var fake = new Fake();
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };
        var target = new ScanTarget("http://app.test:8080/scoped");

        var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, target, commit: "deadbeef");

        var result = Assert.IsType<ScanResult>(outcome.Result);
        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.Findings);
        Assert.Empty(result.Findings);
        Assert.Null(result.IncompleteReason);
        Assert.True(Guid.TryParseExact(result.Run.RunId, "N", out _));
        Assert.Equal(TimeSpan.Zero, result.Run.StartedAt.Offset);
        Assert.Equal(TimeSpan.Zero, result.Run.EndedAt.Offset);
        Assert.True(result.Run.StartedAt <= result.Run.EndedAt);
        Assert.Equal("stub", result.Run.Tool);
        Assert.Equal(Pinned, result.Run.Image);
        Assert.Equal(plan.RulesVersion, result.Run.RulesVersion);
        Assert.Equal("scanner", result.Run.Sandbox);
        Assert.Equal(ScanNet, result.Run.Network);
        Assert.True(result.Run.NetworkVerifiedInternal);
        Assert.Equal(target.Address, result.Run.Target);
        Assert.Equal("deadbeef", result.Run.Commit);
        Assert.Equal(fake.Diagnostics, result.UnperformedChecks);
        Assert.Equal(0, outcome.ProcessExitCode);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(70, 1)]
    [InlineData(124, 124)]
    public void ACompletedResultPreservesNormalizedFindingsAndTheToolStatus(int scannerCode, int processCode)
    {
        var fake = new Fake { ExecCode = scannerCode };
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };
        var finding = new ScanFinding("rule", "finding", "low", "high", new ScanLocation("/path", "GET", null), "evidence");

        var outcome = Engine(fake).Run(new StubScanner(plan, findings: [finding]), "scanner", plan, new ScanTarget("http://app.test"));

        Assert.Equal("completed", outcome.Result!.Status);
        Assert.Equal(finding, Assert.Single(outcome.Result.Findings!));
        Assert.Equal(scannerCode, outcome.Result.ScannerExitCode);
        Assert.Equal(processCode, outcome.ProcessExitCode);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("report-collection-failed")]
    [InlineData("report-empty")]
    [InlineData("report-unparseable")]
    [InlineData("unperformed-checks-lost")]
    public void IncompleteResultsCannotLookLikeCleanEmptyFindings(string reason)
    {
        var fake = new Fake();
        switch (reason)
        {
            case "timeout": fake.ExecTimedOut = true; fake.ExecCode = 124; break;
            case "report-collection-failed": fake.ReportMissing = true; break;
            case "report-empty": fake.Report = " \n"; break;
            case "report-unparseable": fake.Report = "not JSON"; break;
            case "unperformed-checks-lost": fake.DiagnosticsMissing = true; break;
        }
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };

        var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test"));

        Assert.Equal("incomplete", outcome.Result!.Status);
        Assert.Equal(reason, outcome.Result.IncompleteReason);
        Assert.Null(outcome.Result.Findings);
        Assert.Equal(ScanEngine.IncompleteExitCode, outcome.ProcessExitCode);
        Assert.NotEqual(outcome.ExitCode, outcome.ProcessExitCode);
        Assert.Contains(fake.Calls, argv => argv[0] == "stop");
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 70, 1)]
    [InlineData(false, 124, 124)]
    [InlineData(true, 124, 70)]
    public void ResultAndBothRawArtifactsArePublishedBeforeTeardown(bool timedOut, int scannerCode, int processCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wip-result-" + Guid.NewGuid().ToString("N"));
        var fake = new Fake { ExecTimedOut = timedOut, ExecCode = scannerCode };
        var plan = ValidPlan() with { UnperformedChecksPath = "/out/unperformed.txt" };
        var observed = false;
        fake.BeforeStop = () =>
        {
            var runDirectory = Assert.Single(Directory.GetDirectories(directory));
            Assert.True(File.Exists(Path.Combine(runDirectory, "result.json")));
            Assert.True(File.Exists(Path.Combine(runDirectory, "report.json")));
            Assert.True(File.Exists(Path.Combine(runDirectory, "unperformed-checks.txt")));
            Assert.Empty(Directory.GetFiles(runDirectory, "*.tmp"));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDirectory, "result.json")));
            Assert.Equal(timedOut ? "incomplete" : "completed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(scannerCode, document.RootElement.GetProperty("scannerExitCode").GetInt32());
            Assert.Equal(timedOut ? "" : fake.Report, File.ReadAllText(Path.Combine(runDirectory, "report.json")));
            Assert.Equal(timedOut ? "" : fake.Diagnostics, File.ReadAllText(Path.Combine(runDirectory, "unperformed-checks.txt")));
            observed = true;
        };
        try
        {
            var outcome = Engine(fake).Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test"), outputDirectory: directory);
            Assert.Equal(processCode, outcome.ProcessExitCode);
            Assert.True(observed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    // ----------------------------------------------------------------- cleanup

    [Fact]
    public void TheSandboxIsStoppedAfterASuccessfulScan()
    {
        var fake = new Fake();

        Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080"));

        Assert.True(Ran(fake, "stop"));
        Assert.False(Ran(fake, "remove"), "removal is sandbox destroy's, so a scan leaves the sandbox inspectable");
    }

    [Fact]
    public void TheSandboxIsStoppedWhenTheScanItselfThrows()
    {
        var fake = new Fake { ExecThrows = true };

        var error = Assert.Throws<WipException>(() =>
            Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(), new ScanTarget("http://app.test:8080")));

        Assert.Contains("scanner failed to start", error.Message);
        Assert.True(Ran(fake, "exec"));
        Assert.True(Ran(fake, "stop"));
    }
}
