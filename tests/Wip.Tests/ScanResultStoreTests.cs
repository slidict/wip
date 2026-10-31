using System.Text;
using System.Text.Json;
using Wip.Security;

namespace Wip.Tests;

public sealed class ScanResultStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wip-scan-result-" + Guid.NewGuid().ToString("N"));

    private static ScanResult Completed(IReadOnlyList<ScanFinding>? findings = null) => new(
        new(Guid.NewGuid().ToString("N"), DateTimeOffset.Parse("2026-01-02T03:04:05+02:00"),
            DateTimeOffset.Parse("2026-01-02T03:06:07+02:00"), "zap", "example/tool@sha256:" + new string('a', 64),
            "rules-1", "scan", "wip-n-internal", true, "http://target:8080/path", "abc123"),
        "completed", findings ?? [], UnperformedChecks: "not checked: authenticated pages\r\n", ScannerExitCode: 2);

    [Fact]
    public void DocumentCarriesVersionedMetadataAndAnExplicitEmptyCompletedResult()
    {
        var result = Completed();
        using var stream = new MemoryStream();
        ScanResultWriter.Write(stream, result);
        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Empty(root.GetProperty("findings").EnumerateArray());
        Assert.False(root.TryGetProperty("reason", out _));
        Assert.Equal(2, root.GetProperty("scannerExitCode").GetInt32());
        var run = root.GetProperty("run");
        Assert.Equal(result.Run.RunId, run.GetProperty("runId").GetString());
        Assert.Equal("2026-01-02T01:04:05.0000000Z", run.GetProperty("startedAt").GetString());
        Assert.Equal("2026-01-02T01:06:07.0000000Z", run.GetProperty("endedAt").GetString());
        Assert.Equal("zap", run.GetProperty("tool").GetString());
        Assert.Equal(result.Run.Image, run.GetProperty("image").GetString());
        Assert.Equal("rules-1", run.GetProperty("rulesVersion").GetString());
        Assert.Equal("scan", run.GetProperty("sandbox").GetString());
        Assert.Equal("wip-n-internal", run.GetProperty("network").GetString());
        Assert.True(run.GetProperty("networkVerifiedInternal").GetBoolean());
        Assert.Equal("http://target:8080/path", run.GetProperty("target").GetString());
        Assert.Equal("abc123", run.GetProperty("commit").GetString());
        Assert.Equal(result.UnperformedChecks, root.GetProperty("coverage").GetProperty("unperformedChecks").GetString());
    }

    [Theory]
    [InlineData("/blog/2026-10-26-how-we-fixed-the-thing")]
    [InlineData("/products/widget-2000-blue-large")]
    [InlineData("/very-long-descriptive-section-name/x")]
    [InlineData("/static/js/main.9b7c2d1e.chunk.js")]
    [InlineData("/assets/index-a3f9b2c1d4e5.js")]
    [InlineData("/2026-10-26-release-notes")]
    [InlineData("/reset/0123456789abcdef0123456789abcdef")]
    public void RunMetadataPreservesRequestedPathsButStillRedactsQueryTokens(string path)
    {
        var target = "http://app:8080" + path + "?token=target-secret";
        var result = Completed([new("rule", "name", "low", "high", new(path, "GET", null), null)]);
        result = result with { Run = result.Run with { Target = target, Commit = path } };
        using var stream = new MemoryStream();

        ScanResultWriter.Write(stream, result);

        using var document = JsonDocument.Parse(stream.ToArray());
        var run = document.RootElement.GetProperty("run");
        Assert.Equal("http://app:8080" + path + "?token=[REDACTED]", run.GetProperty("target").GetString());
        Assert.Equal(path, run.GetProperty("commit").GetString());
        var findingPath = document.RootElement.GetProperty("findings")[0].GetProperty("location").GetProperty("path").GetString();
        Assert.Equal(path.StartsWith("/reset/", StringComparison.Ordinal) ? "/reset/[REDACTED]" : path, findingPath);
        Assert.DoesNotContain("target-secret", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("report-missing")]
    [InlineData("report-collection-failed")]
    [InlineData("report-empty")]
    [InlineData("report-unparseable")]
    [InlineData("unperformed-checks-lost")]
    public void IncompleteDocumentsHaveAReasonAndNeverAnEmptyFindingsList(string reason)
    {
        var result = Completed() with { Status = "incomplete", Findings = null, IncompleteReason = reason,
            UnperformedChecks = null, CoverageMissingReason = "diagnostics were not collected" };
        using var stream = new MemoryStream();
        ScanResultWriter.Write(stream, result);
        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        Assert.Equal("incomplete", root.GetProperty("status").GetString());
        Assert.Equal(reason, root.GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("findings", out _));
        Assert.Equal(result.CoverageMissingReason, root.GetProperty("coverage").GetProperty("missingReason").GetString());
    }

    [Fact]
    public void QuotesControlsAndMarkupRemainJsonStringData()
    {
        const string hostile = "quote\"\n\u001b[2J <script>data</script>";
        var result = Completed([new("rule", hostile, "low", "high", new("/path", "GET", "q"), hostile)])
            with { UnperformedChecks = hostile };
        using var stream = new MemoryStream();
        ScanResultWriter.Write(stream, result);
        using var document = JsonDocument.Parse(stream.ToArray());
        var finding = document.RootElement.GetProperty("findings")[0];
        Assert.Equal(hostile, finding.GetProperty("name").GetString());
        Assert.Equal(hostile, finding.GetProperty("evidence").GetString());
        Assert.Equal(hostile, document.RootElement.GetProperty("coverage").GetProperty("unperformedChecks").GetString());
    }

    [Fact]
    public void SecretsAreRedactedBeforePersistenceWhileRawArtifactsRemainUnmodified()
    {
        const string evidence = "Authorization: Bearer auth-plain-secret\r\nCookie: sid=cookie-plain-secret\r\naccess_token=token-plain-secret";
        var result = Completed([new("rule", "name", "high", "high", new("/login?api_key=path-secret", "GET", "token=parameter-secret"), evidence)]);
        var runDirectory = new ScanResultStore(directory).Write(result, evidence, result.UnperformedChecks);
        var serialized = File.ReadAllText(Path.Combine(runDirectory, "result.json"));
        using var document = JsonDocument.Parse(serialized);
        var finding = document.RootElement.GetProperty("findings")[0];
        Assert.Contains(ScanResultRedactor.Marker, finding.GetProperty("evidence").GetString());
        foreach (var secret in new[] { "auth-plain-secret", "cookie-plain-secret", "token-plain-secret", "path-secret", "parameter-secret" })
            Assert.DoesNotContain(secret, serialized);
        Assert.Equal(evidence, File.ReadAllText(Path.Combine(runDirectory, "report.json")));
        Assert.Equal(result.UnperformedChecks, File.ReadAllText(Path.Combine(runDirectory, "unperformed-checks.txt")));
    }

    [Fact]
    public void EmbeddedCoverageIsRedactedWhileItsRawArtifactIsPreserved()
    {
        const string raw = "Crawled http://auth:8080/reset?token=coverage-plain-secret\nCookie: sid=coverage-cookie-secret\nCrawled /reset/0123456789abcdef0123456789abcdef\n";
        var result = Completed() with { UnperformedChecks = raw };

        var runDirectory = new ScanResultStore(directory).Write(result, "raw report", raw);
        var serialized = File.ReadAllText(Path.Combine(runDirectory, "result.json"));
        using var document = JsonDocument.Parse(serialized);
        var coverage = document.RootElement.GetProperty("coverage").GetProperty("unperformedChecks").GetString();

        Assert.Contains("[REDACTED]", coverage);
        Assert.Contains("http://auth:8080/reset?token=[REDACTED]", coverage);
        Assert.DoesNotContain("coverage-plain-secret", serialized);
        Assert.DoesNotContain("coverage-cookie-secret", serialized);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", serialized);
        Assert.Equal(raw, File.ReadAllText(Path.Combine(runDirectory, "unperformed-checks.txt")));
    }

    [Fact]
    public void EvidenceForASecretParameterIsMaskedWithoutChangingItsParameterName()
    {
        const string secret = "aStandaloneSecretTokenValue";
        var result = Completed([new("rule", "name", "medium", "high", new("/path", "POST", "access_token"), secret)]);
        using var stream = new MemoryStream();
        ScanResultWriter.Write(stream, result);
        using var document = JsonDocument.Parse(stream.ToArray());
        var finding = document.RootElement.GetProperty("findings")[0];
        Assert.Equal("access_token", finding.GetProperty("location").GetProperty("parameter").GetString());
        Assert.Equal(ScanResultRedactor.Marker, finding.GetProperty("evidence").GetString());
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void EachCompleteFileIsPublishedAtomicallyAndTheResultIsPublishedLast()
    {
        var published = new List<string>();
        var result = Completed();
        var store = new ScanResultStore(directory, (temporary, final) =>
        {
            Assert.True(File.Exists(temporary));
            Assert.False(File.Exists(final));
            Assert.EndsWith(".tmp", temporary);
            if (Path.GetFileName(final) == "result.json")
            {
                using var parsed = JsonDocument.Parse(File.ReadAllBytes(temporary));
                Assert.Equal(result.Run.RunId, parsed.RootElement.GetProperty("run").GetProperty("runId").GetString());
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(final)!, "report.json")));
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(final)!, "unperformed-checks.txt")));
            }
            File.Move(temporary, final);
            published.Add(Path.GetFileName(final));
        });
        var runDirectory = store.Write(result, "raw\n", "omitted\r\n");
        Assert.Equal(Path.Combine(Path.GetFullPath(directory), result.Run.RunId), runDirectory);
        Assert.Equal(new[] { "report.json", "unperformed-checks.txt", "result.json" }, published);
        Assert.Empty(Directory.EnumerateFiles(runDirectory, "*.tmp"));
    }

    [Fact]
    public void FailedPublicationNeverExposesAPartialResultAndCleansTemporaryFiles()
    {
        var result = Completed();
        var store = new ScanResultStore(directory, (_, _) => throw new IOException("fixture write failure"));
        Assert.Throws<WipException>(() => store.Write(result, "raw", "omitted"));
        var runDirectory = Path.Combine(directory, result.Run.RunId);
        Assert.False(File.Exists(Path.Combine(runDirectory, "result.json")));
        Assert.Empty(Directory.EnumerateFiles(runDirectory, "*.tmp"));
    }

    [Fact]
    public void MissingRawArtifactsHaveEmptyFilesAndAnAuthoritativeIncompleteReason()
    {
        var result = Completed() with { Status = "incomplete", Findings = null, IncompleteReason = "timeout",
            UnperformedChecks = null, CoverageMissingReason = "execution timed out" };
        var path = new ScanResultStore(directory).Write(result, null, null);
        Assert.Empty(File.ReadAllText(Path.Combine(path, "report.json")));
        Assert.Empty(File.ReadAllText(Path.Combine(path, "unperformed-checks.txt")));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "result.json")));
        Assert.Equal("timeout", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void ARunIdCannotEscapeTheRequestedOutputDirectory()
    {
        var result = Completed();
        result = result with { Run = result.Run with { RunId = "../elsewhere" } };
        Assert.Throws<ConfigException>(() => new ScanResultStore(directory).Write(result, "raw", "omitted"));
    }

    /// <summary>
    /// An unusable output path used to surface from Write, which runs after the scan: the
    /// ArgumentException from an empty path is neither IO failure Write converts, so it left
    /// the CLI through the top-level handler and took a completed scan's report with it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyOutputDirectoryIsRefusedBeforeAnythingRuns(string requested)
    {
        var exception = Assert.Throws<ConfigException>(() => ScanResultStore.RequireUsable(requested));
        Assert.Contains("--output", exception.Message);
    }

    [Fact]
    public void AUsableOutputDirectoryIsAcceptedAndCreated()
    {
        var nested = Path.Combine(directory, "results", "nested");
        ScanResultStore.RequireUsable(nested);
        Assert.True(Directory.Exists(nested));
        // Idempotent: the CLI checks before the image pull and the engine checks again.
        ScanResultStore.RequireUsable(nested);
    }

    [Fact]
    public void AnOutputPathThatIsAFileIsRefused()
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "occupied");
        File.WriteAllText(file, "not a directory");
        Assert.Throws<WipException>(() => ScanResultStore.RequireUsable(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
