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
        // Caller-supplied run metadata identifies the requested scan, not crawled URLs.
        // Preserve its path segments while still redacting credentials and query secrets.
        var run = result.Run;
        WriteMetadataString(writer, "runId", run.RunId);
        writer.WriteString("startedAt", run.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("endedAt", run.EndedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        WriteMetadataString(writer, "tool", run.Tool);
        WriteMetadataString(writer, "image", run.Image);
        WriteMetadataString(writer, "rulesVersion", run.RulesVersion);
        WriteMetadataString(writer, "sandbox", run.Sandbox);
        WriteMetadataString(writer, "network", run.Network);
        writer.WriteBoolean("networkVerifiedInternal", run.NetworkVerifiedInternal);
        WriteMetadataString(writer, "target", run.Target);
        WriteMetadataString(writer, "commit", run.Commit);
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
        // The embedded copy shares the document's redaction boundary. The separate
        // unperformed-checks.txt artifact preserves the exact captured diagnostic text.
        if (result.UnperformedChecks is { } rawCoverage) WriteString(writer, "unperformedChecks", rawCoverage);
        else WriteString(writer, "missingReason", result.CoverageMissingReason);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteMetadataString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, ScanResultRedactor.RedactMetadata(value));
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, ScanResultRedactor.Redact(value));
    }
}
