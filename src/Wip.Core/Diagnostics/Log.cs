using Wip.Platform;

namespace Wip.Diagnostics;

/// <summary>
/// Writes wip's own progress/status/warning/error lines to stderr, tinting the "wip:" tag so
/// they read apart at a glance from the raw, unprefixed passthrough output
/// <see cref="Execution.CommandRunner"/> streams from whatever wip shells out to (docker, rsync,
/// wslc, ...), and tinting a "warning:"/"error:" label on top for the two lines actually mean.
/// </summary>
/// <remarks>
/// Before this, every "wip: ..." line in <c>CliContext</c> was its own
/// <c>Console.Error.WriteLine</c> call, indistinguishable in color or shape from a child
/// process's raw output interleaved with it, and from each other regardless of whether the
/// line was routine progress or something the user needed to notice (issue #134). Severity
/// here covers only wip's own messages: a child process's raw output (a docker warning, an
/// rsync error) is not parsed or reclassified, since guessing at arbitrary third-party output
/// shapes is exactly the fragility the issue's design discussion ruled out. This also does not
/// touch <c>--debug</c> output: those "wip: [debug] ..." lines are a deliberately raw,
/// uncolored channel already distinguished by their own tag, and stay on
/// <see cref="DebugReporter"/>'s direct writes.
/// </remarks>
public static class Log
{
    private const ushort EnglishPrimaryLanguageId = 0x09;
    private const ushort JapanesePrimaryLanguageId = 0x11;
    private const string TagAccent = "\x1b[36m";
    private const string WarnAccent = "\x1b[33m";
    private const string ErrorAccent = "\x1b[31m";
    private const string Reset = "\x1b[0m";

    /// <summary>Writes "wip: message" to stderr, tinting the tag when the terminal supports it.</summary>
    public static void Info(string message) => Console.Error.WriteLine(Format(message, IsColorEnabled()));

    /// <summary>Writes "wip: warning: message" -- something the user should notice but that
    /// does not stop wip from continuing.</summary>
    public static void Warn(string message) => Console.Error.WriteLine(
        FormatWarn(message, IsColorEnabled(), DisplayLanguage.CurrentPrimaryLanguageId()));

    /// <summary>Writes "wip: error: message" -- wip is about to stop, or a command it ran
    /// already failed.</summary>
    public static void Error(string message) => Console.Error.WriteLine(
        FormatError(message, IsColorEnabled(), DisplayLanguage.CurrentPrimaryLanguageId()));

    /// <summary>Pure formatting, kept apart from <see cref="IsColorEnabled"/> so the tag's shape
    /// is testable without a real console or environment variables.</summary>
    internal static string Format(string message, bool colorize) => FormatTagged(null, message, colorize);

    internal static string FormatWarn(string message, bool colorize) =>
        FormatWarn(message, colorize, EnglishPrimaryLanguageId);

    internal static string FormatWarn(string message, bool colorize, ushort languageId) =>
        FormatTagged((SeverityLabel(languageId, isError: false), WarnAccent), message, colorize);

    internal static string FormatError(string message, bool colorize) =>
        FormatError(message, colorize, EnglishPrimaryLanguageId);

    internal static string FormatError(string message, bool colorize, ushort languageId) =>
        FormatTagged((SeverityLabel(languageId, isError: true), ErrorAccent), message, colorize);

    /// <summary>
    /// Gets the translated severity label. English is deliberately the default branch so an
    /// OS language for which wip has no translation never produces a missing or blank label.
    /// </summary>
    private static string SeverityLabel(ushort languageId, bool isError) => languageId switch
    {
        JapanesePrimaryLanguageId => isError ? "エラー" : "警告",
        _ => isError ? "error" : "warning",
    };

    private static string FormatTagged((string Label, string Accent)? severity, string message, bool colorize)
    {
        var tag = colorize ? $"{TagAccent}wip:{Reset}" : "wip:";
        if (severity is not { } level)
        {
            return $"{tag} {message}";
        }

        var label = colorize ? $"{level.Accent}{level.Label}:{Reset}" : $"{level.Label}:";
        return $"{tag} {label} {message}";
    }

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
