using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wip.Security;

namespace Wip.Tests;

public class NucleiScannerTests
{
    private static ScanPlan Plan(string target = "http://app.test:8080/app") =>
        ScanEngine.Select("nuclei").Plan(new ScanTarget(target));

    [Fact]
    public void ProfilePinsImageAndExternalTemplatesWithoutBackendOptions()
    {
        var plan = Plan();
        plan.Validate("nuclei");
        Assert.Equal(NucleiScanner.ImageReference, plan.Image.Reference);
        Assert.Equal(new PinnedRules("https://github.com/projectdiscovery/nuclei-templates.git",
            "2603fc1f23f2ef219ea4834128a163113c6b5168", "/opt/wip-nuclei-templates", "http/misconfiguration"), plan.Rules);
        Assert.Equal(new[] {
            "nuclei", "-u", "http://app.test:8080/app", "-t", "/opt/wip-nuclei-templates/http/misconfiguration",
            "-duc", "-ni", "-no-stdin", "-nh", "-dr", "-dut", "-pt", "http",
            "-etags", "dos,fuzz,bruteforce", "-rl", "10", "-c", "2", "-timeout", "5", "-retries", "0",
            "-nc", "-jsonl", "-omit-raw", "-omit-template", "-o", "/tmp/wip-nuclei-native.jsonl",
            "-elog", "/tmp/wip-nuclei-errors.jsonl"
        }, plan.Argv.Skip(10));
        Assert.All(plan.Argv, argument => Assert.DoesNotContain(argument, char.IsControl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("app.test")]
    [InlineData("ftp://app.test/")]
    [InlineData("http://user:secret@app.test/")]
    [InlineData("http://app.test/#fragment")]
    [InlineData("http://app.test/a,http://other.test/")]
    public void InvalidOrMultipleTargetsFailBeforeExecution(string target) =>
        Assert.Throws<ConfigException>(() => Plan(target));

    [Fact]
    public void JsonlFindingsPreserveRulesSeverityAndEvidenceButDropUrlSecrets()
    {
        var findings = new NucleiScanner().ConvertReport("""
            {"template-id":"headers","info":{"name":"Missing headers","severity":"info"},"matched-at":"http://app.test/path?token=secret","extracted-results":["one","two"],"matcher-status":true}
            {"template-id":"issue","info":{"name":"Issue","severity":"critical"},"matched-at":"invalid URI","matcher-name":"matched"}
            """ + "\n" + NucleiScanner.Completion);
        Assert.Equal(2, findings.Count);
        Assert.Equal("headers", findings[0].RuleId);
        Assert.Equal("Missing headers", findings[0].Name);
        Assert.Equal("informational", findings[0].Severity);
        Assert.Equal(new ScanLocation("/path", null, null), findings[0].Location);
        Assert.Null(findings[0].Confidence);
        Assert.Equal("one\ntwo", findings[0].Evidence);
        Assert.Equal("critical", findings[1].Severity);
        Assert.Null(findings[1].Location.Path);
        Assert.Equal("matched", findings[1].Evidence);
    }

    [Fact]
    public void OptionalMetadataStaysAbsentAndSuccessfulEmptyReportHasNoFindings()
    {
        var scanner = new NucleiScanner();
        Assert.Empty(scanner.ConvertReport(NucleiScanner.Completion));
        var finding = Assert.Single(scanner.ConvertReport(
            """{"template-id":"rule","info":{}}""" + "\n" + NucleiScanner.Completion));
        Assert.Null(finding.Name);
        Assert.Null(finding.Severity);
        Assert.Null(finding.Evidence);
    }

    // The wrapper publishes whatever nuclei flushed before a fatal error, so a report without
    // the marker is a truncated run. Accepting it would store a partial finding set as
    // "completed" -- the engine only reads the status this conversion implies.
    [Theory]
    [InlineData("""{"template-id":"rule","info":{}}""")]
    [InlineData("")]
    public void AReportWithoutTheCompletionRecordIsRefused(string report)
    {
        var scanner = new NucleiScanner();
        Assert.Throws<FormatException>(() => scanner.ConvertReport(report));
    }

    // An empty extracted-results array is absence, not evidence: joining it yields "", which
    // would otherwise suppress the matcher-name fallback and store an empty string.
    [Fact]
    public void EmptyExtractedResultsFallBackToTheMatcherName()
    {
        var finding = Assert.Single(new NucleiScanner().ConvertReport(
            """{"template-id":"rule","info":{},"extracted-results":[],"matcher-name":"banner"}"""
            + "\n" + NucleiScanner.Completion));
        Assert.Equal("banner", finding.Evidence);
    }

    // A JSON null is how an absent optional field is spelled; only a non-array value is malformed.
    [Fact]
    public void NullExtractedResultsAreAbsenceNotAMalformedArray()
    {
        var finding = Assert.Single(new NucleiScanner().ConvertReport(
            """{"template-id":"rule","info":{},"extracted-results":null}""" + "\n" + NucleiScanner.Completion));
        Assert.Null(finding.Evidence);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"template-id\":\"rule\"}")]
    [InlineData("{\"template-id\":\"\",\"info\":{}}")]
    [InlineData("{\"template-id\":1,\"info\":{}}")]
    [InlineData("{\"template-id\":\"rule\",\"info\":[]}")]
    [InlineData("{\"template-id\":\"rule\",\"info\":{\"severity\":{}}}")]
    [InlineData("{\"template-id\":\"rule\",\"info\":{},\"extracted-results\":[1]}")]
    [InlineData("{\"template-id\":\"rule\",\"info\":{},\"extracted-results\":{}}")]
    [InlineData("{\"template-id\":\"rule\",\"info\":{},\"matched-at\":{}}")]
    [InlineData("{\"template-id\":\"rule\",\"info\":{},\"matcher-status\":false}")]
    [InlineData("{\"wip-nuclei-completed\":\"unknown\"}")]
    [InlineData("{\"wip-nuclei-completed\":\"http-misconfiguration-v1\",\"other\":1}")]
    public void InvalidStructuresAreNotSilentlyLost(string report) =>
        Assert.Throws<FormatException>(() => new NucleiScanner().ConvertReport(report));

    [Fact]
    public void SyntaxErrorsAfterValidFindingsInvalidateTheWholeReport()
    {
        Assert.ThrowsAny<JsonException>(() => new NucleiScanner().ConvertReport("{\"template-id\":\"rule\",\"info\":{}}\n{"));
        Assert.Throws<FormatException>(() => new NucleiScanner().ConvertReport(NucleiScanner.Completion + "\n" + NucleiScanner.Completion));
    }

    [Theory]
    [InlineData("empty", 0, 0)]
    [InlineData("finding", 0, 1)]
    [InlineData("failure", 7, 0)]
    [InlineData("missing", 0, 0)]
    [InlineData("oversized", 0, 0)]
    public void WrapperPreservesNativeLogsAndCannotFabricateMissingOutput(string scenario, int exitCode, int findings)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Wrapper uses Alpine-compatible sh"); return; }
        var root = Path.Combine(Path.GetTempPath(), "wip-nuclei-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stub = Path.Combine(root, "nuclei");
            File.WriteAllText(stub, """
                #!/bin/sh
                while [ "$#" -gt 0 ]; do
                  case "$1" in
                    -o) output=$2; shift;;
                    -elog) errors=$2; shift;;
                    -u) printf '%s' "$2" > "$WIP_CAPTURE"; shift;;
                  esac
                  shift
                done
                printf 'native skipped template\n' >&2
                printf '{"template-id":"stdout-duplicate","info":{}}\n'
                printf 'native request failure\n' > "$errors"
                if [ "$WIP_SCENARIO" = oversized ]; then
                  head -c 3000000 /dev/zero | tr '\0' e >> "$errors"
                  head -c 3000000 /dev/zero | tr '\0' s >&2
                fi
                case "$WIP_SCENARIO" in
                  missing) exit 0;;
                  failure) : > "$output"; exit 7;;
                  finding) printf '%s\n' '{"template-id":"rule","info":{"name":"Issue","severity":"low"}}' > "$output";;
                  empty|oversized) : > "$output";;
                esac
                """);
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var capture = Path.Combine(root, "target");
            const string target = "http://app.test/path?q=$(touch%20marker);literal";
            var plan = Plan(target);
            var args = plan.Argv.Select(argument => argument.StartsWith("/tmp/wip-nuclei-", StringComparison.Ordinal)
                ? Path.Combine(root, Path.GetFileName(argument)) : argument == "nuclei" ? stub : argument).ToArray();
            var report = Path.Combine(root, Path.GetFileName(plan.ReportPath));
            // A previous run must never turn a missing/failed output into success.
            File.WriteAllText(report, NucleiScanner.Completion);
            File.WriteAllText(Path.Combine(root, "wip-nuclei-native.jsonl"), NucleiScanner.Completion);
            var start = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (var argument in args.Skip(1)) start.ArgumentList.Add(argument);
            start.Environment["WIP_SCENARIO"] = scenario;
            start.Environment["WIP_CAPTURE"] = capture;
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(exitCode, process.ExitCode);
            Assert.Equal("", process.StandardOutput.ReadToEnd());
            Assert.Equal("", process.StandardError.ReadToEnd());
            Assert.Equal(new Uri(target).AbsoluteUri, File.ReadAllText(capture));
            var diagnostics = File.ReadAllText(Path.Combine(root, Path.GetFileName(plan.UnperformedChecksPath)));
            Assert.Contains("Checks not performed", diagnostics);
            // nuclei logs to stderr, which the sidecar keeps. Its stdout duplicates every
            // finding already written through -o, and that duplicate must not reach the sidecar:
            // on a noisy target it would pass the engine 1 MiB cap and cost the whole coverage
            // record for a scan that actually succeeded.
            Assert.Contains("native skipped template", diagnostics);
            Assert.Contains("native request failure", diagnostics);
            Assert.DoesNotContain("stdout-duplicate", diagnostics);
            // A refused-connection target makes -elog several MB. Past the engine cap the whole
            // sidecar is lost and a finished scan turns into unperformed-checks-lost.
            if (scenario == "oversized")
            {
                // Both streams are oversized, so both bounds have to apply. One of them
                // missing leaves the sidecar past 3 MB, and two notes prove neither log
                // was dropped whole.
                Assert.Equal(2, Regex.Matches(diagnostics,
                    @"\[wip: truncated, \d+ bytes dropped\]").Count);
                Assert.InRange(diagnostics.Length, 512 * 1024, ScanEngine.MaxReportCharacters - 1);
            }
            if (scenario == "missing") Assert.False(File.Exists(report));
            else if (exitCode != 0) Assert.DoesNotContain(NucleiScanner.Completion, File.ReadAllText(report));
            else
            {
                var raw = File.ReadAllText(report);
                Assert.EndsWith(NucleiScanner.Completion + "\n", raw);
                Assert.Equal(findings, new NucleiScanner().ConvertReport(raw).Count);
            }
            Assert.False(File.Exists(Path.Combine(root, "marker")));
        }
        finally { Directory.Delete(root, true); }
    }
}
