namespace Wip.Security;

/// <summary>A scanner finding, with absent source values preserved as null.</summary>
internal sealed record ScanFinding(
    string? RuleId,
    string? Name,
    string? Severity,
    string? Confidence,
    ScanLocation Location,
    string? Evidence);

/// <summary>A stable location without a deployment authority, query string, or fragment.</summary>
internal sealed record ScanLocation(string? Path, string? Method, string? Parameter);
