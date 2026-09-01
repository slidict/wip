using Wip.Platform;

namespace Wip.Diagnostics;

/// <summary>
/// Writes wip's own progress/status lines to stderr, tinting the "wip:" tag so they read apart
/// at a glance from the raw, unprefixed passthrough output <see cref="Execution.CommandRunner"/>
/// streams from whatever wip shells out to (docker, rsync, wslc, ...).
/// </summary>
/// <remarks>
/// Before this, every "wip: ..." line in <c>CliContext</c> was its own
/// <c>Console.Error.WriteLine</c> call, indistinguishable in color or shape from a child
/// process's raw output interleaved with it (issue #134). This does not touch <c>--debug</c>
/// output: those "wip: [debug] ..." lines are a deliberately raw, uncolored channel already
/// distinguished by their own tag, and stay on <see cref="DebugReporter"/>'s direct writes.
/// </remarks>
public static class Log
{
    private const string TagAccent = "\x1b[36m";
    private const string Reset = "\x1b[0m";

    /// <summary>Writes "wip: message" to stderr, tinting the tag when the terminal supports it.</summary>
    public static void Info(string message) => Console.Error.WriteLine(Format(message, IsColorEnabled()));

    /// <summary>Pure formatting, kept apart from <see cref="IsColorEnabled"/> so the tag's shape
    /// is testable without a real console or environment variables.</summary>
    internal static string Format(string message, bool colorize) =>
        colorize ? $"{TagAccent}wip:{Reset} {message}" : $"wip: {message}";

    /// <summary>
    /// NO_COLOR (https://no-color.org) always wins. Otherwise color only makes sense when
    /// stderr is a real, VT100-capable console: a redirected/piped stderr should stay plain so
    /// logs, CI output, and <c>wip up 2&gt;log</c> don't fill with escape codes, and a console
    /// with no virtual-terminal support would otherwise print the escape bytes themselves
    /// instead of color.
    /// </summary>
    internal static bool IsColorEnabled() => ShouldColorize(
        noColorRequested: Environment.GetEnvironmentVariable("NO_COLOR") is not null,
        isRealConsole: !Console.IsErrorRedirected && AnsiConsole.IsSupported);

    internal static bool ShouldColorize(bool noColorRequested, bool isRealConsole) =>
        !noColorRequested && isRealConsole;
}
