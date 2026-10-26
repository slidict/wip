using System.Text.Json;

namespace Wip.Security;

/// <summary>A fixed, unauthenticated HTTP misconfiguration profile.</summary>
internal sealed class NucleiScanner : ISecurityScanner
{
    internal const string ImageReference = "projectdiscovery/nuclei@sha256:66b5f88c9b98ad29c03343dd34e5e85bde0ad10428d72e0171b067efe1ed7811";
    internal const string TemplatesCommit = "2603fc1f23f2ef219ea4834128a163113c6b5168"; // nuclei-templates v10.3.2
    internal const string RulesPath = "/opt/wip-nuclei-templates";
    private const string Profile = "http-misconfiguration-v1";
    private const string ReportPath = "/tmp/wip-nuclei-report.jsonl";
    private const string UnperformedPath = "/tmp/wip-nuclei-unperformed.txt";
    private const string NativePath = "/tmp/wip-nuclei-native.jsonl";
    private const string ErrorsPath = "/tmp/wip-nuclei-errors.jsonl";
    internal const string Completion = "{\"wip-nuclei-completed\":\"http-misconfiguration-v1\"}";
    private const string Coverage = "Checks not performed: authenticated scanning, crawling, out-of-band callbacks, non-HTTP protocols, workflows, headless/browser and code execution; templates outside http/misconfiguration, unsigned or invalid templates, and dos/fuzz/bruteforce tags are excluded. Redirects are disabled. Requests are bounded to 10/second, concurrency 2, timeout 5 seconds, no retries; the engine bounds total runtime. Native logs and request errors below record runtime skips/failures; no findings does not prove omitted checks passed.";

    // Alpine has sh, not Python. Only this fixed program is shell syntax: paths, coverage
    // and all nuclei arguments (including the target) are positional data. Never eval them.
    // The marker is composed from Completion rather than repeated: a renamed profile would
    // otherwise leave the wrapper emitting one ConvertReport refuses, turning every finished
    // scan into report-unparseable, and the test that would catch it is Linux-only. nuclei
    // writes findings to stdout as well as to -o, so stdout is dropped: the duplicate would
    // push the sidecar past the engine 1 MiB cap on a noisy target and lose the coverage
    // record for a scan that succeeded. The log stays, on stderr.
    // A completion record makes a successful zero-match JSONL report nonempty for the shared
    // engine. It is appended only after exit 0 and an actual native report file; missing
    // templates/output must not become a fabricated successful empty scan.
    private const string Wrapper = """set -u; report=$1; coverage=$2; native=$3; errors=$4; notice=$5; shift 5; rm -f -- "$report" "$native" "$errors" || exit $?; printf '%s\n' "$notice" > "$coverage" || exit $?; "$@" >/dev/null 2>>"$coverage"; code=$?; if [ -f "$errors" ]; then cat "$errors" >> "$coverage" || exit $?; fi; if [ -f "$native" ]; then if [ "$code" -eq 0 ]; then printf '\n%s\n' '""" + Completion + """' >> "$native" || exit $?; fi; mv -- "$native" "$report" || exit $?; fi; exit $code""";

    public string Name => "nuclei";

    public ScanPlan Plan(ScanTarget target)
    {
        target.Validate();
        if (!Uri.TryCreate(target.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsoluteUri.Contains(','))
            throw new ConfigException("Nuclei requires one unauthenticated HTTP or HTTPS URL without a fragment or comma");
        // Verified against the pinned v3.9.0 binary's -help. -duc disables fresh installs
        // as well as update checks; -ni excludes interactsh templates. No remote templates,
        // cloud upload, code, browser or workflow execution is enabled. The declared internal
        // network is still the enforcement boundary, not these profile switches.
        return new(new PinnedImage(ImageReference),
            ["sh", "-c", Wrapper, "wip-nuclei", ReportPath, UnperformedPath, NativePath, ErrorsPath, Coverage,
             "nuclei", "-u", uri.AbsoluteUri, "-t", RulesPath + "/http/misconfiguration",
             "-duc", "-ni", "-no-stdin", "-nh", "-dr", "-dut", "-pt", "http",
             "-etags", "dos,fuzz,bruteforce", "-rl", "10", "-c", "2", "-timeout", "5", "-retries", "0",
             "-nc", "-jsonl", "-omit-raw", "-omit-template", "-o", NativePath, "-elog", ErrorsPath],
            ReportPath, ImageReference + "/" + Profile, UnperformedPath,
            new PinnedRules("https://github.com/projectdiscovery/nuclei-templates.git", TemplatesCommit, RulesPath));
    }

    public IReadOnlyList<ScanFinding> ConvertReport(string report)
    {
        var findings = new List<ScanFinding>();
        using var reader = new StringReader(report);
        var completed = false;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (completed) throw new FormatException("Nuclei completion must be the final JSONL record");
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Nuclei records must be objects");
            if (item.TryGetProperty("wip-nuclei-completed", out _))
            {
                if (item.EnumerateObject().Count() != 1 || Text(item, "wip-nuclei-completed") != Profile)
                    throw new FormatException("Invalid Nuclei completion record");
                completed = true;
                continue;
            }
            var id = Text(item, "template-id");
            if (string.IsNullOrWhiteSpace(id) || !item.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                throw new FormatException("Nuclei findings require a template-id and info object");
            if (item.TryGetProperty("matcher-status", out var status) && status.ValueKind != JsonValueKind.True)
                throw new FormatException("Nuclei finding must represent a successful match");
            var matched = Text(item, "matched-at");
            var path = Uri.TryCreate(matched, UriKind.Absolute, out var location) && location.Scheme is "http" or "https"
                ? location.AbsolutePath : null;
            string? evidence = null;
            if (item.TryGetProperty("extracted-results", out var extracted))
            {
                if (extracted.ValueKind != JsonValueKind.Array || extracted.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new FormatException("Nuclei extracted-results must be an array of strings");
                evidence = string.Join("\n", extracted.EnumerateArray().Select(value => value.GetString()));
            }
            var severity = Text(info, "severity");
            findings.Add(new ScanFinding(id, Text(info, "name"), severity == "info" ? "informational" : severity,
                // An empty extracted-results array joins to "", which is absence, not evidence.
                null, new ScanLocation(path, null, null),
                evidence is { Length: > 0 } ? evidence : Text(item, "matcher-name")));
        }
        // The marker is appended only after exit 0 and a real native report, but the wrapper
        // still publishes what nuclei flushed before a fatal error. Requiring it here is what
        // makes a truncated finding set unparseable -- and therefore incomplete -- instead of
        // a "completed" run whose missing checks are invisible in the stored document.
        if (!completed) throw new FormatException("Nuclei report has no completion record; the scan did not finish");
        return findings.AsReadOnly();
    }

    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException($"Nuclei {name} must be a string");
        return value.GetString();
    }
}
