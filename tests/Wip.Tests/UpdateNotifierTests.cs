using System.Net;
using Wip.Diagnostics;

namespace Wip.Tests;

public class UpdateNotifierTests
{
    [Fact]
    public void FindsHighestVersionInWinGetDirectoryListing()
    {
        const string response = """
            [
              { "name": "2.4.9", "type": "dir" },
              { "name": "not-a-version", "type": "file" },
              { "name": "2.10.0", "type": "dir" },
              { "name": "2.5.2", "type": "dir" }
            ]
            """;

        Assert.Equal("2.10.0", UpdateNotifier.FindLatestVersion(response));
    }

    [Fact]
    public void IgnoresNonDirectoryEntriesEvenWithAHigherVersionName()
    {
        const string response = """
            [
              { "name": "2.5.2", "type": "dir" },
              { "name": "9.9.9", "type": "file" }
            ]
            """;

        Assert.Equal("2.5.2", UpdateNotifier.FindLatestVersion(response));
    }

    [Fact]
    public void PicksAReleaseOverASamePrereleaseRegardlessOfManifestOrder()
    {
        const string prereleaseFirst = """
            [
              { "name": "2.5.2-rc.1", "type": "dir" },
              { "name": "2.5.2", "type": "dir" }
            ]
            """;
        const string releaseFirst = """
            [
              { "name": "2.5.2", "type": "dir" },
              { "name": "2.5.2-rc.1", "type": "dir" }
            ]
            """;

        Assert.Equal("2.5.2", UpdateNotifier.FindLatestVersion(prereleaseFirst));
        Assert.Equal("2.5.2", UpdateNotifier.FindLatestVersion(releaseFirst));
    }

    [Fact]
    public void SkipsDirectoriesWithAMalformedPrereleaseTag()
    {
        const string response = """
            [
              { "name": "2.5.2-rc.01", "type": "dir" },
              { "name": "2.4.9", "type": "dir" }
            ]
            """;

        // "01" is not a valid SemVer identifier (a numeric identifier can't have a leading
        // zero, and an alphanumeric one needs a non-digit character), so the whole directory
        // name is rejected rather than winning by some fallback ordering.
        Assert.Equal("2.4.9", UpdateNotifier.FindLatestVersion(response));
    }

    [Theory]
    [InlineData("2.5.3", "2.5.2", true)]
    [InlineData("2.5.2", "2.5.2", false)]
    [InlineData("2.5.1", "2.5.2", false)]
    [InlineData("v3.0.0", "2.5.2+build", true)]
    [InlineData("invalid", "2.5.2", false)]
    [InlineData("2.5.2", "2.5.2-rc.1", true)]
    [InlineData("2.5.2-rc.1", "2.5.2", false)]
    [InlineData("2.5.2-rc.2", "2.5.2-rc.1", true)]
    [InlineData("2.5.2-rc.1", "2.5.2-rc.2", false)]
    [InlineData("2.5.2-beta", "2.5.2-alpha", true)]
    [InlineData("2.5.2-rc.1", "2.5.2-rc.1.1", false)]
    [InlineData("2.5.2-rc.99999999999999999999", "2.5.2-rc.1", true)]
    [InlineData("2.5.2-rc.99999999999999999999", "2.5.2-rc.99999999999999999998", true)]
    [InlineData("2.5.2-rc.01", "2.5.2-rc.1", false)]
    [InlineData("2.5.2-rc.1", "2.5.2-rc.01", false)]
    public void ComparesVersions(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateNotifier.IsNewer(candidate, current));
    }

    [Fact]
    public void FormatsProminentPlainUpdateNotice()
    {
        var actual = Log.FormatUpdateAvailable("2.5.2", "2.6.0", colorize: false);

        Assert.Contains("*** WIP UPDATE AVAILABLE ***", actual);
        Assert.Contains("2.5.2 -> 2.6.0", actual);
        Assert.Contains("winget upgrade --id Slidict.Wip --exact", actual);
        Assert.Contains("scoop update wip", actual);
        Assert.DoesNotContain('\x1b', actual);
    }

    [Fact]
    public void HighlightsUpdateHeadingAndCommandWhenColorEnabled()
    {
        var actual = Log.FormatUpdateAvailable("2.5.2", "2.6.0", colorize: true);

        Assert.Contains("\x1b[1;35m*** WIP UPDATE AVAILABLE ***\x1b[0m", actual);
        Assert.Contains("\x1b[1;35m", actual);
    }

    [Fact]
    public void ReadCacheTreatsAMissingFileAsNeverChecked()
    {
        var path = TempCachePath();

        var (checkedAt, latest) = UpdateNotifier.ReadCache(path);

        Assert.Equal(DateTimeOffset.MinValue, checkedAt);
        Assert.Null(latest);
    }

    [Fact]
    public void WriteThenReadCacheRoundTrips()
    {
        var path = TempCachePath();
        try
        {
            UpdateNotifier.WriteCache("2.6.0", path);

            var (checkedAt, latest) = UpdateNotifier.ReadCache(path);

            Assert.Equal("2.6.0", latest);
            Assert.True(DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromMinutes(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadCacheTreatsCorruptedJsonAsNeverChecked()
    {
        var path = TempCachePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not valid json");

            var (checkedAt, latest) = UpdateNotifier.ReadCache(path);

            Assert.Equal(DateTimeOffset.MinValue, checkedAt);
            Assert.Null(latest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadCacheTreatsAMissingPropertyAsNeverChecked()
    {
        var path = TempCachePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "latest": "2.6.0" }""");

            var (checkedAt, latest) = UpdateNotifier.ReadCache(path);

            Assert.Equal(DateTimeOffset.MinValue, checkedAt);
            Assert.Null(latest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RefreshCacheWritesTheLatestVersionOnSuccess()
    {
        var path = TempCachePath();
        try
        {
            const string response = """
                [
                  { "name": "2.5.2", "type": "dir" },
                  { "name": "2.9.0", "type": "dir" }
                ]
                """;
            var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });

            UpdateNotifier.RefreshCache(handler, path);

            var (_, latest) = UpdateNotifier.ReadCache(path);
            Assert.Equal("2.9.0", latest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RefreshCachePreservesThePreviousResultWhenTheRequestFails()
    {
        var path = TempCachePath();
        try
        {
            WriteStaleCache(path, "2.5.2");
            var handler = new StubHandler(() => throw new HttpRequestException("offline"));

            UpdateNotifier.RefreshCache(handler, path);

            var (checkedAt, latest) = UpdateNotifier.ReadCache(path);
            Assert.Equal("2.5.2", latest);
            Assert.True(DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromMinutes(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RefreshCacheSkipsARedundantRequestWhenTheCacheIsAlreadyFresh()
    {
        var path = TempCachePath();
        try
        {
            // Simulates a different helper having already refreshed the cache between when
            // this one was spawned and when it won the lock: nothing here holds the lock, but
            // the cache itself is already fresh, so RefreshCache's own double-check should
            // skip the network call entirely.
            UpdateNotifier.WriteCache("2.5.2", path);
            var requested = false;
            var handler = new StubHandler(() =>
            {
                requested = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            });

            UpdateNotifier.RefreshCache(handler, path);

            Assert.False(requested);
            var (_, latest) = UpdateNotifier.ReadCache(path);
            Assert.Equal("2.5.2", latest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RefreshCacheReleasesItsLockEvenWhenTheRequestFails()
    {
        var path = TempCachePath();
        var lockPath = $"{path}.lock";
        try
        {
            var handler = new StubHandler(() => throw new HttpRequestException("offline"));

            UpdateNotifier.RefreshCache(handler, path);

            // Throws if the lock is still held, failing the test.
            using var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        }
        finally
        {
            File.Delete(path);
            File.Delete(lockPath);
        }
    }

    [Fact]
    public void RefreshCacheSwallowsAFailureToCreateTheLockDirectory()
    {
        // A file where the lock's parent directory needs to be makes Directory.CreateDirectory
        // throw IOException, the same shape an unwritable LocalApplicationData would produce.
        // RefreshCache() runs directly inside the detached helper process with nothing above it
        // to catch this, so it must not escape here either.
        var blocker = TempCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "not a directory");
        var cachePath = Path.Combine(blocker, "update.json");
        try
        {
            var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]"),
            });

            var exception = Record.Exception(() => UpdateNotifier.RefreshCache(handler, cachePath));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void RefreshCacheSkipsTheRequestWhenAnotherRefreshHoldsTheLock()
    {
        var path = TempCachePath();
        var lockPath = $"{path}.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        try
        {
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                var requested = false;
                var handler = new StubHandler(() =>
                {
                    requested = true;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
                });

                UpdateNotifier.RefreshCache(handler, path);

                Assert.False(requested);
                Assert.False(File.Exists(path));
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(lockPath);
        }
    }

    private static string TempCachePath() =>
        Path.Combine(Path.GetTempPath(), "wip-tests", $"update-{Guid.NewGuid():N}.json");

    private static void WriteStaleCache(string path, string? latest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var checkedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(2);
        var latestJson = latest is null ? "null" : $"\"{latest}\"";
        File.WriteAllText(path, $$"""{"checkedAt":"{{checkedAt:O}}","latest":{{latestJson}}}""");
    }

    private sealed class StubHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory());
    }
}
