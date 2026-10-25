using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wip.Yaml;

namespace Wip.Security;

/// <summary>A bounded, unauthenticated traditional-spider and passive-scan profile.</summary>
internal sealed class ZapScanner : ISecurityScanner
{
    internal const string ImageReference = "ghcr.io/zaproxy/zaproxy@sha256:5ccfff9a411743ab2615136a2edd7416bec9ce41572fa94e935df38c14adb2b6";
    private const string PlanPath = "/tmp/wip-zap-plan.yaml";
    private const string ReportPath = "/tmp/wip-zap-report.json";
    private const string UnperformedPath = "/tmp/wip-zap-unperformed.txt";

    // This fixed Python program receives a JSON-quoted YAML document as data, not shell
    // syntax. Quoting keeps every argv element free of control characters. The writable
    // /tmp paths work with the image's non-root zap user, without a host mount.
    private const string Wrapper = """import json,pathlib,subprocess,sys; pathlib.Path(sys.argv[3]).unlink(missing_ok=True); pathlib.Path(sys.argv[4]).unlink(missing_ok=True); pathlib.Path(sys.argv[2]).write_text(json.loads(sys.argv[1]),encoding='utf-8'); raw=open(sys.argv[4],'w',encoding='utf-8'); raw.write('Checks not performed by this profile: active scanning, authenticated paths, browser/Ajax crawling, form submission, and out-of-band callbacks.\nCrawl coverage is bounded to one minute, depth two, ten children per node, and the selected URL subtree; passive scanning waits at most one minute. Findings do not prove these omitted checks passed.\nNative ZAP automation output follows unchanged:\n'); raw.flush(); code=subprocess.call(['/zap/zap.sh','-cmd','-silent','-addonuninstall','oast','-autorun',sys.argv[2]],stdout=raw,stderr=subprocess.STDOUT); raw.close(); sys.exit(code)""";

    public string Name => "zap";

    public ScanPlan Plan(ScanTarget target)
    {
        target.Validate();
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
        // prefix to a sibling, port, scheme, or host. No browser or active-scan job runs.
        // Spider.start disables automatic redirects and filters seeds before dispatch;
        // DefaultFetchFilter rejects URLs outside the context (including redirect targets):
        // The traditional spider's context restriction is documented here:
        // https://www.zaproxy.org/docs/api/#spider
        var environment = Map(
            ("contexts", List(Map(("name", "wip"), ("urls", List(url)),
                ("includePaths", List("^" + scope + "$")), ("excludePaths", List("^(?!" + scope + "$).*"))))),
            ("parameters", Map(("failOnError", true), ("failOnWarning", false), ("continueOnFailure", false), ("progressToStdout", true))));

        // Documented spider limits and form switches keep this profile bounded and passive:
        // https://www.zaproxy.org/docs/desktop/addons/spider/automation/
        // Report parameters select the unmodified traditional JSON artifact under /tmp:
        // https://www.zaproxy.org/docs/desktop/addons/report-generation/automation/
        // https://www.zaproxy.org/docs/desktop/addons/passive-scanner/job-pscanconf/
        // https://www.zaproxy.org/docs/desktop/addons/passive-scanner/job-pscanwait/
        var jobs = List(
            Map(("type", "passiveScan-config"), ("parameters", Map(("scanOnlyInScope", true)))),
            Map(("type", "spider"), ("parameters", Map(("context", "wip"), ("url", url),
                ("maxDuration", 1), ("maxDepth", 2), ("maxChildren", 10), ("threadCount", 2),
                ("postForm", false), ("processForm", false), ("parseRobotsTxt", false), ("parseSitemapXml", false)))),
            Map(("type", "passiveScan-wait"), ("parameters", Map(("maxDuration", 1)))),
            Map(("type", "report"), ("parameters", Map(("template", "traditional-json"),
                ("reportDir", "/tmp"), ("reportFile", "wip-zap-report"), ("displayReport", false)))));
        var yaml = YamlWriter.Dump(Map(("env", environment), ("jobs", jobs)));

        // ZAP's core command-line reference documents -cmd, -silent (no unsolicited calls),
        // and -addonuninstall; the AF guide explicitly supports uninstall with -autorun:
        // https://www.zaproxy.org/docs/desktop/cmdline/
        // https://www.zaproxy.org/docs/automate/automation-framework/
        // Removing the oast add-on disables its external callback services altogether.
        // No add-on install/update is requested, so rules remain those in the image digest.
        return new(new PinnedImage(ImageReference),
            ["python3", "-c", Wrapper, Quote(yaml), PlanPath, ReportPath, UnperformedPath],
            ReportPath, ImageReference + "/wip-passive-v1", UnperformedPath);
    }

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
                        Location = new ScanLocation(ReportPathOnly(ReportText(instance, "uri")),
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

    private static string? ReportPathOnly(string? address)
    {
        if (address is null) return null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("ZAP instance URI must be an absolute HTTP or HTTPS URL");
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
