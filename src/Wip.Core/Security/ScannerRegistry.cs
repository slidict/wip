namespace Wip.Security;

/// <summary>
/// The scanners compiled into wip, as a static table.
/// </summary>
/// <remarks>
/// <para>
/// A static table rules out dynamic loading and extension by arbitrary
/// command or image, so there is deliberately no reflection, no assembly scanning and no
/// configuration key that can add an entry: a tool arrives by a reviewed pull request to wip
/// that adds a line here, which is what makes its code, dependencies, image, permissions,
/// traffic and result handling reviewable at all.
/// </para>
/// </remarks>
internal static class ScannerRegistry
{
    private static readonly ISecurityScanner[] Registered = [new ZapScanner()];

    /// <summary>Registered names in declaration order, for a message that lists the choices.</summary>
    internal static IReadOnlyList<string> Names => Registered.Select(s => s.Name).ToArray();

    /// <summary>
    /// The one scanner <paramref name="tool"/> selects. Throws when it names none, which is
    /// unsupported tools fail closed: there is no fallback entry or nearest match,
    /// because guessing which tool to run is not a safe recovery.
    /// </summary>
    internal static ISecurityScanner Resolve(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            throw new ConfigException($"A tool must be selected with --tool; {Available()}");
        // Ordinal, not culture-aware: the name is an identifier, and a culture-sensitive
        // comparison can make two different identifiers equal.
        var matches = Registered.Where(s => string.Equals(s.Name, tool, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
            throw new ConfigException($"Unknown tool: {tool}. {Available()}");
        // A duplicate name means two entries answer to one selector, so which one ran would
        // depend on declaration order. That is a build-time mistake in this file.
        if (matches.Length != 1)
            throw new WipException($"Tool {tool} is registered more than once; the registry is ambiguous");
        return matches[0];
    }

    private static string Available() =>
        Registered.Length == 0
            ? "no scanners are registered in this build"
            : $"registered tools: {string.Join(", ", Names)}";
}
