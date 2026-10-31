using System.Text.Json;
using System.ComponentModel;
using Wip.Security;

namespace Wip.Tests;

public sealed class RulesFetcherTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly string root = Path.Combine(Path.GetTempPath(), "wip-rules-fetch-" + Guid.NewGuid().ToString("N"));
    private static readonly PinnedRules Rules = new("https://example.invalid/rules.git", Commit, "/opt/wip-rules");

    [Fact]
    public void ConstructionDoesNotTouchDiskOrRequireGitForAdaptersWithoutRules()
    {
        _ = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("git must not run"));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void FetchVerifiesTheExactCommitBeforeCheckoutAndPublishesOnlyRulesContent()
    {
        var fake = new FakeGit();
        var fetcher = new RulesFetcher(root, fake.Run);
        var staged = fetcher.Fetch(Rules);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), Commit, "tree"), staged);
        Assert.Equal("pinned rule content", File.ReadAllText(Path.Combine(staged, "rules.yaml")));
        Assert.False(Directory.Exists(Path.Combine(staged, ".git")));
        Assert.Equal(new[] { "init", "fetch", "rev-parse", "checkout" }, fake.Operations);
        var fetch = Assert.Single(fake.Commands, command => command.Contains("fetch"));
        Assert.Contains("--depth", fetch);
        Assert.Contains("1", fetch);
        Assert.Contains(Rules.RepositoryUrl, fetch);
        Assert.Contains(Commit, fetch);
        Assert.All(fake.Timeouts, timeout => Assert.InRange(timeout.TotalMilliseconds, 1, int.MaxValue));
        Assert.All(fake.Commands, command => Assert.Equal("git", command[0]));
        Assert.All(fake.Commands, command => Assert.Contains(command, argument => argument.StartsWith("core.hooksPath=", StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    /// <summary>
    /// The checkout writes every file of the tree, so it costs what the fetch costs rather than
    /// what the metadata calls cost. It had the thirty-second budget, which a templates
    /// repository of thousands of files would exceed on a host that scans each write.
    /// </summary>
    [Fact]
    public void TheCheckoutGetsTheSameBudgetAsTheFetch()
    {
        var fake = new FakeGit();
        new RulesFetcher(root, fake.Run).Fetch(Rules);

        var budgets = fake.Operations.Zip(fake.Timeouts).ToDictionary(pair => pair.First, pair => pair.Second);
        Assert.Equal(budgets["fetch"], budgets["checkout"]);
        Assert.True(budgets["checkout"] > budgets["init"], "writing the tree must not share the metadata budget");
        Assert.Equal(budgets["init"], budgets["rev-parse"]);
    }

    [Fact]
    public void AnExactPinCacheHitRequiresNoGitAndSurvivesPreparation()
    {
        var fake = new FakeGit();
        var first = new RulesFetcher(root, fake.Run).Fetch(Rules);
        var noGit = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("cache hit must not use git"));
        Assert.Equal(first, noGit.Fetch(Rules));
        Assert.Equal(first, noGit.RequireStaged(Rules));
        Assert.Equal(4, fake.Commands.Count);
    }

    [Fact]
    public void ACommitMismatchNeverChecksOutOrPublishesPartialRules()
    {
        var fake = new FakeGit { ResolvedCommit = new string('f', 40) };
        var failure = Assert.Throws<WipException>(() => new RulesFetcher(root, fake.Run).Fetch(Rules));
        Assert.Contains("commit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkout", fake.Operations);
        Assert.False(Directory.Exists(Path.Combine(root, Commit)));
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    [Fact]
    public void AFailedFetchCannotPublishOrLeaveATemporaryCheckout()
    {
        var fake = new FakeGit { FetchCode = 128 };
        Assert.Throws<WipException>(() => new RulesFetcher(root, fake.Run).Fetch(Rules));
        Assert.DoesNotContain("rev-parse", fake.Operations);
        Assert.False(Directory.Exists(Path.Combine(root, Commit)));
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrUnrunnableGitExplainsWhyGitIsRequired(bool throws)
    {
        var fetcher = new RulesFetcher(root, (_, _, _) => throws ? throw new Win32Exception(2) : (127, ""));
        var failure = Assert.Throws<WipException>(() => fetcher.Fetch(Rules));
        Assert.Contains("git", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pinned rules", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    [Fact]
    public void GitMetadataDoesNotMakeAnEmptyCheckoutLookLikeRules()
    {
        var fake = new FakeGit { EmptyCheckout = true };
        Assert.Throws<WipException>(() => new RulesFetcher(root, fake.Run).Fetch(Rules));
        Assert.False(Directory.Exists(Path.Combine(root, Commit)));
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    [Fact]
    public void AnUnverifiedDirectoryNamedAfterThePinIsRejectedWithoutFetching()
    {
        Directory.CreateDirectory(Path.Combine(root, Commit, "tree"));
        File.WriteAllText(Path.Combine(root, Commit, "tree", "rules.yaml"), "unverified");
        var fetcher = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch in Run"));
        Assert.Throws<WipException>(() => fetcher.RequireStaged(Rules));
        Assert.Throws<WipException>(() => fetcher.Fetch(Rules));
    }

    [Fact]
    public void ModifiedCachedContentIsRejectedWithoutNetworkAccess()
    {
        var fake = new FakeGit();
        var path = new RulesFetcher(root, fake.Run).Fetch(Rules);
        File.WriteAllText(Path.Combine(path, "rules.yaml"), "modified content");
        var fetcher = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch"));
        Assert.Throws<WipException>(() => fetcher.RequireStaged(Rules));
        Assert.Throws<WipException>(() => fetcher.Fetch(Rules));
    }

    [Fact]
    public void RunCannotFetchRulesThatWereNeverPrepared()
    {
        var fetcher = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch"));
        Assert.Throws<WipException>(() => fetcher.RequireStaged(Rules));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ACheckoutSymlinkIsRejectedWithoutFollowingOrDeletingItsTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symlink can require Windows developer privileges");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside-rule.yaml");
        File.WriteAllText(outside, "outside content");
        var fake = new FakeGit { LinkTarget = outside };
        Assert.Throws<WipException>(() => new RulesFetcher(root, fake.Run).Fetch(Rules));
        Assert.Equal("outside content", File.ReadAllText(outside));
        Assert.False(Directory.Exists(Path.Combine(root, Commit)));
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    [Fact]
    public void AMalformedVerificationMarkerFailsClosedWithoutGit()
    {
        var fake = new FakeGit();
        new RulesFetcher(root, fake.Run).Fetch(Rules);
        File.WriteAllText(Path.Combine(root, Commit, "verified.json"), "{\"version\":\"1\"}");
        var noGit = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch"));
        Assert.Throws<WipException>(() => noGit.RequireStaged(Rules));
    }

    [Fact]
    public void EveryGitInvocationPinsAttributeDrivenNativeLineEndingsToLf()
    {
        var fake = new FakeGit();
        new RulesFetcher(root, fake.Run).Fetch(Rules);
        Assert.All(fake.Commands, command =>
        {
            var options = command.Select((argument, index) => (argument, index))
                .Where(item => item.argument == "-c").Select(item => command[item.index + 1]);
            Assert.Contains("core.autocrlf=false", options);
            Assert.Contains("core.eol=lf", options);
        });
    }

    [Fact]
    public void VerifiedTreeCountIncludesEveryRegularFileAndNestedFileWithoutGitMetadata()
    {
        var fake = new FakeGit { NestedFiles = true };
        var fetcher = new RulesFetcher(root, fake.Run);
        var path = fetcher.Fetch(Rules);
        var staged = fetcher.RequireStagedTree(Rules);
        Assert.Equal(path, staged.Path);
        Assert.Equal(3, staged.FileCount);
        Assert.Equal(path, fetcher.RequireStaged(Rules));
        Assert.Equal(4, fake.Commands.Count);
    }

    [Fact]
    public void LegacyNativeEolCachesAreRefusedWithoutFetchingOverTheNetwork()
    {
        var fake = new FakeGit();
        var fetcher = new RulesFetcher(root, fake.Run);
        fetcher.Fetch(Rules);
        var markerPath = Path.Combine(root, Commit, "verified.json");
        using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
        var fingerprint = document.RootElement.GetProperty("treeSha256").GetString();
        using (var stream = File.Create(markerPath))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("commit", Commit);
            writer.WriteString("treeSha256", fingerprint);
            writer.WriteEndObject();
        }
        var noGit = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("legacy entries must not silently fetch"));
        var failure = Assert.Throws<WipException>(() => noGit.Fetch(Rules));
        Assert.Contains("prepare again", failure.Message);
        Assert.Throws<WipException>(() => noGit.RequireStagedTree(Rules));
        Assert.Equal(4, fake.Commands.Count);
    }

    [Fact]
    public void FingerprintUsesExplicitNormalizedPathOrderAcrossRootsAndHostSeparators()
    {
        // The digest encodes a/b.yaml before a0.yaml. Native Windows absolute-path
        // ordering reverses them because backslash sorts after '0', unlike '/'.
        const string expected = "ffe0e019efa5fd39cd5218190da627eb7f4de4d8122f90daa54fa403304b9e3c";
        foreach (var prefix in new[] { "short", "different-long-root-prefix" })
        {
            var cache = Path.Combine(root, prefix);
            var fake = new FakeGit { PortablePaths = true };
            var fetcher = new RulesFetcher(cache, fake.Run);
            var tree = fetcher.Fetch(Rules);
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(cache, Commit, "verified.json")));
            Assert.Equal(expected, marker.RootElement.GetProperty("treeSha256").GetString());
            Assert.Equal("nested rule\n", File.ReadAllText(Path.Combine(tree, "a", "b.yaml")));
            Assert.Equal("sibling rule\n", File.ReadAllText(Path.Combine(tree, "a0.yaml")));
            Assert.Equal(2, fetcher.RequireStagedTree(Rules).FileCount);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncompleteExistingEntriesGiveActionableRecoveryWithoutDeletingOrFetching(bool missingTree)
    {
        var fake = new FakeGit();
        new RulesFetcher(root, fake.Run).Fetch(Rules);
        var entry = Path.Combine(root, Commit);
        var tree = Path.Combine(entry, RulesFetcher.TreeDirectoryName);
        var marker = Path.Combine(entry, "verified.json");
        if (missingTree) Directory.Delete(tree, recursive: true);
        else File.Delete(marker);
        var noGit = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch"));

        var error = Assert.Throws<WipException>(() => noGit.Fetch(Rules));

        Assert.Contains("remove the cache entry and prepare again", error.Message);
        Assert.True(Directory.Exists(entry));
        Assert.Equal(missingTree, File.Exists(marker));
        Assert.Equal(!missingTree, Directory.Exists(tree));
        if (!missingTree) Assert.Equal("pinned rule content", File.ReadAllText(Path.Combine(tree, "rules.yaml")));
        Assert.Equal(4, fake.Commands.Count);
    }

    [Fact]
    public void DerivedStagingPathRequiresNoDirectoryOrGitAccess()
    {
        var fetcher = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch"));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), Commit, RulesFetcher.TreeDirectoryName), fetcher.StagingPath(Rules));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void PublicationNeverMovesOrDeletesOpenGitMetadataBeforePublishingTheRules()
    {
        var fake = new FakeGit { HoldMetadataOpen = true };
        try
        {
            var staged = new RulesFetcher(root, fake.Run).Fetch(Rules);
            Assert.Equal("pinned rule content", File.ReadAllText(Path.Combine(staged, "rules.yaml")));
            var published = Path.GetDirectoryName(staged)!;
            Assert.Equal(new[] { "tree", "verified.json" }, Directory.EnumerateFileSystemEntries(published)
                .Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(staged, ".git")));
            Assert.All(fake.OpenMetadata, stream => Assert.Equal((int)'g', stream.ReadByte()));
            Assert.All(fake.Environments, environment =>
            {
                var publication = Path.GetDirectoryName(environment["GIT_WORK_TREE"])!;
                Assert.False(environment["GIT_DIR"].StartsWith(publication + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            });
        }
        finally { foreach (var stream in fake.OpenMetadata) stream.Dispose(); }
    }

    [Fact]
    public void InjectedGitReceivesTheSameIsolatedEnvironmentAsTheRealRunner()
    {
        var fake = new FakeGit();
        new RulesFetcher(root, fake.Run).Fetch(Rules);
        Assert.Equal(4, fake.Environments.Count);
        var first = fake.Environments[0];
        Assert.All(fake.Environments, environment =>
        {
            Assert.Equal(first.OrderBy(pair => pair.Key), environment.OrderBy(pair => pair.Key));
            Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
            Assert.Equal("0", environment["GIT_CONFIG_COUNT"]);
            Assert.Equal(environment["GIT_DIR"], environment["GIT_COMMON_DIR"]);
            Assert.Equal(Path.Combine(environment["GIT_DIR"], "index"), environment["GIT_INDEX_FILE"]);
            Assert.Equal(Path.Combine(environment["GIT_DIR"], "objects"), environment["GIT_OBJECT_DIRECTORY"]);
            var publication = Path.GetDirectoryName(environment["GIT_WORK_TREE"])!;
            Assert.False(environment["GIT_DIR"].StartsWith(publication + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        });
        Assert.All(fake.Commands.Zip(fake.Environments), pair =>
        {
            var arguments = pair.First.ToList();
            Assert.Equal(pair.Second["GIT_WORK_TREE"], arguments[arguments.IndexOf("-C") + 1]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransientPublicationFailuresRetryOnlyTheMoveWithoutRefetching(bool accessDenied)
    {
        var fake = new FakeGit();
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var fetcher = new RulesFetcher(root, fake.Run, (source, destination) =>
        {
            attempts++;
            if (attempts < 3) throw PublishError(accessDenied);
            Directory.Move(source, destination);
        }, waits.Add);

        var staged = fetcher.Fetch(Rules);

        Assert.Equal("pinned rule content", File.ReadAllText(Path.Combine(staged, "rules.yaml")));
        // The real move may encounter an additional host-held handle after our two
        // injected failures; keep the cap and git sequence strict instead of host timing.
        Assert.InRange(attempts, 3, 5);
        Assert.Equal(Enumerable.Range(1, attempts - 1).Select(attempt => TimeSpan.FromMilliseconds(attempt * 100)), waits);
        Assert.Equal(4, fake.Commands.Count);
        Assert.Equal(new[] { "init", "fetch", "rev-parse", "checkout" }, fake.Operations);
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedPublicationRetriesAreBoundedAndFailClosed(bool accessDenied)
    {
        var fake = new FakeGit();
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var error = PublishError(accessDenied);
        var fetcher = new RulesFetcher(root, fake.Run, (_, _) => { attempts++; throw error; }, waits.Add);

        var failure = Assert.Throws<WipException>(() => fetcher.Fetch(Rules));

        Assert.Equal("Pinned rules staging could not be published after 5 attempts", failure.Message);
        Assert.Same(error, failure.InnerException);
        Assert.Equal(5, attempts);
        Assert.Equal(new[] { 100d, 200d, 300d, 400d }, waits.Select(wait => wait.TotalMilliseconds));
        Assert.Equal(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(waits.Sum(wait => wait.TotalMilliseconds)));
        Assert.Equal(4, fake.Commands.Count);
        Assert.False(Directory.Exists(Path.Combine(root, Commit)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AConcurrentVerifiedPublicationWinsWithoutRetryingOrWaiting(bool accessDenied)
    {
        var fake = new FakeGit();
        var attempts = 0;
        var publishedAtAttempt = 0;
        var waitsAtPublication = -1;
        var waits = new List<TimeSpan>();
        var fetcher = new RulesFetcher(root, fake.Run, (source, destination) =>
        {
            attempts++;
            Directory.Move(source, destination);
            publishedAtAttempt = attempts;
            waitsAtPublication = waits.Count;
            throw PublishError(accessDenied);
        }, waits.Add);

        var staged = fetcher.Fetch(Rules);

        Assert.Equal("pinned rule content", File.ReadAllText(Path.Combine(staged, "rules.yaml")));
        // Earlier real moves may fail transiently, but publication must end retries and
        // backoff immediately, including after our injected concurrent-publisher error.
        Assert.InRange(attempts, 1, 5);
        Assert.Equal(publishedAtAttempt, attempts);
        Assert.Equal(waitsAtPublication, waits.Count);
        Assert.Equal(Enumerable.Range(1, attempts - 1).Select(attempt => TimeSpan.FromMilliseconds(attempt * 100)), waits);
        Assert.Equal(4, fake.Commands.Count);
        Assert.Equal(new[] { "init", "fetch", "rev-parse", "checkout" }, fake.Operations);
        Assert.Empty(Directory.EnumerateDirectories(root, ".tmp-*"));
    }

    [Fact]
    public void AConcurrentDirectoryStillNeedsContentVerification()
    {
        var fake = new FakeGit();
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var fetcher = new RulesFetcher(root, fake.Run, (source, destination) =>
        {
            attempts++;
            Directory.Move(source, destination);
            File.WriteAllText(Path.Combine(destination, "tree", "rules.yaml"), "unverified content");
            throw new IOException("another publisher won");
        }, waits.Add);

        var failure = Assert.Throws<WipException>(() => fetcher.Fetch(Rules));

        Assert.Contains("verified content", failure.Message);
        Assert.Equal(1, attempts);
        Assert.Empty(waits);
        Assert.Equal(4, fake.Commands.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GitFetchOrVerificationFailureNeverInvokesPublicationOrBackoff(bool fetchFails)
    {
        var fake = new FakeGit { FetchCode = fetchFails ? 9 : 0, ResolvedCommit = new string('f', 40) };
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var fetcher = new RulesFetcher(root, fake.Run, (_, _) => attempts++, waits.Add);
        Assert.Throws<WipException>(() => fetcher.Fetch(Rules));
        Assert.Equal(0, attempts);
        Assert.Empty(waits);
        Assert.Equal(fetchFails ? 2 : 3, fake.Commands.Count);
    }

    [Fact]
    public void UnrelatedMoveFailuresAreNotRetried()
    {
        var fake = new FakeGit();
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var fetcher = new RulesFetcher(root, fake.Run, (_, _) =>
        {
            attempts++;
            throw new InvalidOperationException("unrelated failure");
        }, waits.Add);
        Assert.Throws<InvalidOperationException>(() => fetcher.Fetch(Rules));
        Assert.Equal(1, attempts);
        Assert.Empty(waits);
        Assert.Equal(4, fake.Commands.Count);
    }

    [Fact]
    public void AnExceptionFromBackoffCannotRetryTheMoveAgain()
    {
        var fake = new FakeGit();
        var attempts = 0;
        var pauses = 0;
        var fetcher = new RulesFetcher(root, fake.Run, (_, _) =>
        {
            attempts++;
            throw new IOException("move is temporarily blocked");
        }, _ => { pauses++; throw new IOException("backoff failed"); });
        Assert.Throws<WipException>(() => fetcher.Fetch(Rules));
        Assert.Equal(1, attempts);
        Assert.Equal(1, pauses);
        Assert.Equal(4, fake.Commands.Count);
    }

    [Fact]
    public void SubpathsPublishOnlyThoseSubtreesAtTheirOriginalPositions()
    {
        var rules = Rules with { Subpaths = ["http/misconfiguration", "helpers"] };
        var fake = new FakeGit();
        var fetcher = new RulesFetcher(root, fake.Run);

        var staged = fetcher.Fetch(rules);

        // One checkout takes every pathspec, in canonical order.
        var checkout = Assert.Single(fake.Commands, command => command.Contains("checkout"));
        Assert.Equal(new[] { Commit, "--", "helpers", "http/misconfiguration" }, checkout.TakeLast(4));
        Assert.All(fake.Environments, environment => Assert.Equal("1", environment["GIT_LITERAL_PATHSPECS"]));
        Assert.Equal(new[] { "helpers/rule.yaml", "http/misconfiguration/rule.yaml" },
            Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(staged, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal));
        Assert.Equal(2, fetcher.RequireStagedTree(rules).FileCount);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(staged)!, "verified.json")));
        Assert.Equal(new[] { "helpers", "http/misconfiguration" },
            marker.RootElement.GetProperty("subpaths").EnumerateArray().Select(value => value.GetString()));
        // The same set in another order is the same pin and reuses the entry without git.
        var reordered = Rules with { Subpaths = ["helpers", "http/misconfiguration"] };
        Assert.Equal(rules, reordered);
        Assert.Equal(staged, new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch")).Fetch(reordered));
        // Neither a whole-tree pin nor another set may reuse this entry.
        Assert.NotEqual(fetcher.StagingPath(Rules), staged);
        Assert.NotEqual(fetcher.StagingPath(Rules with { Subpaths = ["http/misconfiguration"] }), staged);
        Assert.Throws<WipException>(() => fetcher.RequireStaged(Rules));
        Assert.Throws<WipException>(() => fetcher.RequireStaged(Rules with { Subpaths = ["http/misconfiguration"] }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[\"http/misconfiguration\"]")]
    [InlineData("[\"http/misconfiguration\",\"helpers\"]")]
    [InlineData("\"helpers\"")]
    public void AMarkerRecordingAnotherSubpathSetIsRefusedOnReuse(string? recorded)
    {
        var rules = Rules with { Subpaths = ["http/misconfiguration", "helpers"] };
        var staged = new RulesFetcher(root, new FakeGit().Run).Fetch(rules);
        var markerPath = Path.Combine(Path.GetDirectoryName(staged)!, "verified.json");
        var marker = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(markerPath))!;
        marker.Remove("subpaths");
        if (recorded is not null) marker["subpaths"] = System.Text.Json.Nodes.JsonNode.Parse(recorded);
        File.WriteAllText(markerPath, marker.ToJsonString());
        var noGit = new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("must not fetch"));

        var error = Assert.Throws<WipException>(() => noGit.RequireStaged(rules));
        Assert.Contains("prepare again", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/http")]
    [InlineData("http/")]
    [InlineData("http//misconfiguration")]
    [InlineData("./http")]
    [InlineData("http/../misconfiguration")]
    [InlineData(":(glob)http")]
    [InlineData("http/*")]
    [InlineData("http\\misconfiguration")]
    [InlineData("http\nmisconfiguration")]
    public void MalformedSubpathsAreRefusedBeforeGitRuns(string subpath)
    {
        // A malformed entry is refused alone and next to a valid one.
        foreach (var rules in new[] { Rules with { Subpaths = [subpath] }, Rules with { Subpaths = ["helpers", subpath] } })
        {
            var error = Assert.Throws<ConfigException>(() => rules.Validate("fixture"));
            Assert.Contains("subpath", error.Message);
            Assert.Throws<ConfigException>(() =>
                new RulesFetcher(root, (_, _, _) => throw new InvalidOperationException("git must not run")).Fetch(rules));
        }
    }

    // An empty set must not quietly mean the whole tree; repeats and nesting would give one tree two keys.
    [Theory]
    [InlineData("")]
    [InlineData("helpers,helpers")]
    [InlineData("http,http/misconfiguration")]
    public void EmptyRepeatedOrNestedSubpathSetsAreRefused(string subpaths)
    {
        var rules = Rules with { Subpaths = subpaths.Split(',', StringSplitOptions.RemoveEmptyEntries) };
        var error = Assert.Throws<ConfigException>(() => rules.Validate("fixture"));
        Assert.Contains("subpaths", error.Message);
    }

    private static Exception PublishError(bool accessDenied) => accessDenied
        ? new UnauthorizedAccessException("external directory handle") : new IOException("external directory handle");

    private sealed class FakeGit
    {
        internal string ResolvedCommit { get; init; } = Commit;
        internal int FetchCode { get; init; }
        internal bool EmptyCheckout { get; init; }
        internal bool NestedFiles { get; init; }
        internal bool PortablePaths { get; init; }
        internal string? LinkTarget { get; init; }
        internal List<IReadOnlyList<string>> Commands { get; } = [];
        internal List<string> Operations { get; } = [];
        internal List<TimeSpan> Timeouts { get; } = [];
        internal List<IReadOnlyDictionary<string, string>> Environments { get; } = [];
        internal List<FileStream> OpenMetadata { get; } = [];
        internal bool HoldMetadataOpen { get; init; }

        internal (int Code, string Output) Run(IReadOnlyList<string> command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            Commands.Add(command.ToArray());
            Timeouts.Add(timeout);
            Environments.Add(new Dictionary<string, string>(environment));
            var operation = new[] { "init", "fetch", "rev-parse", "checkout" }.Single(command.Contains);
            Operations.Add(operation);
            var arguments = command.ToList();
            var tree = arguments[arguments.IndexOf("-C") + 1];
            Assert.False(Directory.Exists(Path.Combine(tree, ".git")));
            Assert.False(File.Exists(Path.Combine(tree, ".git")));
            switch (operation)
            {
                case "init":
                    Directory.CreateDirectory(Path.Combine(environment["GIT_DIR"], "objects"));
                    File.WriteAllText(Path.Combine(environment["GIT_DIR"], "config"), "git metadata");
                    return (0, "");
                case "fetch": return (FetchCode, "");
                case "rev-parse": return (0, ResolvedCommit + "\n");
                case "checkout":
                    if (HoldMetadataOpen)
                        OpenMetadata.Add(new FileStream(Path.Combine(environment["GIT_DIR"], "config"), FileMode.Open, FileAccess.Read, FileShare.Read));
                    var separator = arguments.LastIndexOf("--");
                    if (separator < arguments.Count - 1)
                        foreach (var pathspec in arguments.Skip(separator + 1))
                        {
                            var subtree = Path.Combine(tree, pathspec);
                            Directory.CreateDirectory(subtree);
                            File.WriteAllText(Path.Combine(subtree, "rule.yaml"), "pinned rule content");
                        }
                    else if (PortablePaths)
                    {
                        Directory.CreateDirectory(Path.Combine(tree, "a"));
                        File.WriteAllText(Path.Combine(tree, "a", "b.yaml"), "nested rule\n");
                        File.WriteAllText(Path.Combine(tree, "a0.yaml"), "sibling rule\n");
                    }
                    else if (!EmptyCheckout) File.WriteAllText(Path.Combine(tree, "rules.yaml"), "pinned rule content");
                    if (NestedFiles)
                    {
                        Directory.CreateDirectory(Path.Combine(tree, "nested"));
                        File.WriteAllText(Path.Combine(tree, "another.yaml"), "another rule");
                        File.WriteAllText(Path.Combine(tree, "nested", "nested.yaml"), "nested rule");
                    }
                    if (LinkTarget is not null) File.CreateSymbolicLink(Path.Combine(tree, "linked-rule.yaml"), LinkTarget);
                    return (0, "");
                default: throw new InvalidOperationException("unexpected git operation");
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
