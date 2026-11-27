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
        Assert.Contains("Checks not performed", JsonDocument.Parse(plan.Argv[7]).RootElement.GetString());
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

    // A nested, array, boolean or numeric uri is a template shape change: tolerating any of
    // them would drop every finding location while the run still reported completed with a
    // full list. A number is refused even though the sibling readers accept one, because uri
    // is the location and losing it silently is the failure this check exists for.
    [Theory]
    [InlineData("{\"value\":\"http://app.test/app\"}")]
    [InlineData("[\"http://app.test/app\"]")]
    [InlineData("true")]
    [InlineData("42")]
    public void StructurallyWrongInstanceUrisAreRejected(string uri)
    {
        var report = "{\"site\":[{\"alerts\":[{\"pluginid\":\"10020\",\"instances\":[{\"uri\":" + uri + "}]}]}]}";

        Assert.Throws<FormatException>(() => ScanEngine.Select("zap").ConvertReport(report));
    }

    // A value that is merely unusable omits the path and keeps the finding: an absent or null
    // field, a blank string, and a string that is not an http(s) URL.
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"not a url\"")]
    [InlineData("\"ftp://app.test/app\"")]
    public void UnusableInstanceUrisOmitThePathAndKeepTheFinding(string uri)
    {
        var report = "{\"site\":[{\"alerts\":[{\"pluginid\":\"10020\",\"instances\":[{\"uri\":" + uri + "}]}]}]}";

        var finding = Assert.Single(ScanEngine.Select("zap").ConvertReport(report));

        Assert.Null(finding.Location.Path);
        Assert.Equal("10020", finding.RuleId);
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


    // Captured from the plan before the bounds became options. The default profile is what this
    // repository's CI budget was sized for, so it must not drift with a later edit.
    private const string DefaultYaml = """
        ---
        env:
          contexts:
          - name: wip
            urls:
            - http://app.test:8080/app
            includePaths:
            - ^http://app\.test:8080/app(?:/.*|\?.*)?$
            excludePaths:
            - ^(?!http://app\.test:8080/app(?:/.*|\?.*)?$).*
          parameters:
            failOnError: true
            failOnWarning: false
            continueOnFailure: false
            progressToStdout: true
        jobs:
        - type: passiveScan-config
          parameters:
            scanOnlyInScope: true
        - type: spider
          parameters:
            context: wip
            url: http://app.test:8080/app
            maxDuration: 1
            maxDepth: 2
            maxChildren: 10
            threadCount: 2
            postForm: false
            processForm: false
            parseRobotsTxt: false
            parseSitemapXml: false
        - type: passiveScan-wait
          parameters:
            maxDuration: 1
        - type: report
          parameters:
            template: traditional-json
            reportDir: /tmp
            reportFile: wip-zap-report
            displayReport: false
        """;

    private static ScanPlan Plan(ZapProfile profile, string target = "http://app.test:8080/app") =>
        new ZapScanner(profile).Plan(new ScanTarget(target));

    private static string PlanYaml(ScanPlan plan) => JsonDocument.Parse(plan.Argv[3]).RootElement.GetString()!;

    private static string PlanCoverage(ScanPlan plan) => JsonDocument.Parse(plan.Argv[7]).RootElement.GetString()!;

    private static OrderedDictionary<string, object?>[] Jobs(ScanPlan plan) =>
        Sequence(Mapping(YamlLoader.LoadText(PlanYaml(plan), allowAliases: false))["jobs"]).Select(Mapping).ToArray();

    [Fact]
    public void TheDefaultPlanIsTheSameYamlAsBeforeTheBoundsBecameOptions()
    {
        // The plan's content is pinned, not its line endings: the raw literal is LF from the
        // source file, while YamlWriter writes Environment.NewLine (CRLF on Windows).
        Assert.Equal((DefaultYaml + "\n").ReplaceLineEndings("\n"), PlanYaml(Plan()).ReplaceLineEndings("\n"));
        Assert.Equal(PlanYaml(Plan()), PlanYaml(Plan(new ZapProfile())));
    }

    [Fact]
    public void RulesIdentifiersNameTheProfileWithoutItsBounds()
    {
        Assert.Equal(Image + "/wip-passive-v2", Plan().RulesVersion);
        Assert.Equal(Image + "/wip-passive-v2", Plan(new ZapProfile(SpiderMinutes: 10, SpiderDepth: 5)).RulesVersion);
        Assert.Equal(Image + "/wip-active-v1", Plan(new ZapProfile(ActiveScan: true)).RulesVersion);
        Assert.Equal(Image + "/wip-active-v1", Plan(new ZapProfile(ActiveScan: true, ActiveScanMinutes: 30)).RulesVersion);
    }

    [Fact]
    public void BoundsAtTheirLimitsAreAccepted()
    {
        Plan(new ZapProfile(1, 1, 1, 1, ActiveScan: true, ActiveScanMinutes: 1)).Validate("zap");
        Plan(new ZapProfile(ZapProfile.MaxSpiderMinutes, ZapProfile.MaxSpiderDepth, ZapProfile.MaxSpiderChildren,
            ZapProfile.MaxPassiveWaitMinutes, ActiveScan: true, ActiveScanMinutes: ZapProfile.MaxActiveScanMinutes)).Validate("zap");
    }

    [Theory]
    [InlineData("--zap-spider-minutes", 0)]
    [InlineData("--zap-spider-minutes", ZapProfile.MaxSpiderMinutes + 1)]
    [InlineData("--zap-spider-depth", -1)]
    [InlineData("--zap-spider-depth", ZapProfile.MaxSpiderDepth + 1)]
    [InlineData("--zap-spider-children", 0)]
    [InlineData("--zap-spider-children", ZapProfile.MaxSpiderChildren + 1)]
    [InlineData("--zap-passive-wait-minutes", 0)]
    [InlineData("--zap-passive-wait-minutes", ZapProfile.MaxPassiveWaitMinutes + 1)]
    [InlineData("--zap-active-scan-minutes", 0)]
    [InlineData("--zap-active-scan-minutes", ZapProfile.MaxActiveScanMinutes + 1)]
    public void BoundsOutsideTheirRangeAreRefusedNamingTheOptionAndRange(string option, int value)
    {
        var (profile, max) = option switch
        {
            "--zap-spider-minutes" => (new ZapProfile(SpiderMinutes: value), ZapProfile.MaxSpiderMinutes),
            "--zap-spider-depth" => (new ZapProfile(SpiderDepth: value), ZapProfile.MaxSpiderDepth),
            "--zap-spider-children" => (new ZapProfile(SpiderChildren: value), ZapProfile.MaxSpiderChildren),
            "--zap-passive-wait-minutes" => (new ZapProfile(PassiveWaitMinutes: value), ZapProfile.MaxPassiveWaitMinutes),
            _ => (new ZapProfile(ActiveScan: true, ActiveScanMinutes: value), ZapProfile.MaxActiveScanMinutes),
        };

        var error = Assert.Throws<ConfigException>(() => Plan(profile));

        Assert.Contains(option, error.Message);
        Assert.Contains($"between 1 and {max}", error.Message);
    }

    // Checked with the active scan off as well, so an out-of-range value is never accepted silently.
    [Fact]
    public void AnOutOfRangeActiveScanDurationIsRefusedEvenWithoutTheActiveScan()
    {
        var error = Assert.Throws<ConfigException>(() => Plan(new ZapProfile(ActiveScanMinutes: -5)));
        Assert.Contains("--zap-active-scan-minutes", error.Message);
    }

    [Fact]
    public void CallerBoundsReachTheSpiderAndPassiveWait()
    {
        var jobs = Jobs(Plan(new ZapProfile(SpiderMinutes: 15, SpiderDepth: 6, SpiderChildren: 50, PassiveWaitMinutes: 10)));
        var spider = Mapping(jobs.Single(job => Equals(job["type"], "spider"))["parameters"]);
        var wait = Mapping(jobs.Single(job => Equals(job["type"], "passiveScan-wait"))["parameters"]);

        Assert.Equal("15", RubyValue.ToStringValue(spider["maxDuration"]));
        Assert.Equal("6", RubyValue.ToStringValue(spider["maxDepth"]));
        Assert.Equal("50", RubyValue.ToStringValue(spider["maxChildren"]));
        Assert.Equal("10", RubyValue.ToStringValue(wait["maxDuration"]));
        Assert.DoesNotContain(jobs, job => Equals(job["type"], "activeScan"));
    }

    [Fact]
    public void TheActiveScanJobRunsOnlyWhenAskedForAndIsBoundedInsideTheContext()
    {
        Assert.Equal(["passiveScan-config", "spider", "passiveScan-wait", "report"],
            Jobs(Plan()).Select(job => job["type"]));

        var jobs = Jobs(Plan(new ZapProfile(ActiveScan: true, ActiveScanMinutes: 20)));
        Assert.Equal(["passiveScan-config", "spider", "activeScan", "passiveScan-wait", "report"],
            jobs.Select(job => job["type"]));
        var active = Mapping(jobs.Single(job => Equals(job["type"], "activeScan"))["parameters"]);
        Assert.Equal("wip", active["context"]);
        Assert.Equal("20", RubyValue.ToStringValue(active["maxScanDurationInMins"]));
        Assert.DoesNotContain(jobs, job => Equals(job["type"], "spiderAjax"));
    }

    [Fact]
    public void TheActiveScanKeepsEverythingThatConfinesTheRun()
    {
        var passive = Plan();
        var active = Plan(new ZapProfile(ActiveScan: true));
        var passiveEnv = Mapping(YamlLoader.LoadText(PlanYaml(passive), allowAliases: false))["env"];
        var activeEnv = Mapping(YamlLoader.LoadText(PlanYaml(active), allowAliases: false))["env"];

        Assert.Equal(YamlWriter.Dump(passiveEnv), YamlWriter.Dump(activeEnv));
        Assert.Equal(passive.Argv[2], active.Argv[2]);
        Assert.Contains("'-silent'", active.Argv[2]);
        Assert.Contains("'-addonuninstall','oast'", active.Argv[2]);
        Assert.Throws<ConfigException>(() => Plan(new ZapProfile(ActiveScan: true), "http://user:secret@app.test/"));
        Assert.Throws<ConfigException>(() => Plan(new ZapProfile(ActiveScan: true), "http://app.test/#main"));
    }

    [Fact]
    public void ThePassiveCoverageRecordStatesTheBoundsUsedAndListsActiveScanningAsNotPerformed()
    {
        Assert.Equal(
            "Checks not performed by this profile: active scanning, authenticated paths, browser/Ajax crawling, form submission, and out-of-band callbacks.\n" +
            "Crawl coverage is bounded to 1 minute, depth 2, 10 children per node, and the selected URL subtree; passive scanning waits at most 1 minute. " +
            "Findings do not prove these omitted checks passed.\n" +
            "Native ZAP automation output follows unchanged:\n",
            PlanCoverage(Plan()));

        var coverage = PlanCoverage(Plan(new ZapProfile(SpiderMinutes: 15, SpiderDepth: 6, SpiderChildren: 50, PassiveWaitMinutes: 10)));
        Assert.Contains("bounded to 15 minutes, depth 6, 50 children per node", coverage);
        Assert.Contains("passive scanning waits at most 10 minutes", coverage);
        Assert.Contains("Checks not performed by this profile: active scanning,", coverage);
    }

    [Fact]
    public void TheActiveCoverageRecordNoLongerListsActiveScanningAsNotPerformed()
    {
        var coverage = PlanCoverage(Plan(new ZapProfile(ActiveScan: true, ActiveScanMinutes: 20)));

        Assert.StartsWith(
            "Checks not performed by this profile: authenticated paths, browser/Ajax crawling, form submission, and out-of-band callbacks.\n",
            coverage);
        Assert.Contains("active scanning of the crawled in-scope requests stops after at most 20 minutes", coverage);
        Assert.Contains("Findings do not prove these omitted checks passed.", coverage);
        Assert.EndsWith("Native ZAP automation output follows unchanged:\n", coverage);
    }

    private static readonly ZapLogin Login = new("http://app.test:8080/app/login",
        "email={%username%}&password={%password%}", "scanner@example.test", "test-password",
        LoggedInRegex: "Sign out", LoggedOutRegex: "Sign in");

    private static OrderedDictionary<string, object?> Context(ScanPlan plan) =>
        Mapping(Sequence(Mapping(Mapping(YamlLoader.LoadText(PlanYaml(plan), allowAliases: false))["env"])["contexts"])[0]);

    private static OrderedDictionary<string, object?> JobParameters(ScanPlan plan, string type) =>
        Mapping(Jobs(plan).Single(job => Equals(job["type"], type))["parameters"]);

    [Fact]
    public void WithoutALoginThePlanHasNoAuthenticationOrUser()
    {
        var plan = Plan(new ZapProfile(ActiveScan: true));
        var context = Context(plan);

        Assert.Equal(["name", "urls", "includePaths", "excludePaths"], context.Keys);
        Assert.False(JobParameters(plan, "spider").ContainsKey("user"));
        Assert.False(JobParameters(plan, "activeScan").ContainsKey("user"));
    }

    [Fact]
    public void ALoginAddsFormAuthenticationCookieSessionAndOneUserToTheContext()
    {
        var context = Context(Plan(new ZapProfile(Login: Login)));

        var authentication = Mapping(context["authentication"]);
        Assert.Equal("form", authentication["method"]);
        var parameters = Mapping(authentication["parameters"]);
        Assert.Equal("http://app.test:8080/app/login", parameters["loginRequestUrl"]);
        Assert.Equal("email={%username%}&password={%password%}", parameters["loginRequestBody"]);
        var verification = Mapping(authentication["verification"]);
        Assert.Equal("response", verification["method"]);
        Assert.Equal("Sign out", verification["loggedInRegex"]);
        Assert.Equal("Sign in", verification["loggedOutRegex"]);
        Assert.Equal("cookie", Mapping(context["sessionManagement"])["method"]);
        var user = Mapping(Assert.Single(Sequence(context["users"])));
        Assert.Equal(ZapLogin.UserName, user["name"]);
        var credentials = Mapping(user["credentials"]);
        Assert.Equal("scanner@example.test", credentials["username"]);
        Assert.Equal("test-password", credentials["password"]);
    }

    [Fact]
    public void OnlyTheGivenIndicatorIsWrittenAndAnEmptyPasswordIsKept()
    {
        var login = Login with { Password = "", LoggedOutRegex = null };
        var context = Context(Plan(new ZapProfile(Login: login)));

        var verification = Mapping(Mapping(context["authentication"])["verification"]);
        Assert.Equal(["method", "loggedInRegex"], verification.Keys);
        var credentials = Mapping(Mapping(Assert.Single(Sequence(context["users"])))["credentials"]);
        Assert.Equal("", RubyValue.ToStringValue(credentials["password"]));
    }

    [Fact]
    public void TheSpiderAndTheActiveScanRunAsTheLoginUser()
    {
        var plan = Plan(new ZapProfile(ActiveScan: true, Login: Login));

        Assert.Equal(ZapLogin.UserName, JobParameters(plan, "spider")["user"]);
        Assert.Equal(ZapLogin.UserName, JobParameters(plan, "activeScan")["user"]);
        Assert.Equal("wip", JobParameters(plan, "activeScan")["context"]);
        Assert.Equal(ZapLogin.UserName, JobParameters(Plan(new ZapProfile(Login: Login)), "spider")["user"]);
    }

    [Fact]
    public void ALoginKeepsEverythingThatConfinesTheRun()
    {
        var plain = Plan(new ZapProfile(ActiveScan: true));
        var login = Plan(new ZapProfile(ActiveScan: true, Login: Login));
        var plainContext = Context(plain);
        var loginContext = Context(login);

        foreach (var key in new[] { "name", "urls", "includePaths", "excludePaths" })
            Assert.Equal(YamlWriter.Dump(plainContext[key]), YamlWriter.Dump(loginContext[key]));
        Assert.Equal(plain.Argv[2], login.Argv[2]);
        Assert.Contains("'-silent'", login.Argv[2]);
        Assert.Contains("'-addonuninstall','oast'", login.Argv[2]);
        Assert.All(login.Argv, argument => Assert.DoesNotContain(argument, char.IsControl));
        Assert.Throws<ConfigException>(() => Plan(new ZapProfile(Login: Login), "http://user:secret@app.test:8080/app"));
        Assert.Throws<ConfigException>(() => Plan(new ZapProfile(Login: Login), "http://app.test:8080/app#main"));
    }

    [Theory]
    [InlineData("http://other.test:8080/app/login")]
    [InlineData("http://app.test:9090/app/login")]
    [InlineData("https://app.test:8080/app/login")]
    [InlineData("http://app.test:8080/application/login")]
    [InlineData("http://app.test:8080/login")]
    public void ALoginUrlOutsideTheContextIsRefused(string loginUrl)
    {
        var error = Assert.Throws<ConfigException>(() => Plan(new ZapProfile(Login: Login with { LoginUrl = loginUrl })));
        Assert.Contains("--zap-login-url", error.Message);
        Assert.Contains("outside the scanned context", error.Message);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("ftp://app.test:8080/app/login")]
    [InlineData("http://u:p@app.test:8080/app/login")]
    [InlineData("http://app.test:8080/app/login#form")]
    public void ALoginUrlThatIsNotAPlainHttpUrlIsRefused(string loginUrl)
    {
        var error = Assert.Throws<ConfigException>(() => Plan(new ZapProfile(Login: Login with { LoginUrl = loginUrl })));
        Assert.Contains("--zap-login-url must be an HTTP or HTTPS URL", error.Message);
    }

    [Fact]
    public void ALoginUrlInsideTheContextIsAccepted()
    {
        Plan(new ZapProfile(Login: Login with { LoginUrl = "http://app.test:8080/app?login=1" })).Validate("zap");
        Plan(new ZapProfile(Login: Login with { LoginUrl = "http://APP.test:8080/app/session" })).Validate("zap");
    }

    [Theory]
    [InlineData("--zap-login-url")]
    [InlineData("--zap-login-body")]
    [InlineData("--zap-login-username")]
    [InlineData("--zap-logged-in-regex")]
    [InlineData("--zap-logged-out-regex")]
    [InlineData("indicator")]
    [InlineData("--zap-login-password")]
    public void AnIncompleteOrInvalidLoginIsRefusedNamingTheFlag(string option)
    {
        var (login, expected) = option switch
        {
            "--zap-login-url" => (Login with { LoginUrl = "" }, "requires --zap-login-url"),
            "--zap-login-body" => (Login with { LoginBody = "" }, "requires --zap-login-body"),
            "--zap-login-username" => (Login with { Username = "" }, "requires --zap-login-username"),
            "--zap-logged-in-regex" => (Login with { LoggedInRegex = "Sign\nout" }, "--zap-logged-in-regex must not contain control characters"),
            "--zap-logged-out-regex" => (Login with { LoggedOutRegex = "" }, "--zap-logged-out-regex must not be empty"),
            "indicator" => (Login with { LoggedInRegex = null, LoggedOutRegex = null }, "--zap-logged-in-regex or --zap-logged-out-regex"),
            _ => (Login with { Password = null! }, "--zap-login-password"),
        };

        var error = Assert.Throws<ConfigException>(() => Plan(new ZapProfile(Login: login)));
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("--zap-logged-in-regex", "Sign\nout")]
    [InlineData("--zap-logged-in-regex", "Sign\tout")]
    [InlineData("--zap-logged-out-regex", "Sign\nin")]
    [InlineData("--zap-logged-out-regex", "Sign\tin")]
    public void AnIndicatorWithAControlCharacterIsRefusedNamingTheFlag(string option, string pattern)
    {
        var login = option == ZapLogin.LoggedInRegexOption ? Login with { LoggedInRegex = pattern } : Login with { LoggedOutRegex = pattern };

        var error = Assert.Throws<ConfigException>(() => Plan(new ZapProfile(Login: login)));
        Assert.Equal($"{option} must not contain control characters", error.Message);
    }

    [Theory]
    [InlineData(@"\Qa.b\E")]
    [InlineData("a++")]
    public void JavaPatternsThatDotNetRejectsAreLeftToZap(string pattern)
    {
        var plan = Plan(new ZapProfile(Login: Login with { LoggedInRegex = pattern, LoggedOutRegex = pattern }));

        plan.Validate("zap");
        var verification = Mapping(Mapping(Context(plan)["authentication"])["verification"]);
        Assert.Equal(pattern, verification["loggedInRegex"]);
        Assert.Equal(pattern, verification["loggedOutRegex"]);
    }

    [Fact]
    public void ALoginGetsItsOwnRulesIdentifiers()
    {
        Assert.Equal(Image + "/wip-passive-auth-v1", Plan(new ZapProfile(Login: Login)).RulesVersion);
        Assert.Equal(Image + "/wip-active-auth-v1", Plan(new ZapProfile(ActiveScan: true, Login: Login)).RulesVersion);
        Assert.Equal(Image + "/wip-passive-v2", Plan().RulesVersion);
        Assert.Equal(Image + "/wip-active-v1", Plan(new ZapProfile(ActiveScan: true)).RulesVersion);
    }

    [Fact]
    public void TheLoginCoverageRecordNamesTheLoginAndDoesNotClaimItSucceeded()
    {
        var coverage = PlanCoverage(Plan(new ZapProfile(Login: Login)));

        Assert.StartsWith(
            "Checks not performed by this profile: active scanning, browser/Ajax crawling, " +
            "form submission other than the configured login request, confirmation that the login succeeded, " +
            "and out-of-band callbacks.\n",
            coverage);
        Assert.Contains("configured to log in by form at http://app.test:8080/app/login; " +
            "a response matching the logged-in regex Sign out counts as logged in; " +
            "a response matching the logged-out regex Sign in counts as logged out; " +
            "a response matching neither is accepted but recorded as unknown. ", coverage);
        Assert.Contains("Authenticated paths count as covered only where ZAP actually reached them as that user; " +
            "pages ZAP could not reach as that user are not evidence. ", coverage);
        Assert.DoesNotContain("verified", coverage);
        Assert.DoesNotContain("test-password", coverage);
        Assert.DoesNotContain("scanner@example.test", coverage);
        Assert.EndsWith("Native ZAP automation output follows unchanged:\n", coverage);

        var active = PlanCoverage(Plan(new ZapProfile(ActiveScan: true, Login: Login with { LoggedInRegex = null })));
        Assert.StartsWith(
            "Checks not performed by this profile: browser/Ajax crawling, " +
            "form submission other than the configured login request, confirmation that the login succeeded, " +
            "and out-of-band callbacks.\n", active);
        Assert.Contains("; a response matching the logged-out regex Sign in counts as logged out; " +
            "a response without the logged-out marker is accepted but recorded as unknown. ", active);
        Assert.DoesNotContain("logged-in regex", active);
        Assert.DoesNotContain("verified", active);

        var loggedInOnly = PlanCoverage(Plan(new ZapProfile(Login: Login with { LoggedOutRegex = null })));
        Assert.Contains("; a response matching the logged-in regex Sign out counts as logged in; " +
            "any other response counts as logged out. ", loggedInOnly);
        Assert.DoesNotContain("unknown", loggedInOnly);
    }

    [Theory]
    [InlineData("12:30")]
    [InlineData("2024-01-02")]
    [InlineData("1e3")]
    [InlineData("yes")]
    [InlineData("~")]
    [InlineData("")]
    [InlineData("it's")]
    public void EveryLoginValueIsWrittenQuotedWhateverWipsResolverThinks(string value)
    {
        // Only the password may be empty; the other values must be non-empty to validate.
        var other = value.Length == 0 ? "x" : value;
        var login = new ZapLogin("http://app.test:8080/app/login", other, other, value,
            LoggedInRegex: other, LoggedOutRegex: other);
        var yaml = PlanYaml(Plan(new ZapProfile(Login: login))).ReplaceLineEndings("\n");
        static string Quoted(string text) => "'" + text.Replace("'", "''") + "'";

        Assert.Contains($"password: {Quoted(value)}\n", yaml);
        foreach (var key in new[] { "loginRequestBody", "username", "loggedInRegex", "loggedOutRegex" })
            Assert.Contains($"{key}: {Quoted(other)}\n", yaml);
        Assert.Contains("loginRequestUrl: 'http://app.test:8080/app/login'\n", yaml);

        var credentials = Mapping(Mapping(Assert.Single(Sequence(Context(Plan(new ZapProfile(Login: login)))["users"])))["credentials"]);
        Assert.Equal(value, credentials["password"]);
    }
}
