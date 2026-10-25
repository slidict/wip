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
}
