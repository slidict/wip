namespace Wip.Security;

/// <summary>
/// A pinned image reference. Pinning is the point: a scan has to be repeatable, and a
/// floating tag makes the tool under test change without the configuration changing.
/// </summary>
/// <param name="Reference">
/// The immutable image reference as the backend will receive it: <c>name@sha256:...</c>.
/// Tags, including version tags, can be reassigned. Validated by
/// <see cref="ScanPlan.Validate"/>, not here, so a malformed plan fails in one place.
/// </param>
internal sealed record PinnedImage(string Reference);

/// <summary>
/// What an adapter asks for. Deliberately not a backend invocation: there is no field here
/// through which an adapter could reach <c>wslc run</c>'s flags, so no adapter can attach a
/// network, mount a path, or hand the container an environment of its own. The engine builds
/// every backend argument itself from the declared sandbox and this plan.
/// </summary>
/// <param name="Image">
/// The image the tool runs from, pinned. Recorded for the run's metadata and checked for
/// pinning; the container itself is the declared sandbox's, so this never becomes a
/// <c>run</c> argument.
/// </param>
/// <param name="Argv">
/// The command to execute inside the sandbox, passed to the backend directly and never
/// through a shell -- the same rule <see cref="Execution.SandboxBackend"/> states.
/// </param>
/// <param name="ReportPath">
/// Where inside the container the tool writes its report. The engine reads it from there
/// after the run; the adapter does not get to say how it leaves the sandbox.
/// </param>
/// <param name="RulesVersion">
/// The version or digest of the rule, template or add-on set the tool will apply. Required,
/// because "which rules ran" is part of what makes a result meaningful, and because an
/// unpinned set is fetched at scan time -- which an isolated network forbids.
/// </param>
internal sealed record ScanPlan(
    PinnedImage Image,
    IReadOnlyList<string> Argv,
    string ReportPath,
    string RulesVersion,
    string? UnperformedChecksPath = null)
{
    /// <summary>
    /// Rejects a plan that could not produce a repeatable, isolated scan. Called by the
    /// engine before anything runs, so a bad adapter fails before a container is touched
    /// rather than halfway through a scan.
    /// </summary>
    public void Validate(string tool)
    {
        if (string.IsNullOrWhiteSpace(Image.Reference) || Image.Reference.Any(char.IsControl))
            throw new ConfigException($"Scanner {tool}: image reference must be a nonempty string without control characters");
        // A leading dash would be read as an option rather than an image, the same guard the
        // sandbox image declaration carries.
        if (Image.Reference.StartsWith('-'))
            throw new ConfigException($"Scanner {tool}: image reference must not start with '-'");
        if (!IsPinned(Image.Reference))
            throw new ConfigException(
                $"Scanner {tool}: image '{Image.Reference}' is not pinned; use name@sha256:<64 hexadecimal digits>, " +
                "because tags can change the tool under test without the configuration changing");
        if (Argv.Count == 0)
            throw new ConfigException($"Scanner {tool}: argv must not be empty");
        if (Argv.Any(a => a is null || a.Length == 0 || a.Any(char.IsControl)))
            throw new ConfigException($"Scanner {tool}: argv entries must be nonempty strings without control characters");
        if (!ReportPath.StartsWith('/') || ReportPath.Any(char.IsControl) ||
            ReportPath.Split('/').Any(part => part is "." or ".."))
            throw new ConfigException($"Scanner {tool}: report path must be a canonical absolute Linux path");
        if (UnperformedChecksPath is { } diagnostics &&
            (!diagnostics.StartsWith('/') || diagnostics.Any(char.IsControl) ||
             diagnostics.Split('/').Any(part => part is "." or "..")))
            throw new ConfigException($"Scanner {tool}: unperformed-checks path must be a canonical absolute Linux path");
        if (string.IsNullOrWhiteSpace(RulesVersion) || RulesVersion.Any(char.IsControl))
            throw new ConfigException(
                $"Scanner {tool}: a rules version is required; an unpinned rule set is fetched at scan time, which an isolated network forbids");
    }

    /// <summary>Only a complete SHA-256 content digest pins an immutable image.</summary>
    private static bool IsPinned(string reference)
    {
        var parts = reference.Split("@sha256:", StringSplitOptions.None);
        return parts.Length == 2 && parts[0].Length > 0 && !parts[0].Contains('@') &&
               !parts[0].Any(char.IsWhiteSpace) && parts[1].Length == 64 && parts[1].All(Uri.IsHexDigit);
    }
}

/// <summary>
/// A diagnostic tool wip can drive, as wip sees it. Deliberately <c>internal</c>: there is
/// no external plugin API or SDK, so this is a shape for the adapters compiled into wip
/// and not a surface anyone outside it can implement or discover.
/// </summary>
/// <remarks>
/// An adapter contributes a plan and the ability to read its own report. It does not get to
/// run anything, reach the backend, or decide where its output goes: that is the engine's,
/// which is what keeps the isolation policy out of an adapter's hands.
/// </remarks>
internal interface ISecurityScanner
{
    /// <summary>The name <c>--tool</c> selects, lowercase and stable.</summary>
    string Name { get; }

    /// <summary>
    /// The plan for one run against <paramref name="target"/>, which is an address or URL
    /// reachable only inside the declared network.
    /// </summary>
    ScanPlan Plan(ScanTarget target);
}

/// <param name="Address">
/// What the scanner is pointed at, inside the isolated network: a declared network alias or
/// an address of another sandbox on the same network. Never a host or public name -- those
/// do not resolve there, which is the boundary working.
/// </param>
internal sealed record ScanTarget(string Address)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Address) || Address.Any(char.IsControl) || Address.StartsWith('-'))
            throw new ConfigException("Scan target must be a nonempty string without control characters, and must not start with '-'");
    }
}
