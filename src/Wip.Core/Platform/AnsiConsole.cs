using System.Runtime.InteropServices;

namespace Wip.Platform;

/// <summary>
/// Enables ANSI/VT100 escape-sequence processing on the process's stderr handle, and reports
/// whether that succeeded.
/// </summary>
/// <remarks>
/// Unlike a Unix terminal, a Windows console does not render <c>\x1b[...</c> sequences until
/// <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> is set on its handle, and that is not guaranteed
/// to already be on — an older conhost window, for one. Writing color codes without this
/// first would print the literal escape bytes instead of color, which is worse than no color
/// at all. The check runs once and is cached: the mode cannot meaningfully change mid-process,
/// and repeating two syscalls on every log line would be wasted work.
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
            return false;
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
