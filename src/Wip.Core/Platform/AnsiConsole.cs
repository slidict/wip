using System.Runtime.InteropServices;

namespace Wip.Platform;

/// <summary>
/// Whether the process's stderr handle will render ANSI/VT100 escape sequences, enabling that
/// support on Windows first if it isn't already on.
/// </summary>
/// <remarks>
/// Unlike a Unix terminal, a Windows console does not render <c>\x1b[...</c> sequences until
/// <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> is set on its handle, and that is not guaranteed
/// to already be on — an older conhost window, for one. Writing color codes without this
/// first would print the literal escape bytes instead of color, which is worse than no color
/// at all. wip only ships for Windows, but the solution also builds and runs on a Linux dev
/// box (see the RID comment in Wip.Cli.csproj) and CI's own Ubuntu leg — there, a real
/// terminal already renders ANSI without any enabling step, so gating this on
/// <c>OperatingSystem.IsWindows()</c> the same way as the syscalls below would needlessly
/// disable color on every non-Windows terminal. The check runs once and is cached: the mode
/// cannot meaningfully change mid-process, and repeating two syscalls on every log line would
/// be wasted work.
/// </remarks>
internal static partial class AnsiConsole
{
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    private static readonly Lazy<bool> Supported = new(TryEnable);

    internal static bool IsSupported => Supported.Value;

    private static bool TryEnable()
    {
        if (!OperatingSystem.IsWindows())
        {
            // No handle to enable anything on outside Windows -- and none needed, since a
            // real Unix terminal already understands ANSI on its own.
            return true;
        }

        var handle = GetStdHandle(StdErrorHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        // GetConsoleMode fails outright when the handle is not a console at all (redirected to
        // a file or pipe). Log already checks Console.IsErrorRedirected before trusting this,
        // but there is no reason to assume that here too.
        if (!GetConsoleMode(handle, out var mode))
        {
            return false;
        }

        return (mode & EnableVirtualTerminalProcessing) != 0 ||
               SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr handle, uint mode);
}
