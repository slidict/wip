namespace Wip.Cli;

/// <summary>
/// Unwinds to <see cref="Program"/> with a specific exit status.
/// </summary>
/// <remarks>
/// The Ruby CLI called <c>exit</c> from deep inside a command body when a wslc invocation
/// failed. Doing the same here would skip every <c>finally</c> on the way out — progress
/// timers, staged-context cleanup, the debug log's file handle — so the status is thrown and
/// caught at the top instead.
/// </remarks>
public sealed class ExitException : Exception
{
    public ExitException(int code) => Code = code;

    public int Code { get; }
}
