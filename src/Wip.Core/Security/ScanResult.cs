using System.Globalization;
using System.Text.Json;

namespace Wip.Security;

internal sealed record ScanRunMetadata(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string Tool,
    string Image,
    string RulesVersion,
    string Sandbox,
    string Network,
    bool NetworkVerifiedInternal,
    string Target,
    string? Commit = null);

internal sealed record ScanResult(
    ScanRunMetadata Run,
    string Status,
    IReadOnlyList<ScanFinding>? Findings = null,
    string? IncompleteReason = null,
    string? UnperformedChecks = null,
    string? CoverageMissingReason = null,
    int ScannerExitCode = 0);

internal static class ScanResultWriter
{
    internal const int Version = 1;
    private static readonly string[] IncompleteReasons =
        ["timeout", "report-collection-failed", "report-empty", "report-unparseable", "unperformed-checks-lost"];

    internal static void Validate(ScanResult result)
    {
        if (!Guid.TryParseExact(result.Run.RunId, "N", out _))
            throw new ConfigException("Scan result run id must be a GUID in N format");
        if (result.Status == "completed")
        {
            if (result.Findings is null || result.IncompleteReason is not null)
                throw new ConfigException("A completed scan result requires findings and no incomplete reason");
        }
        else if (result.Status == "incomplete")
        {
            if (result.Findings is not null || result.IncompleteReason is null || !IncompleteReasons.Contains(result.IncompleteReason, StringComparer.Ordinal))
                throw new ConfigException("An incomplete scan result requires a recognized reason and no findings list");
        }
        else throw new ConfigException("Scan result status must be completed or incomplete");
        if (result.UnperformedChecks is null && string.IsNullOrWhiteSpace(result.CoverageMissingReason))
            throw new ConfigException("Scan coverage requires collected text or a missing reason");
    }

    internal static void Write(Stream output, ScanResult result)
    {
        Validate(result);
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", Version);
        writer.WriteStartObject("run");
        var run = result.Run;
        WriteString(writer, "runId", run.RunId);
        writer.WriteString("startedAt", run.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("endedAt", run.EndedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        WriteString(writer, "tool", run.Tool);
        WriteString(writer, "image", run.Image);
        WriteString(writer, "rulesVersion", run.RulesVersion);
        WriteString(writer, "sandbox", run.Sandbox);
        WriteString(writer, "network", run.Network);
        writer.WriteBoolean("networkVerifiedInternal", run.NetworkVerifiedInternal);
        WriteString(writer, "target", run.Target);
        WriteString(writer, "commit", run.Commit);
        writer.WriteEndObject();
        writer.WriteString("status", result.Status);
        writer.WriteNumber("scannerExitCode", result.ScannerExitCode);
        if (result.Status == "completed")
        {
            writer.WriteStartArray("findings");
            foreach (var finding in result.Findings!)
            {
                writer.WriteStartObject();
                WriteString(writer, "ruleId", finding.RuleId);
                WriteString(writer, "name", finding.Name);
                WriteString(writer, "severity", finding.Severity);
                WriteString(writer, "confidence", finding.Confidence);
                writer.WriteStartObject("location");
                WriteString(writer, "path", finding.Location.Path);
                WriteString(writer, "method", finding.Location.Method);
                WriteString(writer, "parameter", finding.Location.Parameter);
                writer.WriteEndObject();
                if (finding.Evidence is { } evidence)
                    writer.WriteString("evidence", ScanResultRedactor.RedactEvidence(evidence, finding.Location.Parameter));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        else writer.WriteString("reason", result.IncompleteReason);
        writer.WriteStartObject("coverage");
        // Coverage is raw diagnostic text, like its separate artifact. Findings and run
        // metadata are redacted; preserving coverage verbatim is part of this contract.
        if (result.UnperformedChecks is { } rawCoverage) writer.WriteString("unperformedChecks", rawCoverage);
        else WriteString(writer, "missingReason", result.CoverageMissingReason);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, ScanResultRedactor.Redact(value));
    }
}
