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
        _ = new RulesFetcher(root, (_, _) => throw new InvalidOperationException("git must not run"));
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

    [Fact]
    public void AnExactPinCacheHitRequiresNoGitAndSurvivesPreparation()
    {
        var fake = new FakeGit();
        var first = new RulesFetcher(root, fake.Run).Fetch(Rules);
        var noGit = new RulesFetcher(root, (_, _) => throw new InvalidOperationException("cache hit must not use git"));
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
        var fetcher = new RulesFetcher(root, (_, _) => throws ? throw new Win32Exception(2) : (127, ""));
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
        var fetcher = new RulesFetcher(root, (_, _) => throw new InvalidOperationException("must not fetch in Run"));
        Assert.Throws<WipException>(() => fetcher.RequireStaged(Rules));
        Assert.Throws<WipException>(() => fetcher.Fetch(Rules));
    }

    [Fact]
    public void ModifiedCachedContentIsRejectedWithoutNetworkAccess()
    {
        var fake = new FakeGit();
        var path = new RulesFetcher(root, fake.Run).Fetch(Rules);
        File.WriteAllText(Path.Combine(path, "rules.yaml"), "modified content");
        var fetcher = new RulesFetcher(root, (_, _) => throw new InvalidOperationException("must not fetch"));
        Assert.Throws<WipException>(() => fetcher.RequireStaged(Rules));
        Assert.Throws<WipException>(() => fetcher.Fetch(Rules));
    }

    [Fact]
    public void RunCannotFetchRulesThatWereNeverPrepared()
    {
        var fetcher = new RulesFetcher(root, (_, _) => throw new InvalidOperationException("must not fetch"));
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
        var noGit = new RulesFetcher(root, (_, _) => throw new InvalidOperationException("must not fetch"));
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
        var noGit = new RulesFetcher(root, (_, _) => throw new InvalidOperationException("legacy entries must not silently fetch"));
        var failure = Assert.Throws<WipException>(() => noGit.Fetch(Rules));
        Assert.Contains("prepare again", failure.Message);
        Assert.Throws<WipException>(() => noGit.RequireStagedTree(Rules));
        Assert.Equal(4, fake.Commands.Count);
    }

    private sealed class FakeGit
    {
        internal string ResolvedCommit { get; init; } = Commit;
        internal int FetchCode { get; init; }
        internal bool EmptyCheckout { get; init; }
        internal bool NestedFiles { get; init; }
        internal string? LinkTarget { get; init; }
        internal List<IReadOnlyList<string>> Commands { get; } = [];
        internal List<string> Operations { get; } = [];
        internal List<TimeSpan> Timeouts { get; } = [];

        internal (int Code, string Output) Run(IReadOnlyList<string> command, TimeSpan timeout)
        {
            Commands.Add(command.ToArray());
            Timeouts.Add(timeout);
            var operation = new[] { "init", "fetch", "rev-parse", "checkout" }.Single(command.Contains);
            Operations.Add(operation);
            var arguments = command.ToList();
            var tree = arguments[arguments.IndexOf("-C") + 1];
            switch (operation)
            {
                case "init":
                    Directory.CreateDirectory(Path.Combine(tree, ".git", "objects"));
                    File.WriteAllText(Path.Combine(tree, ".git", "config"), "git metadata");
                    return (0, "");
                case "fetch": return (FetchCode, "");
                case "rev-parse": return (0, ResolvedCommit + "\n");
                case "checkout":
                    if (!EmptyCheckout) File.WriteAllText(Path.Combine(tree, "rules.yaml"), "pinned rule content");
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
