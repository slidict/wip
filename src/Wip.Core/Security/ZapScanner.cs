using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wip.Yaml;

namespace Wip.Security;

/// <summary>
/// A bounded, unauthenticated traditional-spider and passive-scan profile, with an opt-in
/// bounded active scan. The bounds come from <see cref="ZapProfile"/>.
/// </summary>
internal sealed class ZapScanner(ZapProfile? profile = null) : ISecurityScanner
{
    internal const string ImageReference = "ghcr.io/zaproxy/zaproxy@sha256:5ccfff9a411743ab2615136a2edd7416bec9ce41572fa94e935df38c14adb2b6";
    private const string PlanPath = "/tmp/wip-zap-plan.yaml";
    private const string ReportPath = "/tmp/wip-zap-report.json";
    private const string UnperformedPath = "/tmp/wip-zap-unperformed.txt";

    // This fixed Python program receives a JSON-quoted YAML document and a JSON-quoted
    // coverage header as data, not shell syntax. Quoting keeps every argv element free of
    // control characters. The writable /tmp paths work with the image's non-root zap user,
    // without a host mount.
    private const string Wrapper = """import json,pathlib,subprocess,sys; pathlib.Path(sys.argv[3]).unlink(missing_ok=True); pathlib.Path(sys.argv[4]).unlink(missing_ok=True); pathlib.Path(sys.argv[2]).write_text(json.loads(sys.argv[1]),encoding='utf-8'); raw=open(sys.argv[4],'w',encoding='utf-8'); raw.write(json.loads(sys.argv[5])); raw.flush(); code=subprocess.call(['/zap/zap.sh','-cmd','-silent','-addonuninstall','oast','-autorun',sys.argv[2]],stdout=raw,stderr=subprocess.STDOUT); raw.close(); sys.exit(code)""";

    private readonly ZapProfile profile = profile ?? ZapProfile.Default;

    public string Name => "zap";

    /// <summary>The profile this instance plans with.</summary>
    internal ZapProfile Profile => profile;

    /// <summary>The same scanner with caller-selected bounds; validated when the plan is built.</summary>
    internal ZapScanner WithProfile(ZapProfile selected) => new(selected);

    public ScanPlan Plan(ScanTarget target)
    {
        target.Validate();
        profile.Validate();
        if (!Uri.TryCreate(target.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ConfigException("ZAP requires an unauthenticated HTTP or HTTPS target URL");
        // A fragment is never sent to a server, and keeping one would make the scan silently
        // examine nothing: `AbsoluteUri` retains it while the scope below is built from
        // `GetLeftPart(UriPartial.Path)`, which drops it. Measured on .NET 10 --
        // `http://app:8080/app#main` does not match its own `^scope$` include and does match
        // the complement exclude, so the seed is excluded from its own context, the spider
        // gets no seed, and ZAP still exits 0 with an empty report. Refused rather than
        // trimmed: a target that is not the thing scanned should not be guessed at.
        if (uri.Fragment.Length != 0)
            throw new ConfigException("ZAP target must not carry a URL fragment; a fragment is never sent to the server");

        var url = uri.AbsoluteUri;
        var prefix = uri.GetLeftPart(UriPartial.Path);
        var scope = Regex.Escape(prefix) + (prefix.EndsWith('/') ? ".*" : "(?:/.*|\\?.*)?");

        // Official AF environment documentation specifies urls/includePaths/excludePaths:
        // https://www.zaproxy.org/docs/desktop/addons/automation-framework/environment/
        // The complement exclusion prevents default URL inclusion from broadening a path
        // prefix to a sibling, port, scheme, or host. No browser job runs, and the active scan
        // runs only when the profile asks for it -- inside this same context.
        // Spider.start disables automatic redirects and filters seeds before dispatch;
        // DefaultFetchFilter rejects URLs outside the context (including redirect targets):
        // https://github.com/zaproxy/zap-extensions/blob/main/addOns/spider/src/main/java/org/zaproxy/addon/spider/Spider.java
        // https://github.com/zaproxy/zap-extensions/blob/main/addOns/spider/src/main/java/org/zaproxy/addon/spider/filters/DefaultFetchFilter.java
        var environment = Map(
            ("contexts", List(Map(("name", "wip"), ("urls", List(url)),
                ("includePaths", List("^" + scope + "$")), ("excludePaths", List("^(?!" + scope + "$).*"))))),
            ("parameters", Map(("failOnError", true), ("failOnWarning", false), ("continueOnFailure", false), ("progressToStdout", true))));

        // Documented spider limits and form switches keep this profile bounded:
        // https://www.zaproxy.org/docs/desktop/addons/spider/automation/
        // The opt-in active scan is bounded by maxScanDurationInMins and confined to the same
        // context, so it attacks only requests the in-scope crawl found:
        // https://www.zaproxy.org/docs/desktop/addons/active-scanner/automation/
        // Report parameters select the unmodified traditional JSON artifact under /tmp:
        // https://www.zaproxy.org/docs/desktop/addons/report-generation/automation/
        // https://www.zaproxy.org/docs/desktop/addons/passive-scanner/job-pscanconf/
        // https://www.zaproxy.org/docs/desktop/addons/passive-scanner/job-pscanwait/
        var jobs = List(
            Map(("type", "passiveScan-config"), ("parameters", Map(("scanOnlyInScope", true)))),
            Map(("type", "spider"), ("parameters", Map(("context", "wip"), ("url", url),
                ("maxDuration", profile.SpiderMinutes), ("maxDepth", profile.SpiderDepth),
                ("maxChildren", profile.SpiderChildren), ("threadCount", 2),
                ("postForm", false), ("processForm", false), ("parseRobotsTxt", false), ("parseSitemapXml", false)))));
        // Before the passive wait, so the wait also covers the responses the attacks produced.
        if (profile.ActiveScan)
            jobs.Add(Map(("type", "activeScan"), ("parameters", Map(("context", "wip"),
                ("maxScanDurationInMins", profile.ActiveScanMinutes), ("threadPerHost", 2)))));
        jobs.Add(Map(("type", "passiveScan-wait"), ("parameters", Map(("maxDuration", profile.PassiveWaitMinutes)))));
        jobs.Add(Map(("type", "report"), ("parameters", Map(("template", "traditional-json"),
            ("reportDir", "/tmp"), ("reportFile", "wip-zap-report"), ("displayReport", false)))));
        var yaml = YamlWriter.Dump(Map(("env", environment), ("jobs", jobs)));

        // ZAP's core command-line reference documents -cmd, -silent (no unsolicited calls),
        // and -addonuninstall; the AF guide explicitly supports uninstall with -autorun:
        // https://www.zaproxy.org/docs/desktop/cmdline/
        // https://www.zaproxy.org/docs/automate/automation-framework/
        // Removing the oast add-on disables its external callback services altogether.
        // No add-on install/update is requested, so rules remain those in the image digest.
        return new(new PinnedImage(ImageReference),
            ["python3", "-c", Wrapper, Quote(yaml), PlanPath, ReportPath, UnperformedPath, Quote(Coverage(profile))],
            ReportPath, ImageReference + "/" + profile.Identifier, UnperformedPath);
    }

    /// <summary>
    /// The header of the raw coverage record, stating the bounds this plan actually uses. The
    /// native Automation Framework output is appended below it unchanged.
    /// </summary>
    internal static string Coverage(ZapProfile profile)
    {
        var omitted = profile.ActiveScan
            ? "authenticated paths, browser/Ajax crawling, form submission, and out-of-band callbacks"
            : "active scanning, authenticated paths, browser/Ajax crawling, form submission, and out-of-band callbacks";
        var active = profile.ActiveScan
            ? $"; active scanning of the crawled in-scope requests stops after at most {Minutes(profile.ActiveScanMinutes)}, " +
              "so rules can be left unfinished"
            : "";
        return $"Checks not performed by this profile: {omitted}.\n" +
               $"Crawl coverage is bounded to {Minutes(profile.SpiderMinutes)}, depth {profile.SpiderDepth}, " +
               $"{profile.SpiderChildren} children per node, and the selected URL subtree{active}; " +
               $"passive scanning waits at most {Minutes(profile.PassiveWaitMinutes)}. " +
               "Findings do not prove these omitted checks passed.\n" +
               "Native ZAP automation output follows unchanged:\n";
    }

    private static string Minutes(int value) => value == 1 ? "1 minute" : $"{value} minutes";

    /// <summary>Reads the native traditional-json report without touching execution resources.</summary>
    public IReadOnlyList<ScanFinding> ConvertReport(string report)
    {
        // The traditional-json template emits pluginid/alertRef, name/alert, riskcode,
        // confidence and instances containing uri/method/param/evidence. Numeric codes
        // follow ZAP's alert risk and confidence levels; omitted values remain null.
        // https://www.zaproxy.org/docs/desktop/addons/report-generation/
        using var document = JsonDocument.Parse(report);
        var findings = new List<ScanFinding>();
        foreach (var site in ReportArray(document.RootElement, "site").EnumerateArray())
        {
            foreach (var alert in ReportArray(site, "alerts").EnumerateArray())
            {
                if (alert.ValueKind != JsonValueKind.Object)
                    throw new FormatException("ZAP alerts must be objects");
                var risk = ReportText(alert, "riskcode");
                var confidence = ReportText(alert, "confidence");
                var finding = new ScanFinding(
                    ReportText(alert, "pluginid") ?? ReportText(alert, "alertRef"),
                    ReportText(alert, "name") ?? ReportText(alert, "alert"),
                    risk switch { "0" => "informational", "1" => "low", "2" => "medium", "3" => "high", _ => risk },
                    confidence switch { "0" => "false-positive", "1" => "low", "2" => "medium", "3" => "high", "4" => "confirmed", _ => confidence },
                    new ScanLocation(null, null, null), null);
                if (!alert.TryGetProperty("instances", out var instances) || instances.ValueKind == JsonValueKind.Null)
                {
                    findings.Add(finding);
                    continue;
                }
                if (instances.ValueKind != JsonValueKind.Array)
                    throw new FormatException("ZAP instances must be an array");
                if (instances.GetArrayLength() == 0) findings.Add(finding);
                foreach (var instance in instances.EnumerateArray())
                {
                    if (instance.ValueKind != JsonValueKind.Object)
                        throw new FormatException("ZAP instances must be objects");
                    findings.Add(finding with
                    {
                        Location = new ScanLocation(ReportPathOnly(instance),
                            ReportText(instance, "method")?.ToUpperInvariant(), ReportText(instance, "param")),
                        Evidence = ReportText(instance, "evidence")
                    });
                }
            }
        }
        return findings.AsReadOnly();
    }

    private static JsonElement ReportArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"ZAP report requires a {name} array");
        return value;
    }

    private static string? ReportText(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new FormatException($"ZAP {name} must be text or a number")
        };
    }

    private static string? ReportPathOnly(JsonElement instance)
    {
        // An unusable URI string does not invalidate the rest of this instance or report, so a
        // blank or unparseable one -- and an absent or null field -- omits only this path.
        // Any other present shape is refused, including a number, which the sibling readers
        // accept. uri is deliberately stricter than they are: it carries the location, so a
        // template that changed its shape would silently cost every finding its location while
        // the run still reported completed with a full list. That is the reading this check
        // exists to prevent, and a number loses the location exactly as a nested object does.
        if (!instance.TryGetProperty("uri", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException("ZAP uri must be text; a non-string uri is a changed report shape, not an unusable value");
        if (!Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        // Query strings may carry credentials and are not part of a stable endpoint key.
        return uri.AbsolutePath;
    }

    private static string Quote(string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) writer.WriteStringValue(text);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static List<object?> List(params object?[] values) => [.. values];

    private static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] values)
    {
        var result = RubyValue.NewMapping();
        foreach (var (key, value) in values) result[key] = value;
        return result;
    }
}
