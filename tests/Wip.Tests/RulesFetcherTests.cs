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
                    if (PortablePaths)
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
