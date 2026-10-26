using System.Text.Json;
using Wip.Cli;
using Wip.Configuration;
using Wip.Execution;
using Wip.Platform;
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

    private static SandboxSettings Isolated(string image = Pinned) => Settings($"""

        networks:
          - name: scan
            internal: true
        sandboxes:
          - name: scanner
            image: {image}
            network: scan
          - name: unisolated
            image: {image}
        """);

    /// <summary>A plan that passes validation, so a test can vary one thing at a time.</summary>
    private static ScanPlan ValidPlan(string reportPath = "/out/report.json") => new(
        new PinnedImage(Pinned),
        ["zap-baseline", "-t", "http://app.test:8080"],
        reportPath,
        "2.15.0",
        "/out/unperformed.txt");

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
        public readonly List<TimeSpan> Timeouts = [];
        public bool ContainerExists = true;
        public string ContainerState = "running";
        public List<string> Attachments = [ScanNet];
        public bool NetworkVerifiable = true;
        public int PullCode;
        public int ExecCode;
        public bool ExecTimedOut;
        public bool ExecThrows;
        public int RulesCopyCode;
        public bool RulesEmpty;
        public int RulesReadCode;
        public bool RulesUnreadableFile;
        public bool RulesUntraversableDirectory;
        public bool RulesUnreadableDirectory;
        public string? RulesBlockedAncestor;
        public string? RulesFailedAncestor;
        public string? RulesLinkedAncestor;
        public int RulesAncestorLinkCode = 1;
        public bool RulesAncestorLinkTimedOut;
        public int RulesAncestorPermissionCode;
        public bool RulesAncestorPermissionTimedOut;
        public string[] RulesCopiedFiles = ["template.yaml"];
        public readonly List<SandboxMount> Mounts = [];
        public int RulesDirectoryPermissionCode;
        public bool RulesDirectoryPermissionTimedOut;
        public int RulesFilePermissionCode;
        public bool RulesFilePermissionTimedOut;
        public readonly HashSet<string> RulesDirectories = [];
        public readonly HashSet<string> RulesFiles = [];
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
        public Action? AfterExec;

        /// <summary>
        /// What the running container was created from. Defaults to the declared image; a test
        /// sets it to something else to model a declaration edited after creation.
        /// </summary>
        public string? ContainerImage = Pinned;

        public SandboxCommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, SandboxConsoleMode console)
        {
            Calls.Add(argv.ToArray());
            Timeouts.Add(timeout);
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
                        Mounts = Mounts.Select(mount => new { mount.Type, mount.Name, mount.Destination }).ToArray(),
                        Config = ContainerImage is null ? null : new { Image = ContainerImage },
                        NetworkSettings = new { Networks = Attachments.ToDictionary(n => n, _ => new { }) },
                    },
                }));
            }
            if (argv is ["exec", _, "test", "-L", var ancestorPath])
                return new(ancestorPath == RulesLinkedAncestor ? 0 : RulesAncestorLinkCode, "")
                { TimedOut = RulesAncestorLinkTimedOut };
            // Model the measured WSLC behavior: destination must exist, and a directory
            // source is nested by basename even if its contents were intended.
            if (argv is ["container", "cp", var source, var target])
            {
                var destination = target[(target.IndexOf(':') + 1)..];
                if (RulesCopyCode != 0) return new(RulesCopyCode, "");
                if (!RulesDirectories.Contains(destination)) return new(1, "destination missing");
                var nested = destination + "/" + Path.GetFileName(source);
                RulesDirectories.Add(nested);
                if (!RulesEmpty)
                {
                    foreach (var relative in RulesCopiedFiles)
                    {
                        RulesFiles.Add(nested + "/" + relative);
                        var separator = relative.LastIndexOf('/');
                        if (separator >= 0) RulesDirectories.Add(nested + "/" + relative[..separator]);
                    }
                }
                return new(0, "");
            }
            if (argv is ["exec", _, "rm", "-rf", "--", ..])
            {
                foreach (var path in argv.Skip(5))
                {
                    RulesDirectories.RemoveWhere(entry => entry == path || entry.StartsWith(path + "/", StringComparison.Ordinal));
                    RulesFiles.RemoveWhere(entry => entry.StartsWith(path + "/", StringComparison.Ordinal));
                }
                return new(0, "");
            }
            if (argv is ["exec", _, "mkdir", "-p", "--", var directory])
            {
                RulesDirectories.Add(directory);
                return new(0, "");
            }
            if (argv is ["exec", _, "mv", "--", var from, var to])
            {
                if (!RulesDirectories.Remove(from) || RulesDirectories.Contains(to)) return new(1, "invalid move");
                RulesDirectories.Add(to);
                foreach (var copiedDirectory in RulesDirectories.Where(path => path.StartsWith(from + "/", StringComparison.Ordinal)).ToArray())
                {
                    RulesDirectories.Remove(copiedDirectory);
                    RulesDirectories.Add(to + copiedDirectory[from.Length..]);
                }
                foreach (var file in RulesFiles.Where(file => file.StartsWith(from + "/", StringComparison.Ordinal)).ToArray())
                {
                    RulesFiles.Remove(file);
                    RulesFiles.Add(to + file[from.Length..]);
                }
                return new(0, "");
            }
            if (argv is ["exec", _, "rmdir", "--", var emptyDirectory])
                return new(RulesDirectories.Remove(emptyDirectory) &&
                    !RulesDirectories.Any(path => path.StartsWith(emptyDirectory + "/", StringComparison.Ordinal)) ? 0 : 1, "");
            if (argv is ["exec", _, "find", var ancestor, "-maxdepth", "0", "-type", "d", "!", "-perm", "-o+rx", "-print", "-quit"])
            {
                var resolvedAncestor = ancestor.EndsWith("/.", StringComparison.Ordinal) ? ancestor[..^2] : ancestor;
                if (resolvedAncestor.Length == 0) resolvedAncestor = "/";
                return new(resolvedAncestor == RulesFailedAncestor ? RulesAncestorPermissionCode : 0,
                    resolvedAncestor == RulesBlockedAncestor ? resolvedAncestor : "")
                { TimedOut = resolvedAncestor == RulesFailedAncestor && RulesAncestorPermissionTimedOut };
            }
            if (argv is ["exec", _, "find", var directoryPath, "-type", "d", "!", "-perm", var permission, "-print", "-quit"] &&
                permission is "-o+x" or "-o+rx")
                return new(RulesDirectoryPermissionCode,
                    RulesUntraversableDirectory || (permission == "-o+rx" && RulesUnreadableDirectory) ? directoryPath + "/private" : "")
                { TimedOut = RulesDirectoryPermissionTimedOut };
            if (argv is ["exec", _, "find", var filePath, "-type", "f", "!", "-perm", "-o+r", "-print", "-quit"])
                return new(RulesFilePermissionCode, RulesUnreadableFile ? filePath + "/private.yaml" : "")
                { TimedOut = RulesFilePermissionTimedOut };
            if (argv is ["exec", _, "find", var countedPath, "-type", "f", "-print0"])
            {
                var files = RulesFiles.Where(file => file.StartsWith(countedPath + "/", StringComparison.Ordinal)).ToArray();
                return new(RulesReadCode, files.Length == 0 ? "" : string.Join('\0', files) + "\0");
            }
            if (argv is ["exec", _, "find", var rulesPath, "-type", "f", "-print", "-quit"])
                return new(RulesReadCode, string.Join('\n', RulesFiles.Where(file => file.StartsWith(rulesPath + "/", StringComparison.Ordinal))));
            if (argv is ["exec", _, "find", ..])
                throw new InvalidOperationException("Unexpected pinned-rules find arguments");
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
                AfterExec?.Invoke();
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

    private static ScanEngine Engine(Fake fake, SandboxSettings? settings = null,
        RulesFetcher? rulesFetcher = null, Func<string, string>? mapRulesSource = null)
    {
        var resolved = settings ?? Isolated();
        return new(resolved, new SandboxLifecycle(resolved, fake.Run, null, new NetworkLifecycle(resolved, fake.Run)),
            new NetworkLifecycle(resolved, fake.Run), fake.Run, rulesFetcher, mapRulesSource);
    }

    private static bool Ran(Fake fake, params string[] prefix) =>
        fake.Calls.Any(c => c.Length >= prefix.Length && c.Take(prefix.Length).SequenceEqual(prefix));

    private sealed class RulesCacheFixture(PinnedRules? rules = null) : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "wip-rules-engine-" + Guid.NewGuid().ToString("N"));
        public readonly PinnedRules Rules = rules ?? new("https://example.invalid/rules.git", new string('a', 40), "/opt/wip-rules");
        public readonly List<string[]> GitCalls = [];
        public Action? BeforeGit;
        public bool WrongCommit;
        public bool SecondFile;
        public RulesFetcher Fetcher => new(Path.Combine(Root, "cache"), Git);
        private (int Code, string Output) Git(IReadOnlyList<string> argv,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            BeforeGit?.Invoke();
            GitCalls.Add(argv.ToArray());
            var directory = argv[argv.ToList().IndexOf("-C") + 1];
            var gitDirectory = environment["GIT_DIR"];
            Assert.False(gitDirectory.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.Equal(directory, environment["GIT_WORK_TREE"]);
            if (argv.Contains("init")) Directory.CreateDirectory(gitDirectory);
            if (argv.Contains("rev-parse")) return (0, (WrongCommit ? new string('b', 40) : Rules.Commit) + "\n");
            if (argv.Contains("checkout"))
            {
                File.WriteAllText(Path.Combine(directory, "template.yaml"), "id: verified-rule\n");
                if (SecondFile)
                {
                    Directory.CreateDirectory(Path.Combine(directory, "nested"));
                    File.WriteAllText(Path.Combine(directory, "nested", "second.yaml"), "id: second-rule\n");
                }
            }
            return (0, "");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    [Fact]
    public void PreparationFetchesRulesAfterTheImageAndReusesTheVerifiedPin()
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake();
        cache.BeforeGit = () => Assert.True(Ran(fake, "pull"));
        var declared = ValidPlan() with { Rules = cache.Rules };
        var engine = Engine(fake, rulesFetcher: cache.Fetcher);
        var scanner = new StubScanner(declared);
        var target = new ScanTarget("http://app.test");

        var prepared = engine.Prepare(scanner, "scanner", target);
        Assert.True(File.Exists(Path.Combine(cache.Fetcher.RequireStaged(cache.Rules), "template.yaml")));
        var commands = cache.GitCalls.Count;
        var second = engine.Prepare(new StubScanner(prepared), "scanner", target);

        Assert.Equal(commands, cache.GitCalls.Count);
        Assert.Single(cache.GitCalls, command => command.Contains("fetch"));
        Assert.Equal(declared.RulesVersion + ";rules-commit:" + cache.Rules.Commit, prepared.RulesVersion);
        Assert.Equal(prepared.RulesVersion, second.RulesVersion);
    }

    [Fact]
    public void UnsupportedRulesCacheLocationsAreRefusedBeforePreparationDoesAnyWork()
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake();
        var plan = ValidPlan() with { Rules = cache.Rules };
        string? checkedSource = null;
        var engine = Engine(fake, rulesFetcher: cache.Fetcher, mapRulesSource: source =>
        {
            checkedSource = source;
            return WslPath.ForWslc(@"\\wsl.localhost\Ubuntu\home\u\proj\.wip\scanner-rules",
                "the pinned scanner rules source", mode: null);
        });

        var error = Assert.Throws<ConfigException>(() =>
            engine.Prepare(new StubScanner(plan), "scanner", new ScanTarget("http://app.test")));

        Assert.Contains("the pinned scanner rules source is on the WSL filesystem", error.Message);
        Assert.Equal(Path.Combine(cache.Root, "cache", cache.Rules.Commit, "tree"), checkedSource);
        Assert.Empty(cache.GitCalls);
        Assert.Empty(fake.Calls);
        Assert.False(Directory.Exists(cache.Root));
    }

    [Fact]
    public void RulesSourceValidationRunsBeforeFetchingAndAgainBeforeCopying()
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake();
        var checkedSources = new List<string>();
        var plan = ValidPlan() with { Rules = cache.Rules };
        var engine = Engine(fake, rulesFetcher: cache.Fetcher, mapRulesSource: source =>
        {
            if (checkedSources.Count == 0)
            {
                Assert.Empty(fake.Calls);
                Assert.Empty(cache.GitCalls);
                Assert.False(Directory.Exists(cache.Root));
            }
            else
            {
                Assert.True(Ran(fake, "network", "inspect"));
                Assert.True(Directory.Exists(source));
            }
            checkedSources.Add(source);
            return source;
        });
        var scanner = new StubScanner(plan);
        var target = new ScanTarget("http://app.test");

        var prepared = engine.Prepare(scanner, "scanner", target);
        var outcome = engine.Run(scanner, "scanner", prepared, target);

        Assert.Equal(2, checkedSources.Count);
        Assert.Equal(checkedSources[0], checkedSources[1]);
        Assert.Equal("completed", outcome.Result!.Status);
    }

    [Fact]
    public void RulesCopyGetsFiveMinutesWhileSetupAndProbesKeepOneMinute()
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake();
        var plan = ValidPlan() with { Rules = cache.Rules };

        Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test"));

        var copyIndex = fake.Calls.FindIndex(command => command is ["container", "cp", ..]);
        Assert.True(copyIndex >= 0);
        Assert.Equal(TimeSpan.FromMinutes(5), fake.Timeouts[copyIndex]);
        var probes = fake.Calls.Select((command, index) => (command, index))
            .Where(call => call.command is ["exec", _, "find" or "test" or "rm" or "mkdir" or "mv" or "rmdir", ..]).ToArray();
        Assert.NotEmpty(probes);
        Assert.All(probes, call => Assert.Equal(TimeSpan.FromMinutes(1), fake.Timeouts[call.index]));
    }

    [Fact]
    public void AFailedRulesPinVerificationCannotPrepareAScan()
    {
        using var cache = new RulesCacheFixture { WrongCommit = true };
        var fake = new Fake();
        var plan = ValidPlan() with { Rules = cache.Rules };

        Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Prepare(new StubScanner(plan), "scanner", new ScanTarget("http://app.test")));

        Assert.False(Ran(fake, "exec"));
        Assert.False(Ran(fake, "container", "cp"));
    }

    [Fact]
    public void RunCopiesVerifiedRulesWithoutAShellBeforeScanningAndRecordsThePin()
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var gitCalls = cache.GitCalls.Count;
        var fake = new Fake();
        fake.RulesDirectories.UnionWith(["/opt/wip-rules", "/opt/wip-rules.staging", "/opt/wip-rules.staging/old"]);
        fake.RulesFiles.UnionWith(["/opt/wip-rules/old.yaml", "/opt/wip-rules.staging/old/stale.yaml"]);
        var plan = ValidPlan() with { Rules = cache.Rules };
        string? mapped = null;
        var engine = Engine(fake, rulesFetcher: cache.Fetcher, mapRulesSource: source =>
        {
            mapped = source;
            return "/mapped staging/tree";
        });

        var outcome = engine.Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test"));

        Assert.Equal(cache.Fetcher.RequireStaged(cache.Rules), mapped);
        var copy = Assert.Single(fake.Calls, command => command is ["container", "cp", ..]);
        Assert.Equal(new[] { "container", "cp", "/mapped staging/tree", "container-id:/opt/wip-rules.staging" }, copy);
        var copyIndex = fake.Calls.IndexOf(copy);
        Assert.Contains(fake.Calls.Take(copyIndex), command => command.SequenceEqual(new[] { "exec", "container-id", "rm", "-rf", "--", "/opt/wip-rules", "/opt/wip-rules.staging" }));
        Assert.Contains(fake.Calls.Take(copyIndex), command => command.SequenceEqual(new[] { "exec", "container-id", "mkdir", "-p", "--", "/opt/wip-rules.staging" }));
        var moveIndex = fake.Calls.FindIndex(command => command.SequenceEqual(new[] { "exec", "container-id", "mv", "--", "/opt/wip-rules.staging/tree", "/opt/wip-rules" }));
        var cleanupIndex = fake.Calls.FindIndex(command => command.SequenceEqual(new[] { "exec", "container-id", "rmdir", "--", "/opt/wip-rules.staging" }));
        Assert.True(copyIndex < moveIndex && moveIndex < cleanupIndex);
        Assert.True(cleanupIndex < fake.Calls.FindIndex(command => command is ["exec", _, "find", ..]));
        Assert.Equal(["/opt/wip-rules/template.yaml"], fake.RulesFiles);
        Assert.DoesNotContain("/opt/wip-rules.staging", fake.RulesDirectories);
        Assert.True(copyIndex < fake.Calls.FindIndex(command => command is ["exec", _, "find", ..]));
        Assert.True(fake.Calls.FindIndex(command => command is ["exec", _, "find", ..]) <
                    fake.Calls.FindIndex(command => command is ["exec", _, "zap-baseline", ..]));
        Assert.DoesNotContain(fake.Calls, command => command.Contains("sh"));
        Assert.Equal(gitCalls, cache.GitCalls.Count);
        Assert.Equal("completed", outcome.Result!.Status);
        var probes = fake.Calls.Where(command => command is ["exec", _, "find", ..]).ToArray();
        Assert.Equal(5, probes.Length);
        Assert.Equal(new[] { "exec", "container-id", "find", "/.", "-maxdepth", "0", "-type", "d", "!", "-perm", "-o+rx", "-print", "-quit" }, probes[0]);
        Assert.Equal(new[] { "exec", "container-id", "find", "/opt/.", "-maxdepth", "0", "-type", "d", "!", "-perm", "-o+rx", "-print", "-quit" }, probes[1]);
        Assert.Equal(new[] { "exec", "container-id", "find", "/opt/wip-rules", "-type", "d", "!", "-perm", "-o+rx", "-print", "-quit" }, probes[2]);
        Assert.Equal(new[] { "exec", "container-id", "find", "/opt/wip-rules", "-type", "f", "-print0" }, probes[3]);
        Assert.Equal(new[] { "exec", "container-id", "find", "/opt/wip-rules", "-type", "f", "!", "-perm", "-o+r", "-print", "-quit" }, probes[4]);
        Assert.DoesNotContain(fake.Calls, command => command.Contains("-readable"));
        Assert.EndsWith(";rules-commit:" + cache.Rules.Commit, outcome.RulesVersion);
        using var stream = new MemoryStream();
        ScanResultWriter.Write(stream, outcome.Result);
        using var result = JsonDocument.Parse(stream.ToArray());
        Assert.Contains(cache.Rules.Commit, result.RootElement.GetProperty("run").GetProperty("rulesVersion").GetString());
    }

    [Fact]
    public void ACopyFailureStopsTheSandboxWithoutExecutingTheScanner()
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake { RulesCopyCode = 9 };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains("copy", error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    public void EmptyOrUnreadableContainerRulesRefuseTheScan(bool empty, int code)
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake { RulesEmpty = empty, RulesReadCode = code };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains(empty ? "empty" : "presence", error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
    }

    [Theory]
    [InlineData(false, "unreadable file")]
    [InlineData(true, "untraversable directory")]
    public void InaccessibleRulesRefuseExecutionWithTheSpecificCause(bool directory, string reason)
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake { RulesUntraversableDirectory = directory, RulesUnreadableFile = !directory };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains(reason, error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
        if (directory)
            Assert.DoesNotContain(fake.Calls, command => command is ["exec", _, "find", _, "-type", "f", ..]);
    }

    [Theory]
    [InlineData(true, 3, false, "directory permissions")]
    [InlineData(true, 0, true, "directory permissions")]
    [InlineData(false, 3, false, "file permissions")]
    [InlineData(false, 0, true, "file permissions")]
    public void FailedOrTimedOutRulesPermissionProbesFailClosed(bool directory, int code, bool timedOut, string reason)
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake
        {
            RulesDirectoryPermissionCode = directory ? code : 0,
            RulesDirectoryPermissionTimedOut = directory && timedOut,
            RulesFilePermissionCode = directory ? 0 : code,
            RulesFilePermissionTimedOut = !directory && timedOut
        };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains(reason, error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
    }

    [Fact]
    public void DirectoriesMissingOtherReadCannotSilentlyHideTheRules()
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake { RulesUnreadableDirectory = true };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains("directory", error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/opt")]
    public void InaccessibleAncestorsRefuseRulesBeforeTheScannerRuns(string ancestor)
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake { RulesBlockedAncestor = ancestor };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains("ancestor", error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
    }

    [Theory]
    [InlineData("/", 3, false)]
    [InlineData("/opt", 3, false)]
    [InlineData("/", 0, true)]
    [InlineData("/opt", 0, true)]
    public void FailedOrTimedOutAncestorProbesFailClosed(string ancestor, int code, bool timedOut)
    {
        using var cache = new RulesCacheFixture();
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake { RulesFailedAncestor = ancestor, RulesAncestorPermissionCode = code, RulesAncestorPermissionTimedOut = timedOut };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains("ancestor", error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CopiedRulesMustMatchTheVerifiedHostFileCount(int actualFiles)
    {
        using var cache = new RulesCacheFixture { SecondFile = true };
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake
        {
            RulesCopiedFiles = actualFiles == 1 ? ["template.yaml"] : ["template.yaml", "nested/second.yaml", "extra.yaml"]
        };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains($"expected 2 files, found {actualFiles}", error.Message);
        Assert.Contains(actualFiles < 2 ? "incomplete" : "extra files", error.Message);
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
        Assert.True(Ran(fake, "stop"));
    }

    [Fact]
    public void EveryVerifiedNestedFileCanBeCopiedAndCountedWithoutAShell()
    {
        using var cache = new RulesCacheFixture { SecondFile = true };
        cache.Fetcher.Fetch(cache.Rules);
        var fake = new Fake { RulesCopiedFiles = ["template.yaml", "nested/second.yaml"] };
        var plan = ValidPlan() with { Rules = cache.Rules };

        var outcome = Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test"));

        Assert.Equal("completed", outcome.Result!.Status);
        Assert.Equal(2, fake.RulesFiles.Count);
        Assert.Contains(fake.Calls, command => command.SequenceEqual(new[] { "exec", "container-id", "find", "/opt/wip-rules", "-type", "f", "-print0" }));
        Assert.DoesNotContain(fake.Calls, command => command.Contains("sh"));
    }

    private static SandboxSettings IsolatedWithMount(string mount) => Settings($"""

        networks:
          - name: scan
            internal: true
        volumes:
          - name: project
            persistent: true
            mount: {mount}
        sandboxes:
          - name: scanner
            image: {Pinned}
            network: scan
            volumes: [project]
        """);

    [Theory]
    [InlineData("/work")]
    [InlineData("/work/rules")]
    [InlineData("/work/rules/nested")]
    [InlineData("/work/rules.staging")]
    [InlineData("/work/rules.staging/nested")]
    [InlineData("/")]
    public void RulesOrStagingOverlappingDeclaredMountsFailBeforeAnyBackendCall(string mount)
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake();
        var rules = cache.Rules with { ContainerPath = "/work/rules" };
        var plan = ValidPlan() with { Rules = rules };
        var engine = Engine(fake, IsolatedWithMount(mount), cache.Fetcher);
        var scanner = new StubScanner(plan);
        var target = new ScanTarget("http://app.test");

        var prepareError = Assert.ThrowsAny<WipException>(() => engine.Prepare(scanner, "scanner", target));
        Assert.Contains("mount", prepareError.Message);
        Assert.Empty(fake.Calls);
        Assert.Empty(cache.GitCalls);
        var runError = Assert.ThrowsAny<WipException>(() => engine.Run(scanner, "scanner", plan, target));
        Assert.Contains("mount", runError.Message);
        Assert.Empty(fake.Calls);
        Assert.Empty(cache.GitCalls);
    }

    [Fact]
    public void MountPrefixWithoutAPathBoundaryDoesNotBlockRules()
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake();
        var plan = ValidPlan() with { Rules = cache.Rules with { ContainerPath = "/work/rules" } };
        var engine = Engine(fake, IsolatedWithMount("/workspace"), cache.Fetcher);

        var prepared = engine.Prepare(new StubScanner(plan), "scanner", new ScanTarget("http://app.test"));
        var outcome = engine.Run(new StubScanner(prepared), "scanner", prepared, new ScanTarget("http://app.test"));

        Assert.Equal("completed", outcome.Result!.Status);
    }

    [Fact]
    public void AnUnstatedRuntimeBindMountCannotBeTargetedByRulesCleanup()
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake();
        fake.Mounts.Add(new SandboxMount("bind", "host-project", "/work"));
        var rules = cache.Rules with { ContainerPath = "/work/rules" };
        cache.Fetcher.Fetch(rules);
        var plan = ValidPlan() with { Rules = rules };

        var error = Assert.ThrowsAny<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains("mount", error.Message);
        Assert.DoesNotContain(fake.Calls, command => command is ["exec", _, "rm", ..] or ["container", "cp", ..] or ["exec", _, "zap-baseline", ..]);
        Assert.True(Ran(fake, "stop"));
    }

    [Fact]
    public void ALinkedRulesAncestorCannotAliasAMountedHostDirectory()
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake { RulesLinkedAncestor = "/opt" };
        fake.Mounts.Add(new SandboxMount("bind", "host-project", "/work"));
        cache.Fetcher.Fetch(cache.Rules);
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.ThrowsAny<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains("ancestor", error.Message);
        Assert.Contains(fake.Calls, command => command.SequenceEqual(new[] { "exec", "container-id", "test", "-L", "/opt" }));
        Assert.DoesNotContain(fake.Calls, command => command is ["exec", _, "rm", ..] or ["container", "cp", ..] or ["exec", _, "zap-baseline", ..]);
        Assert.True(Ran(fake, "stop"));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(1, true)]
    public void AnUnverifiableRulesAncestorRefusesCleanup(int code, bool timedOut)
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake { RulesAncestorLinkCode = code, RulesAncestorLinkTimedOut = timedOut };
        cache.Fetcher.Fetch(cache.Rules);
        var plan = ValidPlan() with { Rules = cache.Rules };

        var error = Assert.ThrowsAny<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Contains("ancestor", error.Message);
        Assert.DoesNotContain(fake.Calls, command => command is ["exec", _, "rm", ..] or ["container", "cp", ..] or ["exec", _, "zap-baseline", ..]);
        Assert.True(Ran(fake, "stop"));
    }

    [Fact]
    public void NoRulesPlansDoNotApplyRulesMountRestrictions()
    {
        var fake = new Fake();
        fake.Mounts.Add(new SandboxMount("bind", "host-project", "/work"));
        var plan = ValidPlan();
        var engine = Engine(fake, IsolatedWithMount("/work"));

        var prepared = engine.Prepare(new StubScanner(plan), "scanner", new ScanTarget("http://app.test"));
        var outcome = engine.Run(new StubScanner(prepared), "scanner", prepared, new ScanTarget("http://app.test"));

        Assert.Equal("completed", outcome.Result!.Status);
        Assert.False(Ran(fake, "container", "cp"));
    }

    [Fact]
    public void RunCannotFetchMissingRulesOrContinueWithoutThem()
    {
        using var cache = new RulesCacheFixture();
        var fake = new Fake();
        var plan = ValidPlan() with { Rules = cache.Rules };

        Assert.Throws<WipException>(() => Engine(fake, rulesFetcher: cache.Fetcher)
            .Run(new StubScanner(plan), "scanner", plan, new ScanTarget("http://app.test")));

        Assert.Empty(cache.GitCalls);
        Assert.False(Ran(fake, "container", "cp"));
        Assert.False(Ran(fake, "exec", "container-id", "zap-baseline"));
    }

    [Fact]
    public void AnAdapterWithoutRulesDoesNotUseGitCacheOrCopyOperations()
    {
        using var cache = new RulesCacheFixture();
        cache.BeforeGit = () => throw new InvalidOperationException("legacy scan must not invoke git");
        var fake = new Fake();
        var plan = ValidPlan();
        var engine = Engine(fake, rulesFetcher: cache.Fetcher, mapRulesSource: _ => throw new InvalidOperationException("no copy"));
        var prepared = engine.Prepare(new StubScanner(plan), "scanner", new ScanTarget("http://app.test"));
        var outcome = engine.Run(new StubScanner(plan), "scanner", prepared, new ScanTarget("http://app.test"));

        Assert.Equal(plan.RulesVersion, prepared.RulesVersion);
        Assert.Equal(plan.RulesVersion, outcome.RulesVersion);
        Assert.Empty(cache.GitCalls);
        Assert.False(Directory.Exists(cache.Root));
        Assert.False(Ran(fake, "container", "cp"));
        Assert.DoesNotContain(fake.Calls, command => command is ["exec", _, "rm" or "mkdir" or "mv" or "rmdir" or "find" or "test", ..]);
    }

    // ----------------------------------------------------------------- one tool, from the table

    [Fact]
    public void ZapIsRegisteredInTheReviewedBuiltInTable()
    {
        Assert.Equal(["zap", "nuclei"], ScanEngine.Tools);
        Assert.IsType<NucleiScanner>(ScanEngine.Select("nuclei"));
        Assert.IsType<ZapScanner>(ScanEngine.Select("zap"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Zap")]
    [InlineData("ZAP")]
    [InlineData("zap,nuclei")]
    [InlineData("nuclei zap")]
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

    [Theory]
    [InlineData("zap")]
    [InlineData("nuclei")]
    public void BuiltInAdaptersConformToTheUnchangedEngineAndIsolationBoundary(string tool)
    {
        var scanner = ScanEngine.Select(tool);
        var target = new ScanTarget("http://app.test:8080/");
        var plan = scanner.Plan(target);
        plan.Validate(tool);
        Assert.False(scanner.GetType().IsPublic);
        Assert.False(typeof(ISecurityScanner).IsPublic);
        using var cache = new RulesCacheFixture(plan.Rules);
        var fake = new Fake { ContainerImage = plan.Image.Reference,
            Report = tool == "zap" ? "{\"site\":[]}" : NucleiScanner.Completion };
        var engine = Engine(fake, Isolated(plan.Image.Reference), cache.Fetcher);
        var prepared = engine.Prepare(scanner, "scanner", target);
        var outcome = engine.Run(scanner, "scanner", prepared, target);
        Assert.Equal("completed", outcome.Result!.Status);
        Assert.Empty(outcome.Result.Findings!);
        Assert.True(outcome.Result.Run.NetworkVerifiedInternal);
        Assert.True(Ran(fake, "stop"));
        Assert.DoesNotContain(fake.Calls, argv => argv.Contains("--network") || argv.Contains("--mount") || argv.Contains("--user"));
        fake.Attachments.Add("foreign-network");
        fake.ContainerState = "running";
        fake.Calls.Clear();
        Assert.Throws<WipException>(() => engine.Run(scanner, "scanner", prepared, target));
        Assert.DoesNotContain(fake.Calls, argv => argv is ["exec", _, "sh" or "python3", ..]);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void APlanWithoutACoveragePathFailsValidationBeforeAnyBackendCall(string? coveragePath)
    {
        var fake = new Fake();
        var plan = ValidPlan() with { UnperformedChecksPath = coveragePath! };

        var validation = Assert.Throws<ConfigException>(() => plan.Validate("stub"));
        Assert.Contains("unperformed-checks path is required", validation.Message);
        Assert.Contains("not checked", validation.Message);
        Assert.Throws<ConfigException>(() =>
            Engine(fake).Prepare(new StubScanner(plan), "scanner", new ScanTarget("http://app.test:8080")));
        Assert.Empty(fake.Calls);
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

        var read = fake.Calls.Single(c => c.Contains("cat") && c.Contains("/out/report.json"));
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

        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliContext.WriteScanOutcome(outcome, plan.ReportPath, output, error,
            isOutputRedirected: true, isErrorRedirected: true);
        Assert.Equal(70, code);
        Assert.Contains("timed out; requested sandbox stop without collecting a partial report", error.ToString());
        Assert.Contains("unperformed-check diagnostics unavailable: scan timed out before diagnostics could be collected", error.ToString());
        Assert.DoesNotContain("wrote no report", error.ToString());
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
    [InlineData(false, 74, 1)]
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

    /// <summary>
    /// The scan is kept, and the failed save still reaches the exit code: asking for --output
    /// and receiving neither a file nor a nonzero status is the reading this format exists to
    /// prevent. The scanner's own status stays in the outcome and the result document.
    /// </summary>
    [Theory]
    [InlineData(0, ScanEngine.PersistenceFailedExitCode)]
    [InlineData(7, ScanEngine.PersistenceFailedExitCode)]
    [InlineData(70, ScanEngine.PersistenceFailedExitCode)]
    [InlineData(74, ScanEngine.PersistenceFailedExitCode)]
    public void PersistenceFailureAfterPreflightPreservesTheCompletedScan(int scannerCode, int processCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wip-result-failure-" + Guid.NewGuid().ToString("N"));
        var fake = new Fake { ExecCode = scannerCode };
        fake.AfterExec = () =>
        {
            Assert.True(Directory.Exists(directory));
            Directory.Delete(directory);
            File.WriteAllText(directory, "the output location became a file after preflight");
        };
        try
        {
            var outcome = Engine(fake).Run(new StubScanner(), "scanner", ValidPlan(),
                new ScanTarget("http://app.test"), outputDirectory: directory);

            Assert.Equal(fake.Report, outcome.Report);
            Assert.Equal(fake.Diagnostics, outcome.UnperformedChecks);
            Assert.Equal(scannerCode, outcome.ExitCode);
            Assert.Equal(processCode, outcome.ProcessExitCode);
            Assert.Equal("completed", outcome.Result!.Status);
            Assert.Equal(scannerCode, outcome.Result.ScannerExitCode);
            Assert.Contains("could not be saved", outcome.PersistenceError);
            Assert.Single(fake.Calls, argv => argv[0] == "stop");

            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(processCode, CliContext.WriteScanOutcome(outcome, ValidPlan().ReportPath, output, error, true, true));
            Assert.Equal(fake.Report, output.ToString());
            Assert.Contains($"exit={scannerCode}", error.ToString());
            Assert.Contains("result persistence failed", error.ToString());
        }
        finally
        {
            if (File.Exists(directory)) File.Delete(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
