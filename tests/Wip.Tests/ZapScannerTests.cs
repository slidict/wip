using System.Text.Json;
using Wip.Security;
using Wip.Yaml;

namespace Wip.Tests;

public class ZapScannerTests
{
    private const string Image = "ghcr.io/zaproxy/zaproxy@sha256:5ccfff9a411743ab2615136a2edd7416bec9ce41572fa94e935df38c14adb2b6";

    private static ScanPlan Plan(string target = "http://app.test:8080/app") => ScanEngine.Select("zap").Plan(new ScanTarget(target));
    private static OrderedDictionary<string, object?> Mapping(object? raw) => RubyValue.AsMapping(raw)!;
    private static List<object?> Sequence(object? raw) => RubyValue.AsSequence(raw)!;

    [Fact]
    public void ZapIsABuiltInScannerWithDigestPinnedRules()
    {
        var scanner = ScanEngine.Select("zap");
        var plan = scanner.Plan(new ScanTarget("http://app.test:8080/"));

        Assert.Equal("zap", scanner.Name);
        Assert.Contains("zap", ScanEngine.Tools);
        Assert.Equal(Image, plan.Image.Reference);
        Assert.Contains("5ccfff9a411743ab2615136a2edd7416bec9ce41572fa94e935df38c14adb2b6", plan.RulesVersion);
        plan.Validate("zap");
    }

    [Fact]
    public void InvocationDisablesCallsHomeAndOastWithoutUsingAShell()
    {
        var plan = Plan();

        Assert.Equal("python3", plan.Argv[0]);
        Assert.Equal("-c", plan.Argv[1]);
        Assert.Contains("subprocess.call", plan.Argv[2]);
        Assert.Contains("'-silent'", plan.Argv[2]);
        Assert.Contains("'-addonuninstall','oast'", plan.Argv[2]);
        Assert.All(plan.Argv, argument => Assert.DoesNotContain(argument, char.IsControl));
        Assert.Equal("/tmp/wip-zap-report.json", plan.ReportPath);
        Assert.Equal("/tmp/wip-zap-unperformed.txt", plan.UnperformedChecksPath);
        Assert.Contains("Checks not performed", plan.Argv[2]);
    }

    [Theory]
    [InlineData("http://app.test:8080/app")]
    [InlineData("http://app.test:8080/app/")]
    [InlineData("http://app.test:8080/")]
    public void AutomationContextConfinesTheSpiderToTheSelectedSubtree(string target)
    {
        var plan = Plan(target);
        var automation = Mapping(YamlLoader.LoadText(JsonDocument.Parse(plan.Argv[3]).RootElement.GetString()!, allowAliases: false));
        var env = Mapping(automation["env"]);
        var context = Mapping(Sequence(env["contexts"])[0]);
        var include = Assert.IsType<string>(Sequence(context["includePaths"])[0]);
        var exclude = Assert.IsType<string>(Sequence(context["excludePaths"])[0]);
        var selected = target.TrimEnd('/');

        Assert.Matches(include, target);
        Assert.Matches(include, selected + "/child");
        Assert.DoesNotMatch(exclude, target);
        Assert.DoesNotMatch(include, "http://other.test:8080/app");
        Assert.DoesNotMatch(include, "http://app.test:8081/app");
        Assert.DoesNotMatch(include, "https://app.test:8080/app");
        Assert.Matches(exclude, "http://other.test:8080/app");
        if (new Uri(target).AbsolutePath.TrimEnd('/').Length > 0)
        {
            Assert.DoesNotMatch(include, selected + "-other");
            Assert.Matches(exclude, selected + "-other");
        }

        var jobs = Sequence(automation["jobs"]).Select(Mapping).ToArray();
        var spider = Mapping(jobs.Single(job => Equals(job["type"], "spider"))["parameters"]);
        Assert.Equal("wip", spider["context"]);
        Assert.Equal("1", RubyValue.ToStringValue(spider["maxDuration"]));
        Assert.Equal("2", RubyValue.ToStringValue(spider["maxDepth"]));
        Assert.Equal("10", RubyValue.ToStringValue(spider["maxChildren"]));
        Assert.Equal(false, spider["postForm"]);
        Assert.Equal(false, spider["processForm"]);
        Assert.Equal(false, spider["parseRobotsTxt"]);
        Assert.Equal(false, spider["parseSitemapXml"]);
        Assert.DoesNotContain(jobs, job => Equals(job["type"], "activeScan"));
        Assert.DoesNotContain(jobs, job => Equals(job["type"], "spiderAjax"));
        var report = Mapping(jobs.Single(job => Equals(job["type"], "report"))["parameters"]);
        Assert.Equal("traditional-json", report["template"]);
        Assert.Equal("/tmp", report["reportDir"]);
        Assert.Equal("wip-zap-report", report["reportFile"]);
    }

    [Theory]
    [InlineData("http://app.test:8080/app///", "http://app.test:8080/app/")]
    [InlineData("http://app.test:8080//", "http://app.test:8080/other")]
    public void RepeatedTrailingSlashesDoNotBroadenTheSelectedSubtree(string target, string outside)
    {
        var plan = Plan(target);
        var automation = Mapping(YamlLoader.LoadText(JsonDocument.Parse(plan.Argv[3]).RootElement.GetString()!, allowAliases: false));
        var context = Mapping(Sequence(Mapping(automation["env"])["contexts"])[0]);
        var include = Assert.IsType<string>(Sequence(context["includePaths"])[0]);
        var exclude = Assert.IsType<string>(Sequence(context["excludePaths"])[0]);

        Assert.Matches(include, target);
        Assert.Matches(include, target + "child");
        Assert.DoesNotMatch(include, outside);
        Assert.Matches(exclude, outside);
    }

    [Theory]
    [InlineData("app.test")]
    [InlineData("ftp://app.test/")]
    [InlineData("http://user:password@app.test/")]
    public void TargetsMustBeUnauthenticatedHttpUrls(string target)
    {
        var error = Assert.Throws<ConfigException>(() => Plan(target));
        Assert.Contains("unauthenticated HTTP", error.Message);
    }

    /// <summary>
    /// A fragment used to produce a scan that examined nothing and said so nowhere. The seed
    /// keeps the fragment (`AbsoluteUri`) while the scope is built without it
    /// (`GetLeftPart(UriPartial.Path)`), so the seed failed its own `^scope$` include, matched
    /// the complement exclude, and left the spider with no seed at all -- while ZAP still
    /// exited 0 with an empty report.
    /// </summary>
    [Theory]
    [InlineData("http://app.test:8080/app#main")]
    [InlineData("http://app.test:8080/#top")]
    [InlineData("https://app.test/a/b#")]
    public void TargetsMustNotCarryAFragment(string target)
    {
        var error = Assert.Throws<ConfigException>(() => Plan(target));
        Assert.Contains("fragment", error.Message);
    }

    /// <summary>
    /// The shape the rejection above protects: had the fragment reached the plan, the seed
    /// would have been excluded from its own context. Asserted against the regexes the plan
    /// actually emits, so it fails if either side stops dropping or keeping the fragment.
    /// </summary>
    [Fact]
    public void TheSeedUrlIsAlwaysInsideItsOwnIncludeScope()
    {
        foreach (var target in new[] { "http://app.test:8080/app", "http://app.test:8080/", "http://app.test:8080/a?q=1" })
        {
            var plan = Plan(target);
            var automation = Mapping(YamlLoader.LoadText(JsonDocument.Parse(plan.Argv[3]).RootElement.GetString()!, allowAliases: false));
            var context = Mapping(Sequence(Mapping(automation["env"])["contexts"])[0]);
            var seed = Assert.IsType<string>(Sequence(context["urls"])[0]);
            var include = Assert.IsType<string>(Sequence(context["includePaths"])[0]);
            var exclude = Assert.IsType<string>(Sequence(context["excludePaths"])[0]);
            Assert.Matches(include, seed);
            Assert.DoesNotMatch(exclude, seed);
        }
    }
    [Fact]
    public void TraditionalJsonAlertsAndInstancesBecomeFindings()
    {
        const string report = """
            {"site":[{"alerts":[{"pluginid":"10020","alertRef":"10020-1","name":"Missing Header","riskcode":"2","confidence":"3",
            "instances":[{"uri":"http://app.test:8080/app?token=first","method":"get","param":"session","evidence":"header evidence"},
                         {"uri":"https://other.test:443/app?token=second","method":"GET","param":"session","evidence":"other evidence"}]}]}]}
            """;

        var findings = ScanEngine.Select("zap").ConvertReport(report);

        Assert.Equal(2, findings.Count);
        Assert.Equal("10020", findings[0].RuleId);
        Assert.Equal("Missing Header", findings[0].Name);
        Assert.Equal("medium", findings[0].Severity);
        Assert.Equal("high", findings[0].Confidence);
        Assert.Equal(new ScanLocation("/app", "GET", "session"), findings[0].Location);
        Assert.Equal(findings[0].Location, findings[1].Location);
        Assert.Equal("header evidence", findings[0].Evidence);
        Assert.Equal("other evidence", findings[1].Evidence);
    }

    [Fact]
    public void UnusableInstanceUrisDoNotDiscardOtherFindingsOrInstanceFields()
    {
        const string report = """
            {"site":[{"alerts":[{"pluginid":"10020","name":"Header issue","riskcode":"2","confidence":"3",
            "instances":[{"uri":"http://app.test:8080/app","method":"get","param":"valid","evidence":"good evidence"},
                         {"uri":"","method":"post","param":"blank","evidence":"blank URI evidence"},
                         {"uri":"   ","method":"put","param":"whitespace","evidence":"whitespace URI evidence"},
                         {"uri":"not a URI","method":"patch","param":"invalid","evidence":"invalid URI evidence"}]}]}]}
            """;

        var findings = ScanEngine.Select("zap").ConvertReport(report);

        Assert.Equal(4, findings.Count);
        Assert.Equal(new ScanLocation("/app", "GET", "valid"), findings[0].Location);
        Assert.Equal(new ScanLocation(null, "POST", "blank"), findings[1].Location);
        Assert.Equal(new ScanLocation(null, "PUT", "whitespace"), findings[2].Location);
        Assert.Equal(new ScanLocation(null, "PATCH", "invalid"), findings[3].Location);
        Assert.Equal(["good evidence", "blank URI evidence", "whitespace URI evidence", "invalid URI evidence"], findings.Select(finding => finding.Evidence));
        Assert.All(findings, finding =>
        {
            Assert.Equal("10020", finding.RuleId);
            Assert.Equal("Header issue", finding.Name);
            Assert.Equal("medium", finding.Severity);
            Assert.Equal("high", finding.Confidence);
        });
    }

    [Theory]
    [InlineData("{\"value\":\"http://app.test/app\"}")]
    [InlineData("[\"http://app.test/app\"]")]
    [InlineData("42")]
    [InlineData("true")]
    public void NonStringInstanceUrisAreRejected(string uri)
    {
        var report = "{\"site\":[{\"alerts\":[{\"pluginid\":\"10020\",\"instances\":[{\"uri\":" + uri + "}]}]}]}";

        Assert.Throws<FormatException>(() => ScanEngine.Select("zap").ConvertReport(report));
    }

    [Fact]
    public void MissingOptionalFieldsStayAbsentWithoutLosingTheAlert()
    {
        var findings = ScanEngine.Select("zap").ConvertReport("""
            {"site":[{"alerts":[{"alertRef":"10020-1","alert":"Header issue","instances":[{}]}]}]}
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("10020-1", finding.RuleId);
        Assert.Equal("Header issue", finding.Name);
        Assert.Null(finding.Severity);
        Assert.Null(finding.Confidence);
        Assert.Equal(new ScanLocation(null, null, null), finding.Location);
        Assert.Null(finding.Evidence);
    }

    [Fact]
    public void AnAlertWithoutInstancesIsNotConvertedToZeroFindings()
    {
        var finding = Assert.Single(ScanEngine.Select("zap").ConvertReport("""
            {"site":[{"alerts":[{"pluginid":"10020"}]}]}
            """));

        Assert.Equal("10020", finding.RuleId);
        Assert.Null(finding.Location.Path);
    }

    [Fact]
    public void AValidEmptyAlertsArrayConvertsToNoFindings()
    {
        Assert.Empty(ScanEngine.Select("zap").ConvertReport("""{"site":[{"alerts":[]}]}"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"site\":{}}")]
    [InlineData("{\"site\":[{}]}")]
    [InlineData("{\"site\":[{\"alerts\":[null]}]}")]
    [InlineData("{\"site\":[{\"alerts\":[{\"instances\":{}}]}]}")]
    public void MalformedTraditionalReportShapesAreRejected(string report)
    {
        Assert.Throws<FormatException>(() => ScanEngine.Select("zap").ConvertReport(report));
    }

    [Theory]
    [InlineData("0", "0", "informational", "false-positive")]
    [InlineData("1", "1", "low", "low")]
    [InlineData("2", "2", "medium", "medium")]
    [InlineData("3", "3", "high", "high")]
    [InlineData("3", "4", "high", "confirmed")]
    public void ZapCodesUseTheirDocumentedSeverityAndConfidence(string risk, string confidence, string severity, string expectedConfidence)
    {
        var report = "{\"site\":[{\"alerts\":[{\"riskcode\":\"" + risk + "\",\"confidence\":\"" + confidence + "\"}]}]}";
        var finding = Assert.Single(ScanEngine.Select("zap").ConvertReport(report));

        Assert.Equal(severity, finding.Severity);
        Assert.Equal(expectedConfidence, finding.Confidence);
    }

}
